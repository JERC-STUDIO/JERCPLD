package main

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"os"

	"github.com/klauspost/compress/s2"
)

// .pcap2 (bedrocktool 抓包) 文件布局:
//
//	文件头: "BTCP" | version uint32LE | zipSize uint64LE | zipSize 字节资源包 zip
//	记录循环:
//	  magic    uint32LE == 0xAAAAAAAA
//	  compSize uint32LE
//	  toServer uint8            (1 = c2s 客户端→服务端, 0 = s2c 服务端→客户端)
//	  ts       uint64LE         (UnixMilli)
//	  body     [compSize]byte   (s2 压缩; 解压后 = Header(varuint packetID) + payload)
//	  magic    uint32LE == 0xBBBBBBBB
const (
	pcap2HeaderMagic = "BTCP"
	pcap2Version     = 5

	pcap2MagicStart = uint32(0xAAAAAAAA)
	pcap2MagicEnd   = uint32(0xBBBBBBBB)

	// 单条记录压缩体上限,防止损坏文件触发巨额分配(64 MiB 远超正常包大小)。
	pcap2MaxCompressed = 64 << 20

	// zipSize 跳过时按 int64 处理,超过 int64 上限直接判为文件损坏。
	pcap2MaxInt64 = uint64(1)<<63 - 1
)

// ReadPcap2 解析 bedrocktool 的 .pcap2 抓包文件,返回按出现顺序排列的记录。
//
// 容错约定:
//   - 文件版本不是 5 时不中断解析;解析结束后把版本告警作为 error 与已解析出的
//     记录一并返回(此时 records 有效,调用方可以选择忽略该 error)。
//   - 记录边界上的干净 EOF 视为正常结束。
//   - 记录被截断、magic 不匹配、s2 解压失败等都立即返回错误,错误信息带文件偏移。
func ReadPcap2(path string) ([]Record, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("pcap2: open %q: %w", path, err)
	}
	defer f.Close()

	// off 始终是"下一个待读字节"的文件偏移,所有错误信息都带上它。
	var off int64

	// ---------------- 文件头 ----------------
	var head [4]byte
	if _, err := io.ReadFull(f, head[:]); err != nil {
		return nil, fmt.Errorf("pcap2: read header magic at offset %d: %w", off, err)
	}
	if string(head[:]) != pcap2HeaderMagic {
		return nil, fmt.Errorf("pcap2: bad header magic %q at offset %d (want %q)", string(head[:]), off, pcap2HeaderMagic)
	}
	off += 4

	var version uint32
	if err := binary.Read(f, binary.LittleEndian, &version); err != nil {
		return nil, fmt.Errorf("pcap2: read version at offset %d: %w", off, err)
	}
	off += 4

	var versionErr error
	if version != pcap2Version {
		versionErr = fmt.Errorf("pcap2: unexpected version %d at offset 4 (want %d), continuing in tolerant mode", version, pcap2Version)
	}

	var zipSize uint64
	if err := binary.Read(f, binary.LittleEndian, &zipSize); err != nil {
		return nil, fmt.Errorf("pcap2: read resource zip size at offset %d: %w", off, err)
	}
	off += 8

	if zipSize > pcap2MaxInt64 {
		return nil, fmt.Errorf("pcap2: resource zip size %d at offset %d overflows int64", zipSize, off-8)
	}
	if zipSize > 0 {
		zipStart := off
		n, err := io.CopyN(io.Discard, f, int64(zipSize))
		off += n
		if err != nil {
			return nil, fmt.Errorf("pcap2: skip resource zip (%d bytes) at offset %d, copied %d: %w", zipSize, zipStart, n, err)
		}
	}

	// ---------------- 记录循环 ----------------
	records := make([]Record, 0, 4096)

	for idx := 0; ; idx++ {
		recStart := off

		var recMagic uint32
		if err := binary.Read(f, binary.LittleEndian, &recMagic); err != nil {
			// 记录边界上的干净 EOF = 正常结束;半条 magic 说明文件被截断。
			if errors.Is(err, io.EOF) {
				break
			}
			return nil, fmt.Errorf("pcap2: read record %d magic at offset %d: %w", idx, recStart, err)
		}
		off += 4

		if recMagic != pcap2MagicStart {
			return nil, fmt.Errorf("pcap2: bad record %d magic 0x%08X at offset %d (want 0x%08X)", idx, recMagic, recStart, pcap2MagicStart)
		}

		var compressedSize uint32
		if err := binary.Read(f, binary.LittleEndian, &compressedSize); err != nil {
			return nil, fmt.Errorf("pcap2: read record %d compressed size at offset %d: %w", idx, off, err)
		}
		off += 4

		if compressedSize > pcap2MaxCompressed {
			return nil, fmt.Errorf("pcap2: record %d implausible compressed size %d at offset %d (max %d)", idx, compressedSize, off-4, pcap2MaxCompressed)
		}

		var toServer uint8
		if err := binary.Read(f, binary.LittleEndian, &toServer); err != nil {
			return nil, fmt.Errorf("pcap2: read record %d direction at offset %d: %w", idx, off, err)
		}
		off++

		var ts uint64
		if err := binary.Read(f, binary.LittleEndian, &ts); err != nil {
			return nil, fmt.Errorf("pcap2: read record %d timestamp at offset %d: %w", idx, off, err)
		}
		off += 8

		comp := make([]byte, compressedSize)
		if _, err := io.ReadFull(f, comp); err != nil {
			return nil, fmt.Errorf("pcap2: read record %d body (%d bytes) at offset %d: %w", idx, compressedSize, off, err)
		}
		off += int64(compressedSize)

		var endMagic uint32
		if err := binary.Read(f, binary.LittleEndian, &endMagic); err != nil {
			return nil, fmt.Errorf("pcap2: read record %d trailer magic at offset %d: %w", idx, off, err)
		}
		off += 4

		if endMagic != pcap2MagicEnd {
			return nil, fmt.Errorf("pcap2: bad record %d trailer magic 0x%08X at offset %d (want 0x%08X)", idx, endMagic, off-4, pcap2MagicEnd)
		}

		// s2 解压:解压后第一段是 Header(varuint packetID),其余为 payload。
		raw, err := s2.Decode(nil, comp)
		if err != nil {
			return nil, fmt.Errorf("pcap2: record %d s2 decode (%d bytes) at offset %d: %w", idx, compressedSize, recStart, err)
		}

		c := NewCursor(raw)
		id := c.Uvarint()
		if err := c.Err(); err != nil {
			return nil, fmt.Errorf("pcap2: record %d packet id at offset %d: %w", idx, recStart, err)
		}

		// Payload 必须独立于 raw 存活(调用方可能长期持有),所以拷贝一份。
		var payload []byte
		if n := c.Remaining(); n > 0 {
			payload = append([]byte(nil), c.Bytes(n)...)
		}

		records = append(records, Record{
			Index:     idx,
			ToServer:  toServer == 1,
			Timestamp: int64(ts),
			ID:        id,
			Payload:   payload,
		})
	}

	if versionErr != nil {
		return records, versionErr
	}
	return records, nil
}

// Dir 返回记录方向标签: 客户端→服务端为 "c2s",服务端→客户端为 "s2c"。
func (r Record) Dir() string {
	if r.ToServer {
		return "c2s"
	}
	return "s2c"
}

// pcap2PacketNames 是 Bedrock 协议常见包 ID → 包名映射。
var pcap2PacketNames = map[uint32]string{
	1:   "Login",
	2:   "PlayStatus",
	3:   "ServerToClientHandshake",
	4:   "ClientToServerHandshake",
	5:   "Disconnect",
	6:   "ResourcePacksInfo",
	7:   "ResourcePackStack",
	8:   "ResourcePackClientResponse",
	9:   "Text",
	10:  "SetTime",
	11:  "StartGame",
	12:  "AddPlayer",
	13:  "AddActor",
	14:  "RemoveActor",
	15:  "AddItemActor",
	16:  "TakeItemActor",
	17:  "MoveActorAbsolute",
	18:  "MovePlayer",
	19:  "RiderJump",
	20:  "UpdateBlock",
	21:  "AddPainting",
	22:  "TickSync",
	23:  "LevelEvent",
	24:  "BlockEvent",
	25:  "ActorEvent",
	26:  "MobEffect",
	27:  "UpdateAttributes",
	28:  "InventoryTransaction",
	29:  "MobEquipment",
	30:  "MobArmorEquipment",
	31:  "Interact",
	32:  "BlockPickRequest",
	33:  "ActorPickRequest",
	34:  "PlayerAction",
	35:  "ActorFall",
	36:  "HurtArmor",
	37:  "SetActorData",
	38:  "SetActorMotion",
	39:  "SetActorLink",
	40:  "SetHealth",
	41:  "SetSpawnPosition",
	42:  "Animate",
	43:  "Respawn",
	44:  "ContainerOpen",
	45:  "ContainerClose",
	46:  "PlayerHotbar",
	47:  "InventoryContent",
	48:  "InventorySlot",
	49:  "ContainerSetData",
	50:  "CraftingData",
	51:  "CraftingEvent",
	52:  "GuiDataPickItem",
	53:  "AdventureSettings",
	54:  "BlockActorData",
	55:  "PlayerInput",
	56:  "LevelChunk",
	57:  "SetCommandsEnabled",
	58:  "SetDifficulty",
	59:  "ChangeDimension",
	60:  "SetPlayerGameType",
	61:  "PlayerList",
	62:  "SimpleEvent",
	63:  "Teleport",
	64:  "SpawnExperienceOrb",
	65:  "ClientboundMapItemData",
	66:  "MapInfoRequest",
	67:  "RequestChunkRadius",
	68:  "ChunkRadiusUpdated",
	69:  "GameRulesChanged",
	70:  "Camera",
	71:  "BossEvent",
	72:  "ShowCredits",
	73:  "AvailableCommands",
	74:  "CommandRequest",
	75:  "CommandBlockUpdate",
	76:  "CommandOutput",
	77:  "UpdateTrade",
	78:  "UpdateEquip",
	79:  "ResourcePackDataInfo",
	80:  "ResourcePackChunkData",
	81:  "ResourcePackChunkRequest",
	82:  "Transfer",
	83:  "PlaySound",
	84:  "StopSound",
	85:  "SetTitle",
	86:  "AddBehaviorTree",
	87:  "StructureBlockUpdate",
	88:  "ShowStoreOffer",
	89:  "PurchaseReceipt",
	90:  "PlayerSkin",
	91:  "SubClientLogin",
	92:  "AutomationClientConnect",
	93:  "SetLastHurtBy",
	94:  "BookEdit",
	95:  "NPCRequest",
	96:  "PhotoTransfer",
	97:  "ModalFormRequest",
	98:  "ModalFormResponse",
	99:  "ServerSettingsRequest",
	100: "ServerSettingsResponse",
	101: "ShowProfile",
	102: "SetDefaultGameType",
	103: "RemoveObjective",
	104: "SetDisplayObjective",
	105: "SetScore",
	106: "LabTable",
	107: "UpdateBlockSynced",
	108: "MoveActorDelta",
	109: "SetScoreboardIdentity",
	110: "SetLocalPlayerAsInitialized",
	111: "UpdateSoftEnum",
	112: "NetworkStackLatency",
	113: "ScriptCustomEvent",
	114: "SpawnParticleEffect",
	115: "AvailableActorIdentifiers",
	116: "NetworkChunkPublisherUpdate",
	117: "BiomeDefinitionList",
	118: "LevelSoundEvent",
	119: "LevelEventGeneric",
	120: "LecternUpdate",
	122: "VideoStreamConnect",
	123: "ClientCacheStatus",
	124: "OnScreenTextureAnimation",
	125: "MapCreateLockedCopy",
	126: "StructureTemplateDataRequest",
	127: "StructureTemplateDataResponse",
	128: "ClientCacheBlobStatus",
	129: "ClientCacheMissResponse",
	130: "EducationSettings",
	131: "Emote",
	132: "MultiplayerSettings",
	133: "SettingsCommand",
	134: "AnvilDamage",
	135: "CompletedUsingItem",
	136: "NetworkSettings",
	137: "PlayerAuthInput",
	138: "CreativeContent",
	139: "PlayerEnchantOptions",
	140: "ItemStackRequest",
	141: "ItemStackResponse",
	142: "PlayerArmorDamage",
	143: "CodeBuilder",
	144: "UpdatePlayerGameType",
	145: "EmoteList",
	146: "PositionTrackingDBServerBroadcast",
	147: "PositionTrackingDBClientRequest",
	148: "DebugInfo",
	149: "PacketViolationWarning",
	150: "MotionPredictionHints",
	151: "AnimateEntity",
	152: "CameraShake",
	153: "PlayerFog",
	154: "CorrectPlayerMovePrediction",
	155: "ItemComponent",
	156: "FilterText",
	157: "ClientboundDebugRenderer",
	158: "SyncActorProperty",
	159: "AddVolumeEntity",
	160: "RemoveVolumeEntity",
	161: "SimulationType",
	162: "NpcDialogue",
	163: "EduUriResource",
	164: "CreatePhoto",
	165: "UpdateSubChunkBlocks",
	166: "PhotoInfoRequest",
	167: "SubChunk",
	168: "SubChunkRequest",
	169: "PlayerStartItemCooldown",
	170: "ScriptMessage",
	171: "CodeBuilderSource",
	172: "TickingState",
	173: "TickingAreasLoadStatus",
	174: "DimensionData",
	175: "AgentAction",
	176: "ChangeMobProperty",
	177: "LessonProgress",
	178: "RequestAbility",
	179: "RequestPermissions",
	180: "ToastRequest",
	181: "UpdateAbilities",
	182: "UpdateAdventureSettings",
	183: "DeathInfo",
	184: "EditorNetwork",
	185: "FeatureRegistry",
	186: "ServerStats",
	187: "RequestNetworkSettings",
	188: "GameTestRequest",
	189: "GameTestResults",
	190: "UpdateClientInputLocks",
	191: "CameraPresets",
	192: "UnlockedRecipes",
	193: "CameraInstruction",
	194: "CompressedBiomeDefinitionList",
	195: "TrimData",
	196: "OpenSign",
	197: "AgentAnimation",
	198: "RefreshEntitlements",
}

// PacketName 返回 Bedrock 包 ID 对应的可读名字,未知 ID 返回空字符串。
func PacketName(id uint32) string {
	return pcap2PacketNames[id]
}

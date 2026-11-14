package main

import "fmt"

// JERCPLD 关注的包 ID。
//
// 注意:这些编号以 JERCPLD 自己的 docs/PROTOCOL_1001.md 为准。
// 基岩版包 ID 会随版本前移(pcap2.go 里的 pcap2PacketNames 是较老的表,
// 其中 19=RiderJump、29=MobEquipment),而 JERCPLD 在 1.26.x 上观察到的是
// move_player=19、mob_equipment=31、mob_armor_equipment=32。
// 冲突时以本文件的常量为准,并在报告里同时打印两个名字供核对。
const (
	PktAddPlayer         = 12
	PktAddEntity         = 13
	PktAddItemEntity     = 15
	PktMovePlayer        = 19
	PktMobEquipment      = 31
	PktMobArmorEquipment = 32
)

// jerCPLDPacketNames 覆盖 pcap2PacketNames 中与 1.26.x 不一致的条目。
var jerCPLDPacketNames = map[uint32]string{
	PktAddPlayer:         "add_player",
	PktAddEntity:         "add_entity",
	PktAddItemEntity:     "add_item_entity",
	PktMovePlayer:        "move_player",
	PktMobEquipment:      "mob_equipment",
	PktMobArmorEquipment: "mob_armor_equipment",
}

// pktName 优先返回 JERCPLD 视角的包名,未知时回落到 pcap2 的通用表。
func pktName(id uint32) string {
	if n, ok := jerCPLDPacketNames[id]; ok {
		return n
	}
	return PacketName(id)
}

// ---------------------------------------------------------------------------
// Item 线格式
// ---------------------------------------------------------------------------

// ItemSpec 描述一种 Item 编码变体。
//
// 1001 (26.30+) 与 2168 (1.26.40+) 的差异集中在三处:
//   - network_id:1001 是 zigzag32 varint,2168 是 li16 定长
//   - air 短路:1001 在 network_id==0 时立即结束,2168 继续写满尾部字段
//   - block_runtime_id:PROTOCOL_1001.md 一处写 zigzag32、一处写"对非负值编码相同",
//     自相矛盾;这里默认两边都按无符号 varint 读(BlockIDZig=false)。
//     air 时该字段恒为 0,两种读法都是 1 字节,不影响 air 长度判定。
type ItemSpec struct {
	Label           string
	NetworkIDLi16   bool // network_id 是否为 li16 定长
	NetworkIDZig    bool // varint 时是否 zigzag
	CountLE         bool // count 是否为 lu16(否则 varint)
	BlockIDZig      bool // block_runtime_id 是否 zigzag
	AirShortCircuit bool // network_id==0 时是否立即结束
	HasStackFlag    bool // 是否有 has_stack_id 字节
}

var (
	// spec1001 对应 26.30 / 26.31 / 26.32 / 26.33 客户端的 Item。
	spec1001 = ItemSpec{
		Label:           "1001",
		NetworkIDLi16:   false,
		NetworkIDZig:    true,
		CountLE:         true,
		BlockIDZig:      false,
		AirShortCircuit: true,
		HasStackFlag:    true,
	}
	// spec2168 对应 1.26.40+ 服务端的 ItemV4。
	spec2168 = ItemSpec{
		Label:           "2168",
		NetworkIDLi16:   true,
		NetworkIDZig:    false,
		CountLE:         true,
		BlockIDZig:      false,
		AirShortCircuit: false,
		HasStackFlag:    true,
	}
)

// parseItem 按 spec 解析一个 Item,返回消耗字节数与是否为 air。
func parseItem(c *Cursor, spec ItemSpec) (n int, air bool, err error) {
	start := c.Pos()

	var id int64
	switch {
	case spec.NetworkIDLi16:
		id = int64(c.I16LE())
	case spec.NetworkIDZig:
		id = int64(c.Varint())
	default:
		id = int64(c.Uvarint())
	}
	if c.Err() != nil {
		return 0, false, c.Err()
	}

	if id == 0 && spec.AirShortCircuit {
		return c.Pos() - start, true, nil
	}

	if spec.CountLE {
		c.U16LE()
	} else {
		c.Uvarint()
	}
	c.Varint() // metadata
	if spec.HasStackFlag {
		if c.U8() != 0 {
			c.Varint() // stack_id
		}
	}
	if spec.BlockIDZig {
		c.Varint()
	} else {
		c.Uvarint()
	}
	extraLen := c.Uvarint()
	if c.Err() != nil {
		return 0, false, c.Err()
	}
	if extraLen > 0 {
		c.Skip(int(extraLen))
	}
	if c.Err() != nil {
		return 0, false, c.Err()
	}
	return c.Pos() - start, id == 0, nil
}

// ---------------------------------------------------------------------------
// metadata 字典
// ---------------------------------------------------------------------------

// MetaSpec 描述 metadata 条目编码变体。
type MetaSpec struct {
	Label      string
	LegacyType bool // type 之后是否多 1 字节 legacy_type
}

var (
	meta1001 = MetaSpec{Label: "1001", LegacyType: false}
	meta2168 = MetaSpec{Label: "2168", LegacyType: true}
)

// readMetaEntry 读一个 metadata 条目:key + type [+ legacy_type] + value。
func readMetaEntry(c *Cursor, spec MetaSpec) error {
	c.Uvarint() // key
	typ := c.Uvarint()
	if c.Err() != nil {
		return c.Err()
	}
	if spec.LegacyType {
		c.U8()
	}
	return readMetaValue(c, typ)
}

// readMetaValue 按 Bedrock MetadataType 读 value。
func readMetaValue(c *Cursor, typ uint32) error {
	switch typ {
	case 0: // byte
		c.U8()
	case 1: // short
		c.I16LE()
	case 2: // int
		c.I32LE()
	case 3: // float
		c.F32()
	case 4: // string
		c.String()
	case 5: // compound:NBT payload,无外层 tag/name
		return skipNBTCompound(c)
	case 6: // vec3i
		c.I32LE()
		c.I32LE()
		c.I32LE()
	case 7: // long
		c.Skip(8)
	case 8: // vec3f
		c.F32()
		c.F32()
		c.F32()
	default:
		return fmt.Errorf("%w: type=%d at %d", errBadType, typ, c.Pos())
	}
	return c.Err()
}

// parseMetadata 解析一个 metadata 字典,返回条目数。
func parseMetadata(c *Cursor, spec MetaSpec) (int, error) {
	count := int(c.Uvarint())
	if c.Err() != nil {
		return 0, c.Err()
	}
	if count < 0 || count > 4096 {
		return 0, fmt.Errorf("metadata: count %d out of range", count)
	}
	for i := 0; i < count; i++ {
		if err := readMetaEntry(c, spec); err != nil {
			return i, err
		}
	}
	return count, c.Err()
}

// ---------------------------------------------------------------------------
// NBT(仅跳过,不解析内容)
// ---------------------------------------------------------------------------

// skipNBTCompound 跳过一个 compound 的 payload(不含外层 tag 与 name)。
func skipNBTCompound(c *Cursor) error {
	for {
		t := c.U8()
		if c.Err() != nil {
			return c.Err()
		}
		if t == 0 {
			return nil
		}
		n := int(c.U16LE())
		c.Skip(n)
		if err := skipNBTPayload(c, t); err != nil {
			return err
		}
	}
}

// skipNBTPayload 按 tag 类型跳过 NBT payload。
func skipNBTPayload(c *Cursor, tag uint8) error {
	switch tag {
	case 0: // end
		return nil
	case 1: // byte
		c.U8()
	case 2: // short
		c.I16LE()
	case 3: // int
		c.I32LE()
	case 4: // long
		c.Skip(8)
	case 5: // float
		c.F32()
	case 6: // double
		c.Skip(8)
	case 7: // byte array
		n := int(c.I32LE())
		if n < 0 {
			return fmt.Errorf("nbt: negative byte array length %d", n)
		}
		c.Skip(n)
	case 8: // string
		n := int(c.U16LE())
		c.Skip(n)
	case 9: // list
		elem := c.U8()
		n := int(c.I32LE())
		if n < 0 {
			return fmt.Errorf("nbt: negative list length %d", n)
		}
		for i := 0; i < n; i++ {
			if err := skipNBTPayload(c, elem); err != nil {
				return err
			}
		}
	case 10: // compound
		return skipNBTCompound(c)
	case 11: // int array
		n := int(c.I32LE())
		if n < 0 {
			return fmt.Errorf("nbt: negative int array length %d", n)
		}
		c.Skip(n * 4)
	case 12: // long array
		n := int(c.I32LE())
		if n < 0 {
			return fmt.Errorf("nbt: negative long array length %d", n)
		}
		c.Skip(n * 8)
	default:
		return fmt.Errorf("nbt: unknown tag %d at %d", tag, c.Pos())
	}
	return c.Err()
}

// ---------------------------------------------------------------------------
// 包级解析
// ---------------------------------------------------------------------------

// move_player 的 mode 枚举。
const (
	moveModeNormal   = 0
	moveModeReset    = 1
	moveModeTeleport = 2
	moveModePitch    = 3
)

// parseMovePlayer 解析 move_player。
//
// teleportOptional 为 true 表示 teleport 段前有 1 字节 bool 前缀(option 语义);
// 为 false 表示直接读 cause + source_entity_type(switch 语义)。
func parseMovePlayer(c *Cursor, teleportOptional bool) (mode int32, consumed int, err error) {
	start := c.Pos()

	c.Varint64() // runtime_id
	c.F32()      // x
	c.F32()      // y
	c.F32()      // z
	c.F32()      // pitch
	c.F32()      // yaw
	c.F32()      // head_yaw
	mode = c.Varint()
	c.Bool()     // on_ground
	c.Varint64() // ridden_runtime_id
	if c.Err() != nil {
		return 0, 0, c.Err()
	}

	if mode == moveModeTeleport {
		if teleportOptional {
			if c.Bool() {
				c.I32LE()  // cause
				c.Varint() // source_entity_type
			}
		} else {
			c.I32LE()  // cause
			c.Varint() // source_entity_type
		}
	}
	c.Varint64() // tick
	if c.Err() != nil {
		return mode, 0, c.Err()
	}
	return mode, c.Pos() - start, nil
}

// parseMobEquipment 解析 mob_equipment,返回 item 消耗字节数与是否 air。
//
// 结构:runtime_id(varlong) + item + slot(u8) + selected_slot(u8) + window_id(u8)
// item 之后只有 3 个定长字节,因此最适合判定 air 长度。
func parseMobEquipment(c *Cursor, spec ItemSpec) (itemLen int, air bool, err error) {
	c.Varint64() // runtime_id
	itemLen, air, err = parseItem(c, spec)
	if err != nil {
		return 0, false, err
	}
	c.U8() // slot
	c.U8() // selected_slot
	c.U8() // window_id
	return itemLen, air, c.Err()
}

// parseMobArmorEquipment 解析 mob_armor_equipment,返回 item 总字节数与 air 个数。
//
// 结构:runtime_id(varlong) + helmet + chestplate + leggings + boots
func parseMobArmorEquipment(c *Cursor, spec ItemSpec) (itemBytes int, airCount int, err error) {
	c.Varint64() // runtime_id
	for i := 0; i < 4; i++ {
		n, air, e := parseItem(c, spec)
		if e != nil {
			return itemBytes, airCount, e
		}
		itemBytes += n
		if air {
			airCount++
		}
	}
	return itemBytes, airCount, c.Err()
}

// parseAddItemEntity 解析 add_item_entity 的前半段。
//
// 结构:entity_runtime_id(varlong) + item + position(vec3f) + entity_metadata
func parseAddItemEntity(c *Cursor, ispec ItemSpec, mspec MetaSpec) (itemLen int, air bool, err error) {
	c.Varint64() // entity_runtime_id
	itemLen, air, err = parseItem(c, ispec)
	if err != nil {
		return 0, false, err
	}
	c.F32() // x
	c.F32() // y
	c.F32() // z
	if _, err = parseMetadata(c, mspec); err != nil {
		return itemLen, air, err
	}
	return itemLen, air, c.Err()
}

// parseEquipByID 按包 ID 分派到对应的装备解析函数。
func parseEquipByID(id uint32, c *Cursor, spec ItemSpec) (itemLen int, airCount int, err error) {
	switch id {
	case PktMobEquipment:
		var air bool
		itemLen, air, err = parseMobEquipment(c, spec)
		if air {
			airCount = 1
		}
	case PktMobArmorEquipment:
		itemLen, airCount, err = parseMobArmorEquipment(c, spec)
	default:
		err = fmt.Errorf("parseEquipByID: 不支持的包 ID %d", id)
	}
	return
}

// ---------------------------------------------------------------------------
// metadata 扫描
// ---------------------------------------------------------------------------

// MetaHit 是一次 metadata 扫描命中。
type MetaHit struct {
	Offset  int // metadata 起点(相对包体)
	Count   int // 条目数
	End     int // metadata 结束位置(不含)
	TailLen int // End 之后剩余字节
}

// scanMetadata 在整包里扫描所有可能的 metadata 起点。
//
// 用途:当包的前缀字段顺序不确定时(例如 add_entity 的 attributes 是否带
// body_yaw),不依赖前缀计算,而是穷举每个位置尝试解析,由调用方比较
// 两套 spec 的命中结果。
func scanMetadata(b []byte, spec MetaSpec) []MetaHit {
	var hits []MetaHit
	for p := 0; p < len(b); p++ {
		c := NewCursor(b[p:])
		count := int(c.Uvarint())
		if c.Err() != nil {
			continue
		}
		if count <= 0 || count > 256 {
			continue
		}
		ok := true
		for i := 0; i < count; i++ {
			if err := readMetaEntry(c, spec); err != nil {
				ok = false
				break
			}
		}
		if !ok {
			continue
		}
		end := p + c.Pos()
		hits = append(hits, MetaHit{
			Offset:  p,
			Count:   count,
			End:     end,
			TailLen: len(b) - end,
		})
	}
	return hits
}

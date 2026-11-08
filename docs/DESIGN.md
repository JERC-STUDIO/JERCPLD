# JERCPLD 方案说明（骨架 + P1 桌面应用）

JERCPLD 是一个纯 C# 的 Minecraft 基岩版跨版本翻译代理：

- 目标环境：**26.60 客户端 ↔ 26.50 服务端，双向互通，两侧均走 NetherNet**；
- 旧版 RakNet 监听保留为诊断路径（该版本对不依赖它）；
- 中间把 Bedrock 协议包翻译成目标版本。

当前仓库完成**骨架 + P1 桌面应用**：RakNet 最小监听/握手可运行，Avalonia 桌面应用可启停监听并显示会话/统计/日志，NetherNet 只有接口与占位实现，版本翻译只有路由与直通适配器。所有未实现内容都在代码中以 `TODO(...)` 明确标注，不做静默兜底。

整体阶段计划见 [ROADMAP.md](./ROADMAP.md)。

---

## 1. 范围与非目标

### 本阶段已做

1. .NET 8 解决方案与 9 个项目划分，可编译通过（0 警告 0 错误）。
2. RakNet 监听骨架：
   - 回应 `UnconnectedPing` / `UnconnectedPingOpenConnections`；
   - `OpenConnectionRequest1/2` 与 `OpenConnectionReply1/2` 的 MTU 协商；
   - `ConnectionRequest -> ConnectionRequestAccepted -> NewIncomingConnection` 握手；
   - 可靠有序帧的基本收发、ACK 生成、0xFE 游戏包批次提取。
3. NetherNet 传输接口 + 占位实现，明确后续 `ArkMirage.NetherNet` 原型 → `libwebrtc` 生产的替换路线。
4. 版本映射层：星型适配器接口 + 相邻版本链式步骤接口 + 路由/执行器 + 直通适配器。
5. 抓包调试工具 `jercpld-dump`：离线解析 RakNet 离线包、数据报、ACK、Bedrock 批次。
6. 本文档：分层、数据流、IR 定义、新增版本改动清单。

### 明确不做（不是"已完成"）

- RakNet 加密、分片重组、NAK/重传、拥塞控制、IPv6；
- NetherNet 信令 / ICE / DTLS / DataChannel / BDS 认证；
- 任何真实版本的字段级协议翻译；
- Bedrock 压缩算法协商（zlib/snappy 选择）、加密握手；
- Xbox Live / 账号认证。

---

## 2. 分层与项目结构

```
JERCPLD.sln
├── Directory.Build.props
├── global.json
├── docs/DESIGN.md                    本文件
├── src/
│   ├── JERCPLD.Core/                 公共基础：协议版本、IR、日志、传输契约、抓包观察点
│   ├── JERCPLD.Protocol/             纯数据模型：RakNet 包/地址/帧、Bedrock 包模型与 ID
│   ├── JERCPLD.Codec/                编解码：二进制原语、RakNet 帧/数据报/离线包/握手、Bedrock 批次
│   ├── JERCPLD.RakNet/               RakNet UDP 监听器与会话状态机
│   ├── JERCPLD.NetherNet/            NetherNet 上游接口与占位实现
│   ├── JERCPLD.VersionMap/           星型适配器、相邻链式步骤、路由与翻译执行
│   ├── JERCPLD.Proxy/                主流程：客户端会话 <-> 版本翻译 <-> 上游会话
│   └── JERCPLD.Cli/                  主程序 jercpld
└── tools/
    └── JERCPLD.PacketDump/           抓包调试工具 jercpld-dump
```

依赖方向（无环）：

```
Core  <-  Protocol  <-  Codec  <-  RakNet
  ^                        ^         ^
  |                        |         |
  +---- NetherNet          +--- VersionMap ---> Proxy ---> Cli
```

- `Core` 不依赖任何项目。
- `Protocol` 不依赖 `Core`，只放可序列化模型。
- `Codec` 依赖 `Protocol`。
- `RakNet` 依赖 `Core + Protocol + Codec`。
- `NetherNet` 只依赖 `Core`（传输契约）。
- `VersionMap` 依赖 `Core + Protocol + Codec`。
- `Proxy` 依赖上述全部；`Cli` 只做参数解析与组装。

### 为什么这样切

- **模型与编解码分离**：包模型可被版本适配器、抓包工具、测试共同引用，不绑定某一种编解码实现。
- **传输与翻译分离**：RakNet / NetherNet 都只提供"一批 Bedrock 游戏包字节"，翻译层完全不知道底层是 UDP 还是 WebRTC。
- **IR 在 Core**：版本映射、抓包诊断、未来可视化工具都能使用同一套中间表示。

---

## 3. 数据流

### 3.1 连接与握手

```
旧客户端 --UDP--> RakNetServer
  UnconnectedPing        -> UnconnectedPong(MOTD, 协议号=客户端版本)
  OpenConnectionRequest1 -> OpenConnectionReply1(MTU)
  OpenConnectionRequest2 -> OpenConnectionReply2 + 创建 RakNetSession
  ConnectionRequest      -> ConnectionRequestAccepted
  NewIncomingConnection  -> 会话 established
```

`RakNetSession` 完成握手后触发 `Established`，`RakNetServer` 把它交给 `TranslationProxy.HandleClientAsync`。

### 3.2 游戏包转发

```
客户端
  | RakNet 可靠有序帧 [0xFE | varint 长度 | 批次载荷]
  v
RakNetSession
  | 提取批次载荷（GamePacketReceived）
  v
TranslationProxy
  | 1) BedrockBatchCodec.DecodeBatch
  | 2) 每个包：VersionMapRouter.Translate(上行路由)
  | 3) BedrockBatchCodec.EncodeBatch
  v
IServerSession (NetherNet，占位)
  | NetherNet DataChannel 字节流
  v
新版 BDS
```

下行方向对称，使用反向路由。

### 3.3 一次包翻译的内部路径

```
源版本原始包（varint packetId + body）
  -> [PreChain]  相邻版本链式步骤（仅当源版本无星型适配器）
  -> 源锚点 StarAdapter.Decode
  -> IR（IrPacket）
  -> 目标锚点 StarAdapter.Encode
  -> [PostChain] 相邻版本链式步骤（仅当目标版本无星型适配器）
  -> 目标版本原始包
```

注意：`PreChain/PostChain` 操作的是**线格式包**，与 IR 无关；IR 只在两个星型锚点之间流转。

---

## 4. 中间表示（IR）

定义位置：`src/JERCPLD.Core/Ir/`。

### 4.1 值模型

`IrValue` 是刻意极小的类型树：

| 类型 | 用途 |
| --- | --- |
| `IrNull` | 空值 / 未设置 |
| `IrInt(long)` | 所有整数（含枚举、布尔以外的数值） |
| `IrFloat(double)` | 浮点（坐标、旋转等） |
| `IrString(string)` | 文本、标识符 |
| `IrBool(bool)` | 布尔 |
| `IrBytes(byte[])` | 不透明字节串（NBT、skin、UUID 等） |
| `IrList(ReadOnlyList<IrValue>)` | 数组、重复字段 |
| `IrMap(ReadOnlyDictionary<string, IrValue>)` | 结构化对象 |

选择理由：

- 不引入反射/序列化库，纯 BCL，便于逐包手工实现；
- 可精确保留未知字段（见 4.3）；
- 能直接打印成 JSON 形态做抓包对比（工具可自行实现）。

### 4.2 `IrPacket`

```csharp
public sealed record IrPacket(
    string TypeId,              // 规范化类型名，如 "login"、"start_game"
    IrMap Fields,               // 规范化字段名（snake_case）-> IrValue
    ProtocolVersion SourceVersion,
    byte[]? RawPassthrough);    // 无法理解但需原样透传的完整包
```

约定：

1. `TypeId` 与数字包 ID 解耦。数字 ID 随版本变化，`TypeId` 保持稳定。
2. 字段名统一 `snake_case`，例如 `entity_unique_id`、`platform_chat_id`。
3. 适配器负责"版本字段名 -> 规范字段名"的映射；IR 中只出现规范名。
4. IR 不承载压缩、加密、RakNet 帧信息。压缩/加密属于传输层，版本映射只关心包内容。
5. `RawPassthrough` 只作为无法解析时的逃生通道，必须同时保留可诊断信息，不能用来假装已翻译。

### 4.3 未知包与未知字段策略

- 适配器遇到不支持的包 ID：
  - `Supports(id)` 返回 false；
  - 路由器默认**丢弃**并记录原因；
  - 显式开启 `PassUnknownPackets` 时原样透传，但必须打印警告（目标版本未必接受）。
- 适配器遇到不认识的字段：写入 `Fields` 的 `_unknown` map，编码回原版本时原样写回；跨版本编码时可携带或丢弃，由适配器显式决定。
- 不允许"字段缺失就填默认值"的隐式行为；默认值必须由适配器显式声明。

---

## 5. 版本映射层

### 5.1 星型为主

每个版本实现一个 `IVersionStarAdapter`：

```csharp
public interface IVersionStarAdapter
{
    ProtocolVersion Version { get; }
    string Name { get; }
    bool Supports(int packetId);
    IrPacket Decode(BedrockPacketFrame frame);   // 版本 -> IR
    BedrockPacketFrame Encode(IrPacket packet);  // IR -> 版本
}
```

N 个版本只需要 N 个适配器，而不是 N² 个版本对；新增版本只增加一条星型边。

### 5.2 相邻版本链式兜底

```csharp
public interface IAdjacentVersionStep
{
    ProtocolVersion From { get; }
    ProtocolVersion To { get; }                 // 必须 From + 1
    IReadOnlyCollection<int> AffectedPacketIds { get; }
    ReadOnlyMemory<byte> Upgrade(ReadOnlyMemory<byte> rawPacket);    // From -> To
    ReadOnlyMemory<byte> Downgrade(ReadOnlyMemory<byte> rawPacket);  // To -> From
}
```

用途：当某个中间版本差异极小（只是包 ID 偏移、某字段新增/删除）且不值得单独写星型适配器时，用相邻步骤把原始包修正到最近的锚点。

边界：

- 链式步骤只做"线格式增量修正"，不产生 IR；
- 必须连续：`N -> N+1` 的步骤缺失时路由直接失败；
- 差异较大（包结构重排、字段语义变化）时必须新增星型适配器，禁止继续堆链式步骤。

### 5.3 路由算法

`VersionMapRouter.TryCreateRoute(from, to, out reason)`：

1. 若 `from == to`：返回失败（不需要翻译）。
2. 找源锚点：
   - 有精确适配器 -> 直接使用，`PreChain` 为空；
   - 否则按"距离最近、距离相同优先低版本"寻找锚点，并要求存在连续的相邻步骤链。
3. 找目标锚点：同上，生成 `PostChain`（锚点 -> 目标版本）。
4. 任一步失败 -> 返回 null，并给出具体原因（例如"缺少相邻版本步骤 686->687"）。

执行 `Translate(route, rawPacket)`：

1. 依次执行 `PreChain`；
2. 解析出 `packetId`；
3. 源适配器不支持 -> 按策略丢弃/透传；
4. `Decode` 到 IR -> 目标适配器 `Encode`；
5. 依次执行 `PostChain`；
6. 返回 `Translated / PassedThrough / Dropped` 和原因。

### 5.4 当前注册情况

骨架**没有注册任何真实星型适配器**，因此默认 `serve` 会明确提示"没有可用的版本翻译路径"。

`--passthrough` 会注册 `PassthroughVersionAdapter`：它把整个包 ID + 包体塞进 IR 再取出，仅用于验证
"RakNet -> Proxy -> VersionMap -> NetherNet"管线连通性，**不提供任何版本翻译能力**。

---

## 6. NetherNet 传输方案

接口定义在 `src/JERCPLD.Core/Transport/TransportContracts.cs`，实现位于 `src/JERCPLD.NetherNet/`。

### 6.1 替换路线

1. **原型阶段：ArkMirage.NetherNet**
   - 目标：跑通 NetherNet 信令、WebRTC 连接、DataChannel、BDS 认证与游戏包收发。
   - 产出：一个实现 `IUpstreamTransport` / `IServerSession` 的 `ArkMirageNetherNetTransport`。
2. **生产阶段：libwebrtc**
   - 保持同样的接口，替换内部 WebRTC 实现。
   - 上层 `TranslationProxy`、`VersionMap`、`RakNet` 无需改动。
   - 需要处理原生库部署、平台差异、生命周期与线程模型。

### 6.2 当前实现（ArkMirage）

`src/JERCPLD.NetherNet/` 已实现：

| 类型 | 作用 |
| --- | --- |
| `ArkMirageNetherNetServer` | NetherNet 监听端，接受 `Conn` 并适配为 `IClientSession` |
| `ArkMirageNetherNetTransport` | NetherNet 上行端，支持 HTTP 信令与局域网发现两种方式 |
| `LanDiscoverySignaling` | 服务端侧 UDP 7551 局域网发现信令 |
| `LanDiscoveryClientSignaling` | 客户端侧 UDP 7551 发现 + 信令，用于连接真实 BDS |

已验证：

1. 本机 HTTP 信令回环连接成功，6 字节消息双向回显耗时约 26ms。
2. **真实 BDS 1.26.51（协议 2193）**：通过 UDP 7551 发现（NetworkID `0x757128262F3F18BB`），
   WebRTC 建连成功，发送 RequestNetworkSettings 后收到 NetworkSettings（阈值 1、zlib）。
3. **完整代理链路**：模拟 26.60 客户端 → 代理 → 真实 BDS 26.51，
   RequestNetworkSettings 改写为 2193 后得到 NetworkSettings，并原样跨版本返回；
   统计 translated=3、dropped=0、decodeErrors=0。

线格式要点：NetherNet 的 DataChannel 消息直接承载 Bedrock 批次（`VarUInt 包长 | 包 ID | 包体`），
**没有 RakNet 的 0xFE 外层包装**；`ArkMirageSession` 因此直接把消息作为批次交给 Proxy。

### 6.3 未实现清单

- 加密握手（ServerToClientHandshake / ClientToServerHandshake）；
- 真实客户端身份链验证（当前代理原样透传 JWT，不校验、不伪造）；
- Snappy 压缩；
- 断线重连、拥塞与流量统计；
- libwebrtc 生产后端替换。

---

## 6A. 2193 ↔ 2225 翻译现状

实现：`src/JERCPLD.VersionMap/Adapters/LoginStartGameAdapter.cs` 与 `StartGameWireRewriter.cs`。

| 包 | 处理 | 依据 |
| --- | --- | --- |
| Login (1) | 改写 Client Network Version（2225↔2193） | 官方 Login 包：int32 Big Endian |
| RequestNetworkSettings (193) | 改写 Client Network Version | 官方包定义 |
| StartGame (11) | 插入/移除 LevelSettings 的 `Editor Level Migration Version`；重排 GameType 枚举 | 官方 1.26.50/1.26.60 LevelSettings、GameType |
| NetworkSettings (143) | 翻译 CompressionAlgorithm 的 None（2193=2，2225=65535） | 官方类型变更记录 |
| 其余包 | 原样透传，UI 计入「透传」 | 尚未实现字段翻译 |

压缩：`TranslationProxy` 按会话维护 `SessionCompressionState`，从 NetworkSettings 读取阈值与算法；
zlib 可解压/重压缩，Snappy 明确报错并丢弃（不静默假装成功）。

待补：区块/SubChunk/方块调色板、实体元数据、物品注册表、表单，以及 2225 新增内容的降级映射
（endweave fallback：disc_press→jukebox、ice_crystal→amethyst_cluster、icicle→pointed_dripstone、
ice_caves→dripstone_caves、frostbite→zombie、ice_ball→snowball、freezing→38）。

---

## 6B. 加密握手（已实现密码学层）

代码：

| 文件 | 作用 |
| --- | --- |
| `Codec/Bedrock/Encryption/AesCfb8.cs` | AES-256-CFB8 流加密（双向独立状态） |
| `Codec/Bedrock/Encryption/BedrockEncryptionKeys.cs` | ECDH P-384、SHA256(salt‖secret) 派生 |
| `Codec/Bedrock/Encryption/BedrockIdentityKey.cs` | 同一 P-384 私钥用于 ES384 签名与 ECDH；可生成离线自签名 Login |
| `Codec/Bedrock/Encryption/BedrockJwt.cs` | JWT 拆包、Base64Url、x5u 提取 |
| `Codec/Bedrock/Encryption/BedrockHandshakeCodec.cs` | ServerToClient/ClientToServer 握手包、Login 公钥提取 |
| `Proxy/Encryption/BedrockEncryptionBridge.cs` | 客户端侧下发握手 + 上游侧接受握手 + 双端加解密 |

流程：

1. 客户端 Login 中取出客户端公钥；
2. 代理扮演服务端，生成自己的 P-384 密钥与 salt，向客户端发 ServerToClientHandshake；
3. 客户端回 ClientToServerHandshake 后，代理启用客户端方向 CFB8；
4. 代理扮演客户端连接 BDS：BDS 发 ServerToClientHandshake，代理用**自己的身份私钥**派生上游密钥，回 ClientToServerHandshake 后启用上游方向 CFB8。

限制：第 4 步要求 BDS 接受代理的 Login 身份。在线模式必须是真实 Xbox 身份链；没有账号时只能用于
`online-mode=false` 的 BDS。密码学层已通过离线双向测试，但代理接线暂未启用，以免在没有身份的情况下破坏现有链路。

## 6C. 内容降级映射

`VersionMap/Content/ContentFallbackMap.cs` + `Content/fallbacks/v2225_to_v2193.json`：

- 方块：disc_press→jukebox、ice_crystal→amethyst_cluster、icicle→pointed_dripstone；
- 群系：ice_caves→dripstone_caves；
- 实体：frostbite→zombie、ice_ball→snowball；
- 状态效果：freezing→38。

已通过单元测试；尚未接入具体包（需要 NBT / 注册表包解析）。

## 6D. NBT、方块状态表与区块翻译

### NBT

`Codec/Nbt/` 实现 Bedrock NBT 的两种编码：

- `NbtEncoding.LittleEndian`：字符串长度 uint16、数组/列表长度 uint32；
- `NbtEncoding.NetworkLittleEndian`：字符串长度 varuint、数组/列表长度 varint32。

支持 Byte/Short/Int/Long/Float/Double/ByteArray/String/List/Compound/IntArray/LongArray，读写往返已测试。

### 方块状态哈希

`Codec/Nbt/BlockStateHash.cs`：把 `{name, states}` 序列化为 NBT（states 键按序数排序），再做 FNV-1a 32。

生成工具：`tools/JERCPLD.BlockTableGen`。

```powershell
jercpld-blocktable data/vanilla/1.26.50/mojang-blocks.json 2193 data/generated/block_states.2193.json
jercpld-blocktable data/vanilla/1.26.60/mojang-blocks.json 2225 data/generated/block_states.2225.json
```

结果：

| 协议 | 方块数 | 状态数 | 哈希冲突 |
| --- | --- | --- | --- |
| 2193 | 1463 | 22189 | 0 |
| 2225 | 1466 | 24058 | 0 |

2225→2193 映射：exact 22156（92.09%）、fallback 6、filtered 1896、default 0、unmapped 0。

filtered 的主要来源是旧版本没有新版本新增的连接位状态：
- redstone_wire：2225 有 redstone_east/north/south/west，2193 只有 redstone_signal，旧客户端自行推导连接；
- fire / chorus_plant：2225 有 connection_* 状态，2193 没有。

这些不是丢信息，而是旧版本客户端会自行计算。`default` 已降为 0，表示没有状态被无条件退化为首个状态。

数据来源：Mojang `bedrock-samples`
- `v1.26.50.4/metadata/vanilladata_modules/mojang-blocks.json`
- `main`（1.26.60.5）同路径。

**尚未完成**：用真实 BDS StartGame 里的方块表交叉验证哈希实现。该步骤需要完成登录（账号/加密），当前记为阻塞。

### 区块

- `LevelChunk`：2225 末尾新增 `Is Client Biome Update`，已实现增删；
  内部 Serialized Chunk Data 会按 `SubChunkCount` 逐个子区块调用地形重写器。
- `SubChunk`：`SubChunk Request Result` 枚举整体 +1，已实现双向映射；
  `Serialized Sub Chunk` 的地形调色板按 NBT 解析，使用内容降级表改写方块名称，保留状态与索引。
- 版本 8/9 地形格式（版本字节、层数、Y、层头 bits/type、索引、调色板、NBT）已实现，合成数据测试通过。
- 解析失败时回退为原始包/仅信封改写，避免破坏未知布局。

### 哈希校验状态

官方 `mojang-blocks.json` 不包含运行时哈希，只有 `name`、`properties`、`raw_id`、`serialization_id`。
因此当前生成表无法离线交叉验证，必须用真实 BDS StartGame 的方块表核对；该步骤需要完成登录，已记为阻塞。

## 6E. 物品栈与实体元数据

- `Codec/Bedrock/Items/NetworkItemStackCodec.cs`：networkId / count / metadata / blockRuntimeId / NBT 往返读写。
  布局依据社区实现，尚未用真实 2193/2225 包体验证。
- `Codec/Bedrock/Metadata/EntityMetadataCodec.cs`：按官方 `SynchedActorDataList`（count + ID + Type + Value）实现 9 种类型。
- `Codec/Bedrock/Metadata/MetadataSnapshot.cs`：
  - 补默认槽：目标版本要求但源数据缺失的槽用默认值补齐；
  - 位图压紧：存在槽压成位图，0x7F 终止；
  - 需要时再展开为 SynchedActorDataList。
- 上述编解码已通过单元测试；SetActorData 已接入，InventorySlot 当前按透传处理。

### 接入情况

- `SetActorData` 已接入：`Content/metadata/v2225_to_v2193.json`、`v2193_to_v2225.json` 提供重映射与默认槽，
  `SetActorDataWireRewriter` 解析 runtime ID 后的元数据列表并重写，其余字段原样保留。
  两张槽位表都标记 `verified=false`，上线前必须用真实包体校正。
- `InventorySlot` / `NetworkItemStackDescriptor`：官方 changelog 显示该类型在 2193↔2225 无结构变更，
  当前按透传处理；物品运行时 ID 由服务端 ItemRegistry 分配，不需要静态 ID 表。
- 区块调色板降级时若目标方块状态集合未知，会清空 `states`，避免把源方块独有状态带给不认识的方块；
  后续可接入目标 BlockStateTable 做状态过滤以保留 icicle 等可识别状态。

## 6F. 传输层替换边界

`JERCPLD.Proxy` 的 csproj 已移除对 `RakNet` / `NetherNet` 的引用，只依赖
`Core`、`Protocol`、`Codec`、`VersionMap`。
因此替换 GPL 的 ArkMirage 时，只需重写 `JERCPLD.NetherNet` 内部的
`IUpstreamTransport` / `IClientTransportServer` 实现，翻译层无需改动。

---

## 7. RakNet 实现细节与 TODO

### 已实现

| 能力 | 位置 |
| --- | --- |
| Magic 校验 | `Codec/RakNet/RakNetMagic.cs` |
| IPv4 地址编解码（取反 IP + 大端端口） | `Codec/RakNet/RakNetAddressCodec.cs` |
| Ping/Pong、Req1/Reply1、Req2/Reply2 | `Codec/RakNet/OfflineMessageCodec.cs` |
| 帧可靠性头、分片头解析 | `Codec/RakNet/RakNetFrameCodec.cs` |
| 数据报、ACK/NAK 编解码 | `Codec/RakNet/RakNetDatagramCodec.cs` |
| 握手消息 | `Codec/RakNet/HandshakeCodec.cs` |
| UDP 监听、离线包分发、会话注册 | `RakNet/RakNetServer.cs` |
| 握手状态机、可靠有序收包、ACK 生成 | `RakNet/RakNetSession.cs` |

### 未实现（代码内 TODO）

- 分片帧重组：当前收到分片帧直接丢弃并告警；
- NAK/超时重传：当前仅记录 ACK，依赖客户端重发；
- 拥塞控制与发送窗口；
- RakNet 加密；
- IPv6 地址；
- `ConnectionRequestAccepted` 的 systemAddresses 目前用 10 个 `0.0.0.0:0` 占位；
- MTU 超限拆分（当前只告警）。

---

## 8. 新增一个 Minecraft 版本需要改什么

以新增 `1.22.0`（示例协议号 800）为例，按顺序：

1. **登记版本**
   - 修改 `src/JERCPLD.Core/ProtocolVersion.cs`；
   - 在 `KnownProtocolVersions` 增加 `public static readonly ProtocolVersion V1_22_0 = new(800, "1.22.0");`
   - 同时加入 `All`；
   - 用真实 BDS 登录验证协议号，去掉 TODO。

2. **核对 Bedrock 包 ID**
   - 修改 `src/JERCPLD.Protocol/Bedrock/BedrockPacketIds.cs`；
   - 补齐该版本新增/变化的包 ID 与 `NameOf`。

3. **实现星型适配器**
   - 在 `src/JERCPLD.VersionMap/Adapters/` 新增 `V800StarAdapter.cs`；
   - 实现 `IVersionStarAdapter`：
     - `Version = KnownProtocolVersions.V1_22_0`；
     - `Supports` 返回本适配器覆盖的包；
     - `Decode/Encode` 按字段读写 IR（复用 `BinaryReaderEx/BinaryWriterEx`）。
   - 为每个需要跨版本的包写明 `TypeId` 与规范字段名。

4. **注册适配器**
   - 在 Cli / 宿主组装处调用 `registry.Register(new V800StarAdapter())`；
   - 生产代码中不要注册 passthrough 适配器。

5. **（可选）相邻版本链式步骤**
   - 仅当 800 与相邻版本差异极小，且不打算立刻写星型适配器时，新增 `Steps/V799ToV800Step.cs` 实现 `IAdjacentVersionStep`；
   - 在注册处 `registry.Register(step)`；
   - 必须同时验证 `Upgrade` 与 `Downgrade` 两个方向。

6. **配置客户端/BDS 版本**
   - 启动参数 `--server-version 1.22.0`；
   - 监听侧 `--client-version` 保持不变（旧客户端版本）。

7. **验证**
   - 实机登录检查：`Login`、`NetworkSettings`、资源包、`StartGame`、`RequestChunkRadius` 等；
   - 用 `jercpld-dump` 对比翻译前后的包；
   - 确认未知包被显式记录，而不是静默丢包。

8. **更新文档**
   - 在本文档"当前注册情况"和 TODO 表中补充该版本状态。

> 只改协议号不改适配器是**不允许**的：协议号只决定版本识别，字段差异必须由适配器处理。

---

## 9. 抓包调试工具

`tools/JERCPLD.PacketDump`（可执行名 `jercpld-dump`）：

```powershell
# 直接粘贴十六进制
jercpld-dump "01 00 00 00 00 00 00 00 01 00 FF FF 00 FE FE FE FE FD FD FD FD 12 34 56 78 11 22 33 44 55 66 77 88"

# 从文件读取
jercpld-dump capture.hex

# 标准输入
Get-Content capture.hex | jercpld-dump
```

自动识别：离线 Ping/Pong、Req1/Req2、ACK/NAK、RakNet 数据报、0xFE Bedrock 批次（明文失败后尝试 zlib）。

---

## 10. 构建与运行

确保 `dotnet --list-sdks` 能看到 8.x SDK；如果 PATH 中的 `dotnet` 只有运行时（报 "No .NET SDKs were found"），请改用带 SDK 的 `dotnet.exe` 完整路径执行下列命令。

```powershell
dotnet build JERCPLD.sln

# 启动代理（骨架阶段 NetherNet 会明确报未实现）
dotnet run --project src/JERCPLD.Cli -- serve --listen 0.0.0.0:19132 --client-version 1.26.60 --server-version 1.26.50

# 只看版本表
dotnet run --project src/JERCPLD.Cli -- versions

# 桌面应用（开发运行）
dotnet run --project src/JERCPLD.Desktop

# 发布 win-x64 自包含单文件 exe
dotnet publish src/JERCPLD.Desktop -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64
```

握手验证（无需真实客户端）：

1. 发送 `UnconnectedPing`，应收到 `UnconnectedPong`，MOTD 中协议号为客户端版本；
2. 发送 `OpenConnectionRequest1/2`，应收到对应 Reply，MTU 被限制在配置范围内；
3. 发送 `ConnectionRequest`（可靠有序帧），应收到 ACK 与 `ConnectionRequestAccepted`；
4. 发送 `NewIncomingConnection`，日志出现"RakNet 握手完成"；
5. 代理随后尝试连接 NetherNet，明确报 `NetherNet 传输尚未实现` 并关闭会话。

---

## 11. 已知风险

| 风险 | 说明 | 后续 |
| --- | --- | --- |
| 1.26.30 协议号 | 官方文档无该版本页，当前取社区数据 1001 | 用真实客户端/BDS 实测复核 |
| Bedrock 压缩 | 批次可能为 zlib/snappy，当前按明文解析 | 实现 NetworkSettings 协商与压缩解码 |
| 加密 | RakNet 加密与 Bedrock 加密握手都未实现 | 按 BDS 版本补齐 |
| NetherNet 复杂度 | WebRTC 信令与 BDS 认证工作量大 | 先 ArkMirage 原型，再 libwebrtc |
| 会话缓冲 | 上游连接建立前到达的客户端包会丢失 | 增加会话级待发队列 |
| 分片/重传 | 大包与丢包场景不可用 | 实现分片重组与重传 |

---

## 12. 代码中的 TODO 索引

- `src/JERCPLD.Core/ProtocolVersion.cs`：版本表来源标注；1.26.30 待实测复核
- `src/JERCPLD.Core/Networking/EndpointParser.cs`：主机名解析
- `src/JERCPLD.Protocol/RakNet/OfflineMessages.cs`：MOTD 新字段
- `src/JERCPLD.Codec/RakNet/RakNetAddressCodec.cs`：IPv6
- `src/JERCPLD.Codec/RakNet/RakNetFrameCodec.cs`：分片重组
- `src/JERCPLD.Codec/Bedrock/BedrockCompressionCodec.cs`：Snappy
- `src/JERCPLD.RakNet/RakNetServer.cs`：加密、IPv6、分片
- `src/JERCPLD.RakNet/RakNetSession.cs`：重传、systemAddresses、MTU 拆分
- `src/JERCPLD.NetherNet/PlaceholderNetherNetTransport.cs`：ArkMirage / libwebrtc
- `src/JERCPLD.Proxy/TranslationProxy.cs`：压缩协商、会话缓冲
- `src/JERCPLD.VersionMap/VersionMapRouter.cs`：真实适配器注册
- `src/JERCPLD.Desktop/`：P1 直通模式，真实翻译与 NetherNet 接入待后续阶段

---

## 13. 桌面应用（P1）

项目：`src/JERCPLD.Desktop`（Avalonia 11 + CommunityToolkit.Mvvm，纯 C#）。

- 版本下拉覆盖 26.30–26.60，每项显示协议号、来源（官方/社区）与同字节等价版本；
- 一键启动/停止 RakNet 诊断监听；
- 实时显示会话数、包总数、已翻译/透传/丢弃、批次解析失败；
- 日志面板带级别着色与自动滚动；
- 顶部横幅与日志明确标注：**当前为直通模式，版本翻译未启用，NetherNet 未实现**。

发布产物：`dist/win-x64/JERCPLD.exe`（win-x64、自包含、单文件）。

### 13.1 版本表（26.30–26.60）

| MC 版本 | 协议号 | 来源 | 等价组 |
| --- | --- | --- | --- |
| 1.26.30 | 1001 | 社区（官方无版本页） | — |
| 1.26.40 / 1.26.43 / 1.26.44 | 2168 | 官方 | A |
| 1.26.45 | 2169 | 官方 | — |
| 1.26.50 / 1.26.51 | 2193 | 官方 | B |
| 1.26.60 / 1.26.61 / 1.26.62 | 2225 | 官方 | C |

来源：`https://mojang.github.io/bedrock-protocol-docs/changelog/`（2026-11-08 抓取）；
1.26.30 取自 `PrismarineJS/minecraft-data` 的 `data/bedrock/common/protocolVersions.json`，需实测复核。

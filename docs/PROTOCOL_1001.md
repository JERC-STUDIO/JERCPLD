# JERCPLD 26.33（协议 1001）支持方案

日期：2026-11-14
基线：`4c93e90`（v0.0.2）
结论先行：**26.33 不需要开发新协议**。它是 26.30 的热修复版，协议号同为 **1001**，包结构完全一致。仓库对 1001 已有一条 s2c 补丁路径（S10），缺的是 6 个包的补丁与一次真机验收。

---

## 一、事实核验

### 1.1 协议号

Minecraft Wiki（官方版本页）：

> Protocol version **1001**
> 26.33 is a hotfix to Bedrock Edition released on July 9, 2026 … which fixes bugs.
> Fixes: 自定义方块贴图渗透、角色创建器外观重置、Damage Tilt 滑块翻译、快捷栏 emoji 显示。

中文维基明确写着：

> 此版本与 **26.30 至 26.32 相互兼容**。

即：1001 覆盖 26.30 / 26.31 / 26.32 / 26.33 四个客户端版本，**热修复不改变协议**。

### 1.2 与仓库现有记录一致

`docs/STATE.json:116` 已确认：

```
1.26.30=1001、1.26.40=2168、1.26.45=2169、1.26.51=2193、1.26.60=2225
```

`docs/STATE.json:5`：

> 26.30 (1001) only via RakNet

### 1.3 数据源交叉验证

用 `minecraft-data@3.119.0` 的实际 schema 核对：

```bash
$ ls node_modules/minecraft-data/minecraft-data/data/bedrock/ | grep 1.26
1.26.30  1.26.40  1.26.45  1.26.51
```

没有 `1.26.33` 目录——因为它和 1.26.30 共用 1001，社区数据按协议号归档，热修复不会单独建目录。

---

## 二、这意味着什么

| 问题 | 答案 |
| --- | --- |
| 26.33 客户端发什么协议号？ | 1001 |
| 和 26.30 的包结构差异？ | 0（同一 schema） |
| 需要新增 PTL 版本对吗？ | 不需要。1001 没有独立 pair，走 legacy `<2168` 路径 |
| 现有代码能接受 26.33 吗？ | 能，只要 `acceptMinProtocol <= 1001` |
| 传输层能用吗？ | **不能直连 nethernet**，必须走 S10 的独立 `transport=raknet` 实例 |

所以「开发 26.33 协议」的真实工作量，等于「把 1001 这条路补完整 + 验收」，不是新写一套翻译。

---

## 三、1001 当前支持矩阵

| 环节 | 实现位置 | 状态 |
| --- | --- | --- |
| 接受 1001 客户端 | `acceptMinProtocol`（S10 配 1001） | ✅ 已有 |
| raw 协议号改写 1001→2193 | `rewriteRawProtocol`（`:435`） | ✅ 已有 |
| 资源包 ResourcePacksInfo countType | `fixResourcePacksInfoCountType`（`:1271`） | ✅ 已有 |
| 资源包 ClientResponse u8→varint | `fixResourcePackClientResponseForOldClients`（`:1310`） | ✅ 已有 |
| StartGame 插 `is_chat_logging` | `rewriteStartGameForOldClients`（`:1624`） | ✅ 已有 |
| StartGame gamerules li32→varint | `reencodeGamerulesInt32Li32ToVarint` | ✅ 已有 |
| **AddPlayer / AddEntity / AddItemEntity** | 无 | ❌ 缺 |
| **MobEquipment / MobArmorEquipment** | 无 | ❌ 缺 |
| **MovePlayer teleport 字段** | 无 | ❌ 缺 |
| Disconnect reason 枚举 | 无 | ⚠️ 仅影响提示文案 |

S10 验收记录（`docs/STATE.json:91`）：

> 1.26.30 PASS code=0（StartGame 98 blockProperties 解析成功 + 4 区块 + move + animate + 无踢出）；
> spawn=false 系客户端对 set_entity_data metadata NBT 等非关键 s2c 包的 schema 告警

也就是说：**单人生存能进、能走、能看地形；但多人/生物相关的实体包没有翻译**。

---

## 四、1001 ↔ 2168 的完整差异（10 个包）

用 `tools/verify-client/diff-schemas.js` 对 `1.26.30` 与 `1.26.40` 穷举得出（244 个包全遍历）：

```
disconnect (5)                    resource_packs_info (6)
resource_pack_client_response (8) start_game (11)
add_player (12)                   add_entity (13)
add_item_entity (15)              move_player (19)
mob_equipment (31)                mob_armor_equipment (32)
```

**全部是 s2c**。c2s 方向零差异——这点很关键，意味着不需要碰 `144 player_auth_input`，也就不会引入移动/蹲伏类回归。

### 4.1 已覆盖（4 个）

| 包 | 差异 | 现状 |
| --- | --- | --- |
| 5 disconnect | reason 枚举 122+ 整体后移 1 位 | 仅影响断开提示文案，不影响连接 |
| 6 resource_packs_info | `texture_packs[].countType`: li16 ↔ varint | ✅ `:1271` |
| 8 resource_pack_client_response | `response_status` u8↔varint、新增 `response_status_name`、ids 条件化 | ✅ `:1310` |
| 11 start_game | `is_chat_logging` 有无、gamerules 编码 | ✅ `:1624` |

### 4.2 待补（6 个）

#### ① item 栈格式（影响 12 / 15 / 31 / 32）

1001 的 `Item`：

```
network_id      zigzag32 varint
  if network_id == 0 : 结束（air 只占 1 字节）
  else:
    count             lu16
    metadata          varint
    has_stack_id      u8
    stack_id          if has_stack_id != 0: zigzag32
    block_runtime_id  zigzag32
    extra             varint 长度 + NBT
```

2168 的 `ItemV4`：

```
network_id      li16（固定 2 字节小端，不是 varint！）
count           lu16          ← air 也照写
metadata        varint
has_stack_id    bool
stack_id        if true: zigzag32
block_runtime_id varint
extra           varint 长度 + NBT
```

**转换规则**（1001 → 2168，s2c 方向）：

1. 读 zigzag32 `network_id`；
2. 写 li16；
3. 若 `network_id == 0`（air），补写 `count=0, metadata=0, has_stack_id=0, block_runtime_id=0, extra_len=0`；
4. 否则按原值逐字段改写，`block_runtime_id` 由 zigzag32 变 varint（对非负值编码相同，可原样拷贝）。

风险：`li16` 有符号上限 32767，若未来 item id 超界会截断——当前物品表约 2000，安全。

#### ② metadata 字典（影响 12 / 13 / 15）

```
1001:  key(varint) + type(varint) + value(switch)
2168:  key(varint) + type(varint) + legacy_type(u8) + value(switch)
```

每个条目多 1 字节 `legacy_type`。遍历全部条目，在 `type` 之后插入该字节。

**这一条存疑**：`legacy_type` 在真实 Bedrock 线格式里通常不存在，很可能是 minecraft-data 的建模差异。仓库此前已因同类问题给 1.26.40/45 打过补丁（`docs/STATE.json:91` 末句：「官方文档确认 2168/2169/2193 wire 一致，社区 schema 建模错误」）。**必须先抓包确认**，否则加了这一字节会直接让实体包解析失败。

#### ③ move_player teleport（19）

```
1001: teleport = switch(mode == "teleport") { cause: li32, source_entity_type: varint }
2168: teleport = option { cause: li32, source_entity_type: varint }
```

`option` 在字段前多 1 字节 bool。转换：当 `mode == teleport` 时补 `01`，否则补 `00`。

同样需要抓包确认——若 2168 的 `option` 实际不带前缀（`optionalOnRemaining` 语义），则此包**无需改动**。

#### ④ device_os（12）

```
1001: DeviceOS (mapper, varint)
2168: optionalOnRemaining<DeviceOS>
```

`optionalOnRemaining` 表示「剩余字节还有就读」——**不写前缀**。若 `device_os` 是 add_player 最后一个字段（确认是），则两者线格式相同，**无需翻译**。

---

## 五、建议的落地顺序

### 阶段 0：先证明 26.33 能进（0 行代码）

```powershell
# 在 S10 实例（transport=raknet, 端口 19252）上
# plugins/JERCPLD/config/config.json
"acceptMinProtocol": 1001
```

用真实 26.33 客户端连 `19252`，看 `hook.log` 是否出现：

```
[JERCPLD] raw protocol rewrite: packet=193 1001 -> 2193
[JERCPLD][CONN] id=1 proto=1001->2193 hops=1
```

若这两行出现且能进世界，说明 26.33 已可用（与 S10 的 26.30 同级）。

### 阶段 1：抓包确认 ②③

在 BDS 侧开 `capture.enabled=true`，抓三帧：

| 场景 | 包 | 要看的字段 |
| --- | --- | --- |
| 第二玩家进服 | 12 add_player | `held_item` 首字节、`metadata` 每条目长度 |
| 生成生物 | 13 add_entity | `metadata` 条目 |
| 掉落物 | 15 add_item_entity | `item` |
| 玩家传送 | 19 move_player | `mode=teleport` 后是否多 1 字节 |

判据：把 2168 的字节按 1001 schema 解析，若 `metadata` 条目后整体错位 1 字节/条，说明 `legacy_type` 真实存在。

### 阶段 2：按确认结果补代码

只改 `translateOutboundPacket`（`:1935`）的 `<2168` 分支，新增：

```cpp
if (peerProtocol < 2168) {
    switch (packetId) {
    case 12: return fixAddPlayerFor1001(data, offset);
    case 13: return fixAddEntityFor1001(data, offset);
    case 15: return fixAddItemEntityFor1001(data, offset);
    case 31: return fixMobEquipmentFor1001(data, offset);
    case 32: return fixMobArmorEquipmentFor1001(data, offset);
    case 19: return fixMovePlayerFor1001(data, offset);
    }
}
```

核心是两个可复用函数：

```cpp
// 1001 Item(1) -> 2168 ItemV4(2..n)
bool item1001To2168(PacketCursor& in, std::string& out);

// 1001 metadata -> 2168 metadata（每个条目 +1 字节，需先确认）
bool metadata1001To2168(PacketCursor& in, std::string& out);
```

`PacketCursor`（`:880`）已提供 `u8/li32/var/str/skip`，`skipNbtPayload`（`:934`）可跳 NBT，两者直接复用。

### 阶段 3：回归

- 26.33 单人：进服、走、挖、背包
- 26.33 + 第二玩家：互相可见、手持物正确
- 26.33 + 生物：生成/装备正确
- **26.40 / 26.45 / 26.51 / 26.60 全量回归**，确认没碰坏 `peer >= 2168`

---

## 六、为什么不建议新增 `1001-2168.json` PTL 对

1. 现有 `<2168` legacy 路径已经在跑资源包与 StartGame，两套并存会产生「谁先谁后」的歧义；
2. `dispatchOutboundTranslation` 的 legacy 分支条件是 `data == before && clientProtocol < 2168`——一旦 PTL 对改动了包，legacy 就不再执行，等于把已验证的 StartGame 逻辑挤掉；
3. 1001 的差异只有 6 个 s2c 包，写成 PTL 规则需要 builtin 支撑，最终仍要落到 C++，不如直接加在 legacy 路径里。

如果后续要支持 1.26.31/32，无需任何改动——它们同为 1001。

---

## 七、工作量估计

| 项 | 工作量 | 风险 |
| --- | --- | --- |
| 阶段 0 验证 | 0.5 人日（需 26.33 客户端） | 低 |
| 抓包确认 ②③ | 0.5 人日 | — |
| item 转换 | 1 人日 | 中（air 分支、li16 边界） |
| metadata 转换 | 1 人日 | 中（NBT 跳过、条目计数） |
| teleport / device_os | 0.5 人日 | 低 |
| 回归 | 1 人日 | — |
| **合计** | **3–4 人日** | 若 ②③ 确认是 schema 假差异，可压到 2 人日 |

---

## 八、PoC 进度（已落地）

已实现并跑通物品栈编解码：

- `mod/JERCPLD.Mod/src/JercpldPtl1001.h` —— 头文件实现，不依赖 LeviLamina，可直接 include 进 `JerCpldMod.cpp`
- `tests/ptl1001/test_item1001.cpp` —— 6 组用例，`g++ -std=c++20` 编译通过，全部 PASS

```
[1] air item: 1001 单字节 00 -> 2168 八字节      PASS
[2] 普通物品: id=5 count=3                      PASS
[3] 带 stack_id 与 extra                       PASS
[4] 往返: 2168 air -> 1001                      PASS
[5] 往返: 1001 -> 2168 -> 1001 保值              PASS
ALL PASS (fail=0)
```

写测试时抓到一个真实 bug：`ItemV4` 没有 `network_id == 0` 的短路分支，air 物品在 2168 侧仍写满 8 字节；反向转换若只读 2 字节 `li16` 就返回，会让紧随其后的字段整体错位 6 字节。已修正为「air 也要消费尾部字段，但 1001 侧只输出 1 字节」。

> 注意：这个 8 字节结论建立在 minecraft-data 的 schema 上。若真机抓包显示 2168 的 air 只有 2 字节，则 `item1001To2168` 的 air 分支也要相应改成只写 `li16`。抓一帧 12 add_player（手持空）即可判定。

未实现（等抓包结论）：metadata 的 `legacy_type`、move_player 的 teleport option、disconnect 的 reason 重映射。

## 九、一句话总结

26.33 是 26.30 的热修复，协议号同为 1001，**不存在「26.33 的协议」**。
真正要做的是：先用 S10 的 RakNet 实例验证 26.33 能进（大概率直接过），
再抓包判定 6 个 s2c 差异是真实线格式变化还是社区 schema 假差异，
确认后再补 `translateOutboundPacket` 的 `<2168` 分支。

# PTL 规则文件（Packet Translation Language）

## 模块形态（2026-11-09 起）

翻译规则以**单个 DLL** 交付：`plugins/JERCPLD/PTL/JERCPLD.PTL.dll`。

- 一个文件承载全部支持的版本对（当前只有 2193↔2225）。
- 主插件启动时扫描该文件并加载；支持哪些版本由主插件配置的 `acceptMinProtocol` / `acceptMaxProtocol` 决定，不由 DLL 数量决定。
- 服主替换这个 DLL 即可更新翻译能力；新增版本对需要重新构建该 DLL（单模块的取舍：分发/升级简单，但新增版本必须重新出包）。
- 规则数据仍以 `PTL/*.json` 为源，构建期由 `gen-ptl-header.ps1` 内嵌进 DLL。运行期以 DLL 为准；DLL 不存在或无效时才退回读取 `PTL/*.json`（开发/兼容模式）。

### C ABI（导出表固定 5 个符号，全部 `extern "C"`）

```c
uint32_t    jercpld_ptl_api_version(void);          // 当前为 1
uint32_t    jercpld_ptl_pair_count(void);
const char* jercpld_ptl_pair_name(uint32_t index);  // "2193-2225"
const char* jercpld_ptl_pair_rules(uint32_t index); // NUL 结尾的 JSON
int         jercpld_ptl_self_check(void);           // 0 = OK
```

不导出 C++ 类、不跨边界传 STL。主插件加载后立即解析 JSON 并 `FreeLibrary`，运行期不持有模块句柄。

### 为什么是「DLL 承载数据、主插件执行」

- 边界检查、越界回滚、A→B→A 自检全部留在主插件，所有模块走同一套安全逻辑，模块无法绕过。
- 跨 DLL 只传 `const char*`，不会出现 STL/ABI 不兼容。
- 新增声明式版本对只需重建 DLL，不必改主插件；结构性 builtin 仍需主插件支持。

### 加载失败隔离

- API 版本不匹配、导出缺失、自检失败、JSON 解析失败：拒绝该模块并记录原因，服务端继续运行。
- 模块不存在或无效时退回 `PTL/*.json`；两者都不可用则翻译能力为空（不崩溃）。
- 规则在线不热切换：`PTL` 目录 mtime 变化后，只有无在线会话时才重载；有会话只记日志。

## 官方支持范围

- 客户端 **26.50–26.62**（协议 2193–2225）↔ 服务端 **26.50–26.51**（协议 2193）。
- 主线只维护 `2193-2225.json`。
- `2169-2193.json` 已移至 `examples/PTL/`，属非官方维护的示例，默认不安装；26.45 因传输层（RakNet-only 客户端 vs NetherNet 服务端）不可达，见 `docs/BLOCKERS.md` B4。

## 目录与文件

- 目录：`plugins/JERCPLD/PTL/`
- 一个**版本对**一个 JSON 文件，文件名即版本对：`2193-2225.json`（主线）
- 文件内同时表达两个方向；不需要按方向分文件

## 文件身份

```json
{
  "ptlApi": 1,
  "pair": [2193, 2225],
  "serverVersions": ["1.26.50", "1.26.51"],
  "packets": { ... }
}
```

- `ptlApi`：插件规则 API 版本。当前只接受 `1`；不匹配时**拒绝加载**并在日志写明需要哪个版本，不会静默跳过。
- `pair`：`[较旧协议号, 较新协议号]`。
- `serverVersions`：该规则对适用的服务端版本（仅供人读与文档核对，不参与路由）。

## 双向推导

对本质对称的差异，只声明一条事实：

```json
"13": { "name": "add_actor", "rules": [
  { "type": "appendBytes", "addedIn": 2225, "bytes": "00" }
] }
```

含义：2225 新增了这 1 个字节。引擎在 `2193 -> 2225` 时追加，在 `2225 -> 2193` 时校验并删除。

不对称的差异写在 `overrides`，必须分方向明写：

```json
"12": { "name": "add_player", "overrides": {
  "olderToNewer": [ { "type": "builtin", "name": "add_player_passenger" } ],
  "newerToOlder": []
} }
```

> 为什么不对称：2225 的服务端会在 `device_id` 前插入可选字段，但反向需要重新定位该可选字段的负载长度；在拿到 2225 服务端真实抓包前不写反解，避免猜。

## 规则类型

| type | 字段 | 语义 |
| --- | --- | --- |
| `appendBytes` | `addedIn`, `bytes`(hex) | 尾部追加；反向校验后删除 |
| `insertField` | `addedIn`, `skip`(程序), `bytes` | 游标处插入；反向校验后删除 |
| `enumRemap` | `skip`, `map` | 游标处 1 字节查表替换（只影响命中的值） |
| `zeroFixed` | `skip`, `zeros` | 游标处连续 N 字节清零 |
| `builtin` | `name` | 引用引擎内置的结构性重写 |

### 游标程序（`skip`）

按顺序把游标从包载荷起点移动到目标字段，全程做越界检查：

`var`、`u8`、`u16`、`i32`、`lf32`、`str`、`nbt`、`optionalVar`、`skip:N`、`marker:<text>`

`marker:<text>` 用于定位带长度前缀的字符串（如 StartGame 的 `multiplayer_correlation_id`），
引擎找到文本后按前置长度字节跳到字符串末尾，再继续后续 token。

## 引擎内置能力（builtin）

以下必须重新解析/重编码整包，无法用声明式规则表达，跟插件版本走：

| name | 说明 | 反向是否实现 |
| --- | --- | --- |
| `start_game_editor_checksum` | StartGame 中间插入/删除 `editor_level_migration_version`，并把 `block_pallette_checksum` 清 0 | 已实现 |
| `add_player_passenger` | AddPlayer 在 `device_id` 前插入 `passenger_of_block` | 未实现（需 2225 抓包） |
| `level_chunk_skeleton` | LevelChunk 切换 skeleton 模式 / 删除尾部 `client_biome_update` | 已实现 |
| `player_list_fab` | PlayerList 的 `play_fab_id` 迁移 | 未实现（需 2225 抓包） |
| `client_cache_force_off` | c2s `client_cache_status` 强制 false | 不需要 |
| `camera_presets_strip` | 为 2169 及更老客户端裁掉 CameraPresets 尾部字段（非主线，示例用途） | 不需要 |

## 链式路由

- 引擎按已加载的版本对建图，BFS 找最短路径。主线只有 `2193 <-> 2225` 一跳；若启用 `examples/PTL/` 的示例，可能形成 `2169 -> 2193 -> 2225` 两跳（非官方支持）。
- 缺少中间档时**明确报错**并放弃本次翻译（日志：`缺少 X -> Y 的规则对`），不做半程硬翻。
- 路径最多 3 档，超过直接拒绝，避免错误累积。
- 每一跳独立 A→B→A 往返自检：对称规则首次命中时执行，结果不一致即禁用该包规则并记录日志。

## 安全

- 规则结构在加载期校验：`ptlApi`、`pair`、包 ID、规则类型、hex、`skip` token 全部检查，失败即拒绝加载。
- 运行时每条规则的偏移/长度都做边界检查；越界或校验失败时**回滚为原始 buffer**并记日志，不上抛异常。
- 规则文件无法导致进程崩溃：所有解析都在 `try` 内，任何异常都退化为「不翻译」。

## 重载

- 目标形态：`/jercpld reload`。当前**未实现**：LeviLamina 命令 API 依赖 `boost/pfr.hpp`，本机构建环境缺少该头文件。
- 现行替代：`plugins/JERCPLD/PTL/*.json` 的 mtime 变化后，若无在线会话则自动重载；有会话在线时只记日志、不切换规则（`[JERCPLD][PTL] 检测到规则变更，但有会话在线，暂不重载`）。
- 在线判定：login 包 +1、`onDisconnect` -1。

## 新增一个版本对

1. 新建 `PTL/<a>-<b>.json`，写 `ptlApi: 1` 与 `pair`。
2. 对称差异用 `rules` + `addedIn`；不对称差异用 `overrides` 分方向。
3. 需要整包重编码时用 `builtin`，并在上表登记实现与反向状态。
4. 用 `tools/verify-client` 跑该版本回归，抓包核对字段。
5. 若某规则无法做 A→B→A 往返，不要加入 `rules`（会被自检禁用），改在 `overrides` 单方向声明并注明 TODO。

---

# 架构现状与能力矩阵（2026-11-13）

## 1. 先回答「为什么改引擎」

用户的原始契约是：**换/加版本只放一个翻译模块 DLL，主插件不动**。
现状是：模块只导出规则数据（`api_version/pair_count/pair_name/pair_rules/self_check`），**没有翻译代码**；执行翻译的是主插件。
因此契约只在「差异能用现有原语表达」时成立；任何需要新变换语义的差异，都必须改主插件。这不是措辞问题，是当前实现的事实。

## 2. 已实现修复的分类

> 统计口径：`PTL/2169-2193.json` 15 个包、`PTL/2193-2225.json` 7 个包、`PTL/2168-2169.json` 空档；外加主插件里的运行时策略。

### A. 纯 JSON 规则（现有引擎可表达，丢 JSON 即生效）

| 包 | 规则 | 备注 |
| --- | --- | --- |
| 2193↔2225 `13 add_actor` | `appendBytes "00"` | 对称增删 1 字节 |
| 2193↔2225 `44 animate` | `appendBytes "00"` | 对称增删 1 字节 |
| 2169→2193 `111 move_entity_delta` | `appendBytes "00"`（仅 olderToNewer） | 反向用了 builtin，见 B |

**结论：真正纯 JSON 的只有 3 个方向，占已实现规则的极少数。**

### B. 引擎新增原语（通用能力，但仍需主插件更新一次）

| 原语 | 解决的差异类型 | 已使用的包 |
| --- | --- | --- |
| 游标程序 `skip`（var/u8/u16/i32/lf32/str/nbt/optionalVar/skip:N/marker:） | 任意「定位到第 N 个字段」 | 11、129、329 等 |
| 变长位域读取（1–16 字节，tick 校验） | 2169 `input_data` bitfield → 2193 数组 | 144 |
| 定长前缀 + `array<zigzag>` 重编码 | bitfield/数组互换 | 144 |
| 尾部 28 字节（analogue/camera/raw）保留、可选字段组丢弃 | 可选载荷长度未知的重建 | 144（宽松模式） |
| HeightMap 行头增删（16 行 × 16 字节 ↔ 裸 256 字节） | 174 子区块高度图 | 174 |
| 尾部 varint 删除 | 111 反向 | 111 |
| `zeroFixed`（游标 + N 字节清零） | checksum/固定字段清零 | 11（当前用 builtin 实现，可参数化） |
| `appendBytes` 反向校验 | 对称新增字段 | 13、44、111 |

### C. 代码级特例（无法通用化，换版本必须改主插件）

| 包 | 代码逻辑 | 为什么是特例 |
| --- | --- | --- |
| 11 start_game | 清 `block_pallette_checksum`（8B）；2225 还要保留/删除 `editor_level_migration_version` | 字段位置依赖具体布局，必须逐版核对 |
| 129 client_cache_status | 1 字节布尔强制 false | 语义特例（缓存键不兼容） |
| 30 inventory_transaction | 内层 `TransactionUseItem` 的 item descriptor 排布 | 结构复杂，需逐版抓包 |
| 144 player_auth_input | 位域宽度判定、deprecated 过滤、option 组、宽松重建 | 结构复杂，需逐版抓包 |
| 148 item_stack_response | 容器结果/物品栈字段重排 | 结构复杂 |
| 174 subchunk | 高度图行格式 + 子区块版本 | 结构复杂 |
| 74 boss_event | 事件类型 + 字符串/浮点字段差异 | 结构复杂 |
| 86 play_sound | 尾部新增字段（bypass/playback_position） | 逐版字段 |
| 180 dimension_data | 维度字段差异 | 逐版字段 |
| 198 camera_presets | 裁剪尾部新增字段 | 逐版字段 |
| 329 serverbound_pack_setting_change | type=3 string_list 守卫 | 语义特例 |

### D. 运行时策略（不是翻译，但影响可玩性）

| 策略 | 说明 | 位置 |
| --- | --- | --- |
| fallback | 无法解析的 144 用上一帧 2193 包 + 当前 position/rotation；**input_data 已改为清空**，不再重复旧 sneaking | 主插件 |
| deprecated 过滤 | 2169 位域中的 2/54/55 不送进 2193 | 主插件（144） |
| drop | 30/315/351/352 显式丢弃（未实现安全翻译前） | JSON |
| 缓存强制关闭 | 129 → false | JSON + 主插件 |

## 3. 关键问题

### 3.1 多少修复「换版本才需要改插件」？

- 2169-2193：15 个包里 **11 个需要主插件代码**（C 类），3 个是策略 drop，只有 1 个方向能用纯 JSON（111 c2s）。
- 2193-2225：7 个包里 4 个 builtin（11/12/58/129），3 个纯 JSON（13/44）。
- 按包数粗略比例：**约 75% 的包级修复依赖主插件代码**；按代码行数更高（144/30/148/174 占了大头）。

### 3.2 下一个版本（如 26.70）到来时

- 只加 JSON：仅当差异是「尾部固定字节增删」「单字段插入/删除」「单字节枚举重映射」「固定偏移清零」，且不涉及新结构。
- 必须改引擎/模块代码：`start_game` 布局变化、144 位域语义变化、30/148 物品描述符变化、174 子区块版本变化、任何新增的可选载荷组。
- 判断依据（不看 changelog）：用 `tools/verify-client` 的 1.26.xx 模拟端 + 真实抓包，对比 `StartGame` 长度/字段、144 的 `input_data` 宽度与 tick、30 的内层结构；出现现有原语无法消费的字段边界，就必须写代码。

### 3.3 引擎没动过时，JSON 真能独立生效吗？

能，但只限 A 类差异。判断方法：
1. 新版本对的包 ID 集合与旧版本完全相同（对比 `minecraft-data` 的 `packet` 表）；
2. 每个差异都能用 `appendBytes/insertField/enumRemap/zeroFixed` 表达；
3. A→B→A 往返自检通过；
4. 模拟客户端能完整进服且无 kick。
满足这 4 条，JSON 独立生效；任一不满足，必须动代码。

## 4. 风险与建议

- **趋势判断：是，已经在向「按版本写死」滑**。144、30、148、174、11 都是逐版本抓包逐字段实现；它们不是「规则」，是「小型编解码器」。
- 应沉淀为通用原语的能力：
  - 可变长位域（读/写/宽度校验）→ 已具备；
  - 「已知前缀 + array<enum> + 尾部定长」重建 → 已具备；
  - 「可选字段组丢弃 + 帧尾定长保留」→ 已具备（144 宽松模式可直接复用）；
  - `zeroFixed` 参数化 checksum 清零（可把 11 的 builtin 降级为 JSON）。
- 必须承认是代码级的：30/148（物品描述符）、11（StartGame 布局）、74/86（可变字符串/字段）、174（子区块版本）。
- 文档必须明确写：**新版本若改动这些包，需要更新模块 DLL；如果模块是纯规则包，则还需要更新主插件。**

## 5. 模块契约的现实结论

- 当前模块 = **规则包**，不是**翻译器**。
- 「只换 DLL」在 A/B 类差异成立，在 C 类差异不成立。
- 若要真正满足用户契约，需要把 C 类逻辑搬进模块（方案 B），或把引擎做成参数化原语（方案 C）。评估见 `docs/REPO_SPLIT_PLAN.md`。

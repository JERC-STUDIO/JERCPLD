# PTL 规则 Schema v2 设计（把包特例从引擎搬进规则）

> 状态：设计稿（2026-11-14）。目标：**以后协议维护只改 JERCPTL（规则 JSON + PTL DLL），不改 JERCPLD 引擎。**
> 适用范围：先覆盖现有 5 类包级硬编码；其余仍为 builtin 的复杂结构（144/30/148/174/86…）不在本次范围。

## 1. 现状

引擎 `JerCpldMod.cpp` 里有一批「包级」硬编码，只能靠改引擎发布：

| 包 | 引擎实现 | 为什么现在必须写死 |
| --- | --- | --- |
| 74 `boss_event` | `bossEventTo2193/2169` | 需要在第 1 个 varint 后删除/插入一个 varint |
| 180 `dimension_data` | `dimensionDataTo2169/2193` | 数组元素内字段重排 + 整数运算 + 字符串默认值 |
| 111 `move_entity_delta` | `stripTrailingVarint` | 从包尾回扫一个 varint 并删除 |
| 11 `start_game` | `insertStartGameEditorField` / `removeStartGameEditorField` / `zeroBlockPaletteChecksum` | 中间字段插入/删除 + 按 marker 定位后清零 |
| 111/11 | 值域校验 | 尾部 varint 只做了结构校验，没有值域约束 |

现有 PTL 原语只有：`appendBytes` / `insertField` / `enumRemap` / `zeroFixed` / `builtin` / `drop`。
`insertField` 和 `zeroFixed` 其实已经能覆盖 StartGame 的两个动作，缺的是**删除字段**、**尾部裁剪**、**数组元素变换**。

## 2. 职责边界（改造后）

| 能力 | JERCPLD 引擎 | JERCPTL 规则/DLL |
| --- | --- | --- |
| 传输层 hook、协议号改写、连接注册 | ✅ | ❌ |
| BFS 链式路由、批打包、模块加载/ABI 校验 | ✅ | ❌ |
| 通用规则解释器（解析、游标、表达式、边界检查、回滚、A→B→A 自检） | ✅ | ❌ |
| 包 ID ↔ 字段布局 ↔ 方向差异 | ❌ | ✅（JSON） |
| 具体版本的结构变换 | ❌ | ✅（JSON，用通用原语组合） |
| 5 个 C ABI 导出 | 只消费 | ✅（DLL 仍只承载 JSON 数据） |

**边界规则**

- 引擎里不再出现包号、包名、`if (packetId == 74)` 之类分支。
- 规则能表达的差异 = 只改 JSON；新增**原语类型**才需要改引擎（这是本设计的已知边界）。
- PTL DLL 的 ABI 不变（仍是 `api_version / pair_count / pair_name / pair_rules / self_check`），
  只是内嵌 JSON 升级到 `ptlApi: 2`。构建脚本 `gen-ptl-header.ps1` 无需改动。

## 3. Schema v2 扩展

### 3.1 版本

```json
{ "ptlApi": 2, "pair": [2169, 2193], ... }
```

引擎同时接受 `1` 和 `2`。`2` 只用于启用下述新原语；旧规则的语义不变。

### 3.2 通用字段

| 字段 | 用于 | 语义 |
| --- | --- | --- |
| `when` | 任意规则 | 可选的守卫：先按 `skip` 定位，读一个字段并按谓词判断；不满足则**跳过该规则**（不是失败） |
| `field` | `deleteField` / `stripTrailing` | 字段类型 token |
| `range` | `stripTrailing` | `[min,max]` 闭区间；值不在范围内视为规则失败（回滚 + fail-closed） |
| `count` | `arrayTransform` | 数组长度前缀类型，目前支持 `var` |
| `element` | `arrayTransform` | 元素级操作序列 |
| `requireEnd` | `arrayTransform` | 为 `true` 时要求数组结束后游标正好到包尾 |

`when` 结构：

```json
"when": { "skip": ["var"], "field": "u8", "in": [0, 1] }
```

谓词：`eq` / `ne` / `in` / `min` / `max`。全部在加载期校验字段名与参数类型。

### 3.3 新规则类型

#### `insertConst`

在 `skip` 定位到的游标处无条件插入常量字节（不受方向影响）。用于 `overrides` 里只需单向插入的场景；
`insertField` 保留给 `rules` 这类需要双向推导的对称规则。

```json
{ "type": "insertConst", "skip": ["var"], "bytes": "00" }
```

#### `deleteField`

在 `skip` 定位到的游标处删除一个字段（字段宽度由 `field` 决定）。

```json
{ "type": "deleteField", "skip": ["var"], "field": "var" }
```

#### `stripTrailing`

从包尾回扫一个指定类型的值并删除；`range` 用于值域校验，越界即失败。

```json
{ "type": "stripTrailing", "field": "var", "range": [0, 4294967295] }
```

#### `arrayTransform`

在游标处读取数组长度，对每个元素执行 `element` 操作序列，重编码后写回。
支持 `read` / `drop` / `emit` 三种操作：

| op | 字段 | 语义 |
| --- | --- | --- |
| `read` | `name`,`type` | 读一个字段并绑定到变量（数值或原始字节） |
| `drop` | `type` 或 `"remaining"` | 读一个字段并丢弃 |
| `emit` | `type` + `const`/`ref`/`expr`/`map`+`default` | 按指定类型写出 |

表达式只支持 int64 的 `+ - * /` 与括号、整数字面量、已读变量；加载期解析成 AST，运行期做溢出与除零检查。

#### 字段类型 token

`var`（varuint64）、`u8`、`u16`、`i32`、`lf32`、`zigzag`（zigzag32）、`str`、`nbt`、`optionalVar`、`fixed:N`。

同时新增游标 token：

- `markerAny:<a>|<b>`：依次尝试各 needle，命中后按前置长度字节跳到字符串末尾（用于 StartGame 的 `<nethernet>` / `<raknet>` 双传输）。

### 3.4 安全

- 所有读取先做边界检查；任何失败都回滚原始 buffer 并按现有策略丢弃该包（fail-closed）。
- `arrayTransform` 限制元素数与总操作数（加载期上限 + 运行期计数），防止恶意规则拖死线程。
- 表达式为纯函数，无变量赋值以外的副作用。

## 4. 五类特例的迁移写法

### 4.1 `boss_event`（74，2169↔2193）

2169 在 `target_entity_id` 后带 `player_id(varint)`，2193 不带。

```json
"74": {
  "name": "boss_event",
  "overrides": {
    "olderToNewer": [ { "type": "deleteField", "skip": ["var"], "field": "var" } ],
    "newerToOlder": [ { "type": "insertConst", "skip": ["var"], "bytes": "00" } ]
  }
}
```

方向本身就是条件；若未来需要按事件类型区分，用 `when` 追加守卫即可，不需要改引擎。

### 4.2 `dimension_data`（180，2169↔2193）

2169：`id, max_height, min_height, generator, dimension_type, pack_id`
2193：`id, min_y, height_range, generator, dimension_type, pack_id, default_biome`

```json
"180": {
  "name": "dimension_data",
  "overrides": {
    "olderToNewer": [ {
      "type": "arrayTransform", "count": "var", "requireEnd": true,
      "element": [
        { "op": "read", "name": "id", "type": "str" },
        { "op": "read", "name": "max_height", "type": "zigzag" },
        { "op": "read", "name": "min_height", "type": "zigzag" },
        { "op": "read", "name": "generator", "type": "zigzag" },
        { "op": "read", "name": "dimension_type", "type": "zigzag" },
        { "op": "read", "name": "pack_id", "type": "fixed:16" },
        { "op": "emit", "type": "str", "ref": "id" },
        { "op": "emit", "type": "zigzag", "expr": "min_height" },
        { "op": "emit", "type": "zigzag", "expr": "max_height - min_height" },
        { "op": "emit", "type": "zigzag", "ref": "generator" },
        { "op": "emit", "type": "zigzag", "ref": "dimension_type" },
        { "op": "emit", "type": "bytes", "ref": "pack_id" },
        { "op": "emit", "type": "str", "ref": "generator",
          "map": { "3": "minecraft:nether_wastes", "4": "minecraft:the_end", "5": "minecraft:the_void" },
          "default": "minecraft:plains" }
      ]
    } ],
    "newerToOlder": [ {
      "type": "arrayTransform", "count": "var", "requireEnd": true,
      "element": [
        { "op": "read", "name": "id", "type": "str" },
        { "op": "read", "name": "min_y", "type": "zigzag" },
        { "op": "read", "name": "height_range", "type": "zigzag" },
        { "op": "read", "name": "generator", "type": "zigzag" },
        { "op": "read", "name": "dimension_type", "type": "zigzag" },
        { "op": "read", "name": "pack_id", "type": "fixed:16" },
        { "op": "drop", "type": "str" },
        { "op": "emit", "type": "str", "ref": "id" },
        { "op": "emit", "type": "zigzag", "expr": "min_y + height_range" },
        { "op": "emit", "type": "zigzag", "ref": "min_y" },
        { "op": "emit", "type": "zigzag", "ref": "generator" },
        { "op": "emit", "type": "zigzag", "ref": "dimension_type" },
        { "op": "emit", "type": "bytes", "ref": "pack_id" }
      ]
    } ]
  }
}
```

### 4.3 `move_entity_delta`（111，2193→2169）

```json
"newerToOlder": [ { "type": "stripTrailing", "field": "var", "range": [0, 4294967295] } ]
```

`range` 是新增的值域校验：尾部不是合法 tick 值时判定失败并回滚，而不是删错字节。

### 4.4 `start_game`（11）

2193→2225：在第 18 个字段处插入 `editor_level_migration_version(u8)=0`；反向删除。
两个方向都要把 `block_pallette_checksum` 清 0（`<nethernet>` / `<raknet>` 两种 marker 都支持）。

```json
"11": {
  "name": "start_game",
  "overrides": {
    "olderToNewer": [
      { "type": "insertConst",
        "skip": ["var","var","var","skip:12","skip:8","skip:8","skip:2","str",
                 "var","var","var","skip:1","var","var","var","var","skip:1","var","skip:2"],
        "bytes": "00" },
      { "type": "zeroFixed",
        "skip": ["markerAny:<nethernet>|<raknet>","u8","str","nbt"],
        "zeros": 8 }
    ],
    "newerToOlder": [
      { "type": "deleteField",
        "skip": ["var","var","var","skip:12","skip:8","skip:8","skip:2","str",
                 "var","var","var","skip:1","var","var","var","var","skip:1","var","skip:2"],
        "field": "u8" },
      { "type": "zeroFixed",
        "skip": ["markerAny:<nethernet>|<raknet>","u8","str","nbt"],
        "zeros": 8 }
    ]
  }
}
```

2169↔2193 只需要清 checksum：

```json
"11": {
  "name": "start_game",
  "overrides": {
    "olderToNewer": [],
    "newerToOlder": [
      { "type": "zeroFixed",
        "skip": ["markerAny:<nethernet>|<raknet>","u8","str","nbt"],
        "zeros": 8 }
    ]
  }
}
```

### 4.5 值域校验

由 `stripTrailing.range` 承担（见 4.3）。`appendBytes` 反向仍保留「尾部字节必须精确匹配」的校验。

## 5. 引擎改造清单

**新增（通用，不含包号）**

- 规则解析：`deleteField` / `stripTrailing` / `arrayTransform` / `when`，加载期校验字段名、类型 token、表达式 AST、变量引用。
- 运行时：类型化读写（var/u8/u16/i32/lf32/zigzag/str/nbt/optionalVar/fixed:N）、表达式求值、数组重编码、`markerAny` 游标 token。
- `ptlApi` 接受 `1` 与 `2`。

**删除（包级硬编码）**

- 函数：`bossEventTo2193`、`bossEventTo2169`、`dimensionDataTo2169`、`dimensionDataTo2193`、
  `stripTrailingVarint`、`insertStartGameEditorField`、`removeStartGameEditorField`、`zeroBlockPaletteChecksum`
  及其专用 helper（`PacketReader` 若不再被引用一并删除）。
- builtin 名字：`boss_event_to_2169/2193`、`dimension_data_to_2169/2193`、`move_entity_delta_strip_ticks`、
  `start_game_editor_checksum`、`start_game_zero_checksum`。
- `isKnownBuiltin` 同步收缩；规则若仍引用被删名字，**加载期直接报 `unknown builtin`**。

**保留（非本次范围）**

- 传输层 hook、连接注册、BFS 路由、批打包、抓包、性能统计。
- 其余 builtin：`add_player_passenger`、`level_chunk_skeleton`、`player_list_fab`、`client_cache_force_off`、
  `camera_presets_strip`、`play_sound_to_*`、`pack_setting_change_guard`、`transaction_to_*`、
  `player_auth_input_to_2193`、`item_stack_response_to_2169`、`subchunk_to_2169`。

## 6. 兼容与迁移

1. `ptlApi: 1` 仍可加载；但被删除的 builtin 名字不再存在，引用即拒绝（日志写明名字）。
2. 随引擎一起发布的三个规则文件在同一批次改为 `ptlApi: 2`，不保留双份规则。
3. 引擎与规则必须同版本发布：新引擎 + 旧规则（引用旧 builtin）会被拒绝；旧引擎 + 新规则（`ptlApi: 2`）也会被拒绝。
   这是有意的 fail-closed，避免静默半翻译。
4. `rules`（对称规则）的 A→B→A 自检逻辑不变；`overrides` 仍按方向执行。

## 7. 回归计划

1. `build-main.ps1` 构建引擎；JERCPTL 仓 `build.bat` 构建 PTL DLL；`install.ps1 -PtlDll ...` 安装。
2. 重启服务端（重启前单独报告）。
3. `verify-client` 跑三条链路：
   - 1.26.40（2168）：route `2193->2168`、`hops=2`、区块数 > 0；
   - 1.26.45（2169）：`start_game 73899->73899`、区块数 > 0；
   - 1.26.62（2225）：`start_game 73899->73900`、区块数 > 0。
4. 证据取 `plugins/JERCPLD/hook.log` 的 `[PTL][s2c] packet=11/74/111/180 ... len a->b` 行。
5. 另做一次规则级单测（如果时间允许）：用抓包样本验证 A→B→A 往返。

## 8. 已知边界

- 新原语覆盖的是「字段增删、尾部裁剪、数组元素重排 + 整数表达式」。遇到 144 位域、30/148 物品描述符、
  174 子区块这种嵌套结构，仍要新增原语或 builtin —— 届时引擎需要一次升级，但包级逻辑不再散落在引擎里。
- `arrayTransform` 不做通用 NBT/嵌套对象变换；当前只服务定长/变长标量与定长字节块。

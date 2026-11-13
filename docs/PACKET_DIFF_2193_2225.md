# 2193 ↔ 2225 包差异清单

> 服务端：BDS 1.26.51（协议 2193）；客户端目标：1.26.60/1.26.62（协议 2225）。
> 生成时间：2026-11-09 02:2x。
> 来源：
> - Mojang 官方 `bedrock-protocol-docs` 1.26.60 changelog（协议 2225）：8 个新增包、56 个变更包。
> - `minecraft-data 3.119.0` 的 1.26.51/1.26.60 结构递归 diff（字段级）。
> - 真实客户端抓包（`d:\ylcs\diagnostics\session-*-{c2s,s2c}.log`）验证实际发送情况。
>
> 说明：官方 changelog 页面单次抓取被截断，已取得 26/56 个包的逐条明细；其余以 minecraft-data 结构 diff 与本项目实测为准。标「无法确定」的条目需要再查官方单包页面。

## 状态图例

- 已翻译：当前构建已实现并通过模拟客户端回归
- 部分：只处理了主字段/一种方向
- 未处理：已确认差异，尚未改代码
- 无需处理：差异为枚举追加或教育/遥测等本服不发送的包
- 无法确定：缺少官方明细或缺少真实发送证据

## 一、新增包（官方 1.26.60，ID 353-360）

| ID | 包名 | 方向 | 差异 | 状态 |
|---|---|---|---|---|
| 353 | clientbound_matchmaking_state | s2c | 新增包 | 无需处理（2193 服务端不会发送） |
| 354 | serverbound_stonecutter_set_recipe | c2s | 新增包 | 无法确定（客户端使用切石机时才发） |
| 355 | clientbound_stonecutter_set_recipe | s2c | 新增包 | 无需处理（2193 服务端不会发送） |
| 356 | serverbound_matchmaking_cancel | c2s | 新增包 | 无需处理（联机匹配场景） |
| 357 | set_passenger_of_block | 双向 | 新增包 | 无法确定（本服未观测到） |
| 358 | serverbound_cursor_item_drag | c2s | 新增包 | 无法确定（背包拖拽时可能发送） |
| 359 | clientbound_play_audio_content | s2c | 新增包 | 无需处理（2193 服务端不会发送） |
| 360 | serverbound_register_audio_content | c2s | 新增包 | 无法确定（音频内容注册，本服未观测） |

## 二、结构/字段变化（已确认）

| ID | 包名 | 方向 | 差异点 | 类型 | 状态 |
|---|---|---|---|---|---|
| 11 | start_game | s2c | 中间新增 `editor_level_migration_version`(u8) | 中间插入 | 已翻译 |
| 11 | start_game | s2c | `GameType` 在官方 changelog 中标注重新编号 | 值映射 | 无需处理（实测 2193 已在用 2225 值：world_gamemode=0 与 server.properties survival 一致；映射反而把 survival 改成 -1） |
| 11 | start_game | s2c | `block_pallette_checksum` 跨版本不一致 | 字段改写 | 已翻译（清 0 跳过校验） |
| 12 | add_player | s2c | `passenger_of_block` 插在 `device_id` 前 | 中间插入 | 已翻译 |
| 12 | add_player | s2c | `BuildPlatform` 枚举重新编号（官方 changelog） | 值映射 | 无法确定（缺少真实客户端外观证据） |
| 12 | add_player | s2c | `gamemode`(GameType) 重新编号（官方 changelog） | 值映射 | 无法确定（同上） |
| 13 | add_actor | s2c | 末尾新增 `passenger_of_block` | 尾部追加 | 已翻译（追加 0x00） |
| 27 | entity_event (ActorEvent) | s2c | 官方 changelog 标注 ActorEvent 枚举重编号 | 值映射 | 无法确定（实测 2193 枚举与官方 from 值不一致，未启用映射） |
| 33 | interact | c2s | 官方 changelog 标注 Action 1→3/2→4/3→5/4→6 | 值映射 | 无需处理（探针实测：2193 接受 action=4；改写成 2 会触发 invalid enum 踢人，已回退） |
| 44 | animate | 双向 | 末尾新增 `hand`(u8) | 尾部追加 | 已翻译（s2c 补、c2s 裁） |
| 44 | animate | 双向 | 官方 changelog 标注 Action 重编号 | 值映射 | 无需处理（minecraft-data 1.26.51/60 枚举一致；实测 swing_arm 正常） |
| 58 | level_chunk | s2c | 末尾新增 `client_biome_update`(bool) | 尾部追加 | 已翻译 |
| 58 | level_chunk | s2c | 2193 旧缓存模式（sub_chunk_count=0+blob）2225 不识别 | 语义改写 | 已翻译（skeleton + SubChunkRequest） |
| 63 | player_list | s2c | 记录级新增 `play_fab_id`，`Skin` 内删除同字段 | 中间插入+删除 | 已翻译 |
| 63 | player_list | s2c | `BuildPlatform` 枚举重新编号 | 值映射 | 未处理（外观字段） |
| 93 | player_skin | s2c | `Skin` 结构变化（移除 play_fab_id） | 中间删除 | 未处理（本服未观测发送） |
| 49 | inventory_content | s2c | `ContainerSlotType` 实测只新增 68-71（reserved），0-67 未重编号 | 枚举追加 | 无需处理（真实会话 window_id=inventory，解析正常） |
| 50 | inventory_slot | s2c | 同上 | 枚举追加 | 无需处理（真实会话 5 个包解析正常） |
| 30 | inventory_transaction | 双向 | 官方 changelog：`item_use_on_entity` 新增 hand、`item_release` 末尾新增 hand | 中间插入+尾部追加 | 待实测（真实会话 4 个包全部是 normal 类型，未触发） |
| 317 | container_registry_cleanup | s2c | `ContainerSlotType` 仅新增 68-71 | 枚举追加 | 无需处理（未观测发送） |
| 147 | item_stack_request | c2s | `craft_reserved` 插入为 16，原 16/17 后移为 17/18；craft_reserved 携带 reserved_id/times_crafted | 枚举插入+负载变化 | 待实测（真实会话未出现 ISR，未做映射） |
| 144 | player_auth_input | c2s | 内嵌 ItemStackRequest 同上 | 嵌套结构 | 待实测（本次会话未携带） |
| 148 | item_stack_response | s2c | `ContainerSlotType` 仅新增 68-71 | 枚举追加 | 无需处理（未观测发送） |
| 145 | creative_content | s2c | 官方 changelog 标注 CreativeCategory 0..4→1..5；minecraft-data 60 结构未变 | 值映射 | 待实测（真实会话 3591 项/235 组解析正常，分类为 construction/nature/equipment/items；未改值） |
| 52 | crafting_data | s2c | `MolangVersion` 重新编号 | 值映射 | 无法确定（本服未观测到客户端异常） |
| 77 | command_request | c2s | `CommandOriginData`/`CurrentCmdVersion` 重新编号 | 值映射 | 未处理（仅输入命令时） |
| 79 | command_output | s2c | `CommandOriginData` 重新编号 | 值映射 | 未处理（仅命令输出） |
| 184 | agent_action_event | 双向 | `AgentActionType` 整体 +1 | 值映射 | 无需处理（教育 Agent，本服不用） |
| 160 | legacy_telemetry_event | s2c | `ActorType` 枚举新增/重编号 | 值映射 | 无需处理（遥测） |
| 5 | disconnect | s2c | `DisconnectFailReason` 新增 150 | 枚举追加 | 无需处理 |
| 61 | change_dimension | s2c | `Loading Screen Id` 变为 optional | 尾部可选 | 无需处理（未观测发送） |
| 137 | education_settings | s2c | 多个字段 optional | 尾部可选 | 无需处理（教育模式） |
| 198 | camera_presets | s2c | 大量字段变为 optional | 尾部可选 | 部分（模拟客户端可解析；待真实客户端确认） |
| 300 | camera_instruction | s2c | 大量字段变为 optional | 尾部可选 | 部分（未观测发送） |
| 345 | clientbound_attribute_layer_sync | s2c | 结构大改（AttributeLayerData/EnvironmentAttributeData） | 中间重写 | 未处理（本服未观测发送） |
| 348 | clientbound_update_sound_data | s2c | 字段 `Stop`→`Event`（同一 oneOf） | 重命名 | 无需处理（线路格式兼容） |
| 326 | graphics_override_parameter | s2c | Float/Vec3 变为 optional | 尾部可选 | 无需处理（未观测发送） |
| 146 | game_test_request | c2s | `Rotation` 枚举重编号 | 值映射 | 无需处理（测试包） |

## 三、确认无 schema 变化（不需要翻译）

以下为本次重点排查的音频/视觉/UI 包，官方 1.26.60 changelog 与 minecraft-data 递归 diff 均未发现结构变化：

- 音频：`play_sound(86)`、`stop_sound(87)`、`level_sound_event(123)`、`level_sound_event_v2(120)`、`level_sound_event_old(24)`
- 视觉/环境：`level_event(25)`、`level_event_generic(124)`、`spawn_particle_effect(118)`、`set_time(10)`、`set_difficulty(60)`、`update_block(21)`、`update_subchunk_blocks(110)`
- UI/文本：`text(9)`、`modal_form_request(100)`、`modal_form_response(101)`、`toast_request(186)`、`boss_event(74)`、`set_scoreboard_identity(112)`、`available_commands(76)`
- 实体：`add_actor(13)`（仅尾部补字段）、`remove_entity(14)`、`set_entity_data(39)`、`set_entity_motion(40)`、`mob_equipment(31)`、`move_entity_delta(111)`、`player_list(63)`（仅记录结构，已翻译）

## 四、当前构建已实现的翻译（汇总）

1. Login/RequestNetworkSettings 协议号 2225→2193（原始包层）
2. StartGame：editor 字段中间插入 + block_pallette_checksum 清 0
3. AddPlayer：passenger_of_block 中间插入
4. AddActor：passenger_of_block 尾部追加
5. Animate：hand 双向处理（Action 枚举经实测无需映射）
6. PlayerList：play_fab_id 迁移
7. LevelChunk：client_biome_update + skeleton 语义
8. c2s client_cache_status 强制 false

> 重要验证结论：官方 changelog 中 GameType / Animate Action / Interact Action / ActorEvent 的 “from → to” 值与 BDS 1.26.51 实际枚举不一致。实测 interact action=4 在 2193 被接受，映射成 2 反而触发 `invalid enum value` 并以 kicked_for_exploit 踢人；已回退这些映射，仅保留 animate hand 字段处理。后续若要处理枚举，必须以真实客户端+服务端实测为准。

## 六、物品与背包档位实测观察（2026-11-09 06:3x）

数据来源：`d:\ylcs\diagnostics\run6\session-4-s2c.log*`（真实 26.62 会话 02:04:30-02:05:37）。

- `inventory_content(49)` ×4：`window_id=inventory(0)`，1.26.60 解码正常。
- `inventory_slot(50)` ×5：解码正常。
- `inventory_transaction(30)` ×4：全部 `normal` 类型，不含 item_use_on_entity / item_release。
- `creative_content(145)` ×1：3591 项 / 235 组，分类为 construction / nature / equipment / items，1.26.60 解码正常。
- `item_stack_request(147)`：0 次；`player_auth_input(144)` 内嵌 ISR：0 次。

结论：
1. `ContainerSlotType` 在真实数据里只新增 68-71（reserved），观察到的容器 ID 不需要翻译，inventory_content / inventory_slot / item_stack_response / container_registry_cleanup 保持直通。
2. `creative_content` 结构未变，分类值未改；官方 changelog 的 CreativeCategory 重编号与 minecraft-data 不符，标为待实测，不盲改。
3. 真正待验证的是 `item_stack_request` 动作 16/17/18 位移与 `inventory_transaction` 的 item_use_on_entity/item_release `hand` 字段——本次会话未触发，保持 TODO，等真实客户端开背包/合成/丢弃后再抓包决定。

## 八、版本链结构（2026-11-09 19:2x）

代码改为「相邻版本步骤」注册表，位于 `mod/JERCPLD.Mod/src/JerCpldMod.cpp`：

```cpp
struct VersionStep {
    int         serverProtocol;                  // 本实例 BDS 协议号（config: serverProtocol）
    int         clientProtocol;                  // 客户端协议号
    char const* name;
    void (*outbound)(std::string&, size_t, int);  // s2c：服务端格式 -> 客户端格式
    void (*inbound)(std::string&);                // c2s：客户端格式 -> 服务端格式
};
```

- 路由：由 PTL 引擎按已加载的版本对建图（现在主线只有 `2193↔2225` 一跳），未命中即直通，不做任何改写。
- 锚点：`gServerProtocol` 来自 config，默认 2193；原始包协议号改写（`rewriteRawProtocol`）和 s2c 门槛都用它，不再写死。
- 每个连接仍按 peer 记录客户端协议号（`gTranslatedPeers`），避免把 2225 的改写套到低版本连接上。

### 已注册的版本对（主线）

| 版本对 | 方向 | outbound（s2c） | inbound（c2s） |
|---|---|---|---|
| 2193 ↔ 2225 | 双向 | StartGame 插 editor + checksum 清 0；AddPlayer 插 passenger_of_block；AddActor/Animate 补尾部；LevelChunk skeleton + 尾 bool；PlayerList play_fab_id 迁移 | client_cache_status 强制 false；animate hand 裁剪 |

> `2169/2168/1001` 的历史改写代码与 `2169-2193.json` 仅作社区示例保留（`examples/PTL/`），不在官方支持范围；26.45 因 RakNet/NetherNet 传输互斥无法连接，见 BLOCKERS B4。

### 新增一个版本的步骤

1. 在 `plugins/JERCPLD/PTL/` 新建 `<旧协议>-<新协议>.json`（格式见 `docs/PTL.md`）。
2. 对称差异用 `rules` + `addedIn`；不对称差异用 `overrides` 分方向写。
3. 需要整包重编码时用 `builtin`，并在 `docs/PTL.md` 的内置能力表登记。
4. 把该版本加入 `acceptMinProtocol` / `acceptMaxProtocol` 范围。
5. 用 `tools/verify-client` 跑该版本回归，并抓包核对字段。

### 反向实现状态（如实）

- 已实现并做了**算法级往返验证**（取正向翻译后产生的 2225 布局抓包，套用反向算法，再用 1.26.51 schema 解码）：
  - StartGame：删除 `editor_level_migration_version` → 2193 解码 OK，`block_properties=1295`，无 editor 字段。
  - LevelChunk：删除尾部 `client_biome_update` → 2193 解码 OK（注意 `cache_metadata` 的数组长度字段始终存在，与 `cache_enabled` 无关）。
- 已实现但**未验证**：`animate` 补/删 `hand`（需 2225 服务端实测）。
- 明确 TODO（缺 2225 服务端抓包，未写）：AddPlayer 的 `passenger_of_block` 反解、AddActor 尾字段反解、PlayerList 的 `play_fab_id` 回填。
- 反向链路**没有跑通真实服务端**：本机唯一 2225 二进制在 `D:\JERCMG\tools\bds\bedrock_server.exe`（属另一个项目，未改动），但本机只有 LeviLamina 26.51.5，缺少 26.60 的 LeviLamina 与符号解析，mod 无法在其中加载。反向能力目前只是代码 + 离线往返验证，不能宣称可用。

### 子区块调色板 v8 / v9 调查结论

- v8 = Limited、v9 = Limitless，区别是 v9 多了 **subchunk index**（y 索引）字节，不是 2193/2225 的版本分界。
- 两版都是 Caves & Cliffs 之后，网络下发均为 Limitless 布局；2193 抓包中的区块 payload 也是同一格式（本轮抓到的区块为仅生物群系区块，payload 34 字节，未包含方块调色板）。
- 结论：反向不需要做 v8↔v9 转换；真正差异仍是 LevelChunk 尾部的 `client_biome_update`。此项**待 2225 服务端抓包确认**，确认前不写调色板转换代码。

## 九、下一步（按用户给定优先级）

1. 移动/基础交互：真实客户端复测（走动/跳跃/挖掘/放置/开门/攻击）。
2. 方块与地形质量：SubChunk 内自定义方块显示验证。
3. 物品与背包：item_stack_request 的 craft_reserved、ContainerEnumName、creative_content 分类、inventory_transaction 的 hand 字段。
4. 实体：PlayerSkin(93) 的 Skin 重写、BuildPlatform 值映射、AddPlayer gamemode。
5. 其余系统：command 枚举、camera optional 实测、attribute layer sync。

## 十、全量 schema 穷举校验（2026-11-11）

上一版清单基于官方 changelog 摘要与部分结构 diff，存在「无法确定」「待实测」等缺口。本轮把 `tools/verify-client/diff-schemas.js` 改为**穷举模式**：遍历 minecraft-data 1.26.51 / 1.26.60 的全部 254 个 `packet_*` 类型，递归展开嵌套类型（container / switch / option / array / mapper / enum / bitflags），并从 `mcpe_packet` 反查真实包 ID。

用法：`node diff-schemas.js`（全部）或 `node diff-schemas.js <包名片段>`（单包）。

结果：**同名包中 17 个结构有差异，2225 新增 8 个包**。逐包结论如下（按包 ID 排序）。

| ID | 包 | 2193→2225 实际差异（schema 证据） | PTL 现状 |
|---|---|---|---|
| 5 | disconnect | `reason` 枚举新增 150 sub_client_login_not_bound | 无需处理（枚举追加） |
| 11 | start_game | `editor_level_migration_version` 插在 `exported_from_editor` 之后 | 已翻译（builtin） |
| 12 | add_player | `passenger_of_block`(option) 插在 `links` 之后、`device_id` 之前 | 已翻译（仅 2193→2225；反向未实现） |
| 13 | add_entity | `passenger_of_block`(option) 为最后一个字段 | 已翻译（尾部补/裁 1 字节） |
| 25 | level_event | `event` 枚举新增 16489 add_particle_ice_ball_break | 无需处理 |
| 30 | inventory_transaction | `item_use_on_entity.hand`、`item_release.hand`（u8）新增 | **未覆盖**（需 builtin，c2s 高频风险） |
| 44 | animate | `hand` 为最后一个字段 | 已翻译 |
| 49 | inventory_content | `container_id` 枚举新增 68-71 reserved | 无需处理 |
| 50 | inventory_slot | 同上 | 无需处理 |
| 58 | level_chunk | 尾部 `client_biome_update` + 缓存语义 | 已翻译 |
| 93 | player_skin | `skin.play_fab_id` 被删除 | **未覆盖**（需 builtin 删除） |
| 137 | education_settings | agent_capabilities 收缩 | 无需处理（教育模式） |
| 144 | player_auth_input | `item_stack_request.actions` 枚举插入 `craft_reserved`(16)，原 16/17 后移，并新增该分支负载 | **未覆盖**（需 builtin，c2s 背包操作触发） |
| 180 | dimension_data | 尾部新增 `cloud_height`(varint) + `render_clouds`(bool) | **未覆盖**（可声明式补齐，尚未上线） |
| 300 | camera_instruction | 多处 optional 化（穷举工具未能定位到单字段，属 option 包裹） | 未覆盖（低风险，未观测发送） |
| 315 | serverbound_diagnostics | `system_categories` 由 array 变 option | 未覆盖（c2s，诊断包，罕见） |
| 348 | clientbound_update_sound_data | 结构重构：`stop/volume/pitch/fade/seek_to/pause/resume` 删除，改为 `type` 枚举 + switch 负载 | 未覆盖（需 builtin） |

2225 新增包（真实 ID）：353 clientbound_matchmaking_state、354 serverbound_stonecutter_set_recipe、355 clientbound_stonecutter_set_recipe、356 serverbound_matchmaking_cancel、357 set_passenger_of_block（双向）、358 serverbound_cursor_item_drag、359 clientbound_play_audio_content、360 serverbound_register_audio_content。

### 结论与风险排序

1. **进入世界链路已全覆盖**：login/network_settings（原始包层改协议号）→ start_game → level_chunk → animate/move 均有规则，与当前 B15（资源包下载卡住）无关。
2. **c2s 未覆盖项风险最高**：30（用剪刀/弓箭等对实体使用或释放物品）、144（背包拖拽/合成）一旦触发，2193 服务端按旧结构解析 2225 负载，可能直接踢人或错位。这两项必须优先补。
3. **2225 新增 c2s 包**：354/358/360（以及 357）由 2225 客户端主动发送，2193 服务端不认识该包 ID，当前 mod 没有「丢弃未知包」能力；需在 c2s 入口按协议号做白名单丢弃。
4. **可声明式补齐的只有 180**（appendBytes `0000`）；30/93/144/348 需 builtin 重编码，且反向（2193→2225 服务端）仍缺 2225 服务端抓包（本机 2225 二进制为 D:\JERCMG 的 vanilla BDS，无法注入 LeviLamina）。
5. 建议实施顺序：c2s 未知包丢弃 → 30 的 hand 裁剪 → 180 尾部补字段 → 93/144/348。每项按项目验收门槛，先在模拟客户端 26.62→26.51 跑通再替换现网 DLL。

# JERCPLD 夜间自主推进交接（2026-11-12 深夜）

## 2026-11-13 01:00 更新：2168 链式路由已上线

- **2168↔2169 空档已补**：`PTL/2168-2169.json`，`packets=0`；依据 minecraft-data 1.26.40 vs 1.26.45 深对比：**244→244 包，0 类型差异，0 ID 差异**。
- **链式语义已确认**：引擎逐档应用，不做“一次跳到最终格式”。2168 客户端实际走：
  - c2s `route 2168->2193 hops=2 [2168->2169, 2169->2193]`
  - s2c `route 2193->2168 hops=2 [2193->2169, 2169->2168]`
  - 2168 与 2169 布局完全一致，所以空档是 no-op，第二跳拿到的仍是正确的 2169 布局；最终字节对 2168 客户端成立。
  - 26.45 仍是直连：`route 2169->2193 hops=1`、`route 2193->2169 hops=1`。
- **模拟验证**：1.26.40 NetherNet 客户端 PASS（StartGame 3165ms、6 chunks、无 kick）；1.26.45 PASS（StartGame 2977ms、6 chunks），确认加档未引入回归。
- **现网**：PID **20108**（00:50:56 启动），TCP/UDP 7551，00:50:57 `Signed in`，`/v1/join` 200 `players=0`；PTL 3 pairs（2168-2169=0、2169-2193=12、2193-2225=7）；`acceptMinProtocol=2168`；DLL 729,600B / PTL 模块 14,336B。
- **下一步**：真实 1.26.40/43/44/45 客户端复测；RakNet 仍压到最低，26.30（1001）只能走 RakNet。

## 2026-11-13 06:50 更新：真机 -63（BlockMismatch）根因与修复

- **真机会话**：06:41:06 协议 **2169**，直连 1 跳 `2169->2193`；收到 StartGame；06:41:09 断开 reason=122；客户端显示 `-63`（`BlockMismatch=63`）。
- **根因**：实抓 06:41 的 StartGame（73901B）中 `block_pallette_checksum = d26cacf7238f1ce2`（非 0）。`2169-2193` 没有 packet 11 规则，因此 2169 直连和 2168 两跳都未清零；只有 `2193-2225` 有 `start_game_editor_checksum`。
- **修复**：新增 `start_game_zero_checksum` builtin（只清校验值，不动布局）+ `2169-2193` 的 packet 11 `newerToOlder` 规则。2168 两跳时在 `2193->2169` 第一跳生效，2168 客户端同样受益。
- **隔离验证**（spike `D:\ylcs_spike`，nethernet 19252）：
  - 1.26.45：PASS，StartGame 2745ms、spawn=true、257 chunks；`packet=11(start_game) 2193->2169 len 73893->73893`，checksum 全 0。
  - 1.26.40：PASS，StartGame 2780ms、spawn=true、257 chunks；路由 `2168->2193 hops=2`，checksum 全 0。
- **模拟边界**：bedrock-protocol 只解析、不校验 `block_pallette_checksum`，所以 PASS 本身不能证明真机不 -63；证据是抓包中该 8 字节已清零且包长未变。最终以真机复测为准。
- **现网**：PID **19336**（06:48:40 启动），TCP/UDP 7551，06:48:43 `Signed in`，`/v1/join` 200 `players=0`；PTL 3 pairs（2168-2169=0、2169-2193=13、2193-2225=7）。

## 2026-11-13 15:05 更新：-122（KickedForExploit）根因与 144 修复

- **真机会话 14:46**：2169 直连；StartGame `73901->73901`（-63 已消失）；最后 c2s 是 `144 player_auth_input len=99`；服务端随后下发 s2c `156 violation_warning`（`packet_id=144`、`invalid enum value`），`onDisconnect reason=56 KickedForExploit`。
- **根因纠正**：2169 的 `input_data` 实际是 **3 字节小端 bitfield**，不是此前推测的 8 字节。真机样本 `0x600101` → bits {0,8,21,22} → `ascend, sneaking, ascend_block, descend_block`；tick 在 offset 48，modes=`touch/normal/touch`。2193 服务端期待 `array<InputData>`，按数组读会得到 count=1、value=-1，直接触发非法枚举。
- **尾部差异**：2169 在 delta 之后是 5 个 `option<bool>`，真机样本全为 `(present=1, false)`，共 10 字节；2193 对应 5 个 option 字段，本规则统一转成 absent。遇到 `true` 显式失败，不猜载荷。
- **规则**：`144` 的 `olderToNewer` 增加 `player_auth_input_to_2193`；内置 array-form 检测，已经是 2193 array 的包原样放行（模拟客户端 `92->92` PASS）。
- **字节验证**：真机 99B 样本转换后 96B，1.26.51 解包成功，`input_data=["ascend","sneaking","ascend_block","descend_block"]`，modes=`touch/normal/touch`，tick=6。隔离 spike 1.26.45 `--move --animate` PASS（spawn、257 chunks、144 `92->92` 未改写）。
- **现网**：PID **27804**，TCP/UDP 7551，15:05:36 `Signed in`，`/v1/join` 200 `players=0`；PTL 3 pairs（2169-2193=**14**）。
- **边界**：3 字节 bitfield 覆盖 0..23；若后续真机出现 24+ 位或 option=true，规则会显式失败并记录 bits，需要下一轮补。

## 2026-11-13 15:12 更新：144 bitfield 是变长（3/4 字节），已改为动态宽度

- **15:06 真机会话（15:05 构建，非“部署前”）**：前 3 个 `144` 成功 `99->96`（bits `0x600101`）；第 4 个 `144` 为 **100 字节**，固定 3 字节规则失败，原样送 BDS，随后 `onDisconnect reason=56`。
- **100B 样本**：`input_data` 是 **4 字节** `0x60300201`，bits {0,9,20,21,29,30} = `ascend, sneak_down, sprinting, ascend_block, start_swimming, stop_swimming`；modes=`touch/screen/touch`，tick=50。说明 bitfield 按最高置位取**最小字节数**，不是固定 3 或 8 字节。
- **改法**：候选宽度 1..8 逐档验证后续字段（3 个 modes、interact_rotation、tick、delta、5 个 `option<bool>`、28B 固定尾），取第一个完整消费的宽度；2193 array 布局仍原样放行。
- **验证**：99B→96B（w=3）、100B→98B（w=4），两次 1.26.51 解包成功；模拟 array 144 `92->92` PASS。
- **现网**：PID **28664**，TCP/UDP 7551，15:11:31 `Signed in`，`/v1/join` 200 `players=0`；PTL 3 pairs（2169-2193=14）。
- **崩溃取证结论**：hook.log 中没有“StartGame + 一堆 58 后无 onDisconnect”的会话；14:46（旧构建）和 15:06（15:05 构建）都以 `reason=56` 结束。服务端证据只能证明被踢，不能证明客户端进程崩溃。S2C 大包为 `58 len=21`、`56 len=169`、`67 len=65573`（地图数据）、StartGame 73901B，没有 OOM 级数组；未发现“结构合法但内容毒”的 s2c 证据。

## 结论总览

| 项 | 状态 | 证据 |
|---|---|---|
| P1 符号探测 / 路线 A 可行性 | **通过：A 可行（符号与运行时均确认）** | `$onNewIncomingConnection`=0x7ff78e76bb00、`NetworkConnection::$ctor`=0x298e5734de0（隔离实例 hook.log `[SYMPROBE]`） |
| P2a BDS 接受自定义 peer 为连接 | **通过** | 隔离实例 hook.log：`[CONNPROBE] onNewIncoming=true conn=0x276b19d16c0` |
| P2b RakNet 包真正进入 BDS 处理器 | **未完成（硬阻塞）** | 直接注册成功；延迟到后台线程后连接未被网络 tick 服务，注入的 `RequestNetworkSettings` 未出现在 handler 日志 |
| P3 版本号补全 | **部分完成** | 1.26.30/40/45/51/60 已确认；43/44/50/61/62 未确认（见下表） |
| P4 26.30–26.45 PTL 相邻档 | **未完成（有明确证据与下一步）** | 40→45 schema 0 差异；45→51 有 2 新增 + 5 变更，需要完整规则，不能空档硬翻 |
| 现网可用性 | **保持** | PID 2344、TCP/UDP 7551、Signed in、translation=ON、规则来源=模块、公网 `/v1/join` 200 players=0 |

## P1：路线 A 可行性（确定结论）

- `LeviLamina.def` 导出 `NetworkSystem::getConnectionFromId` 与 `ll::service::bedrock::getNetworkSystem`；不导出 `$onNewIncomingConnection` / `NetworkConnection::$ctor`。
- BDS 本体只导出 5 个符号；但 LL 的符号解析器能解析内部 BDS 符号（现有 `$handle`/`receivePacket` hook 已证明）。
- 用 MSVC 编译探针生成精确 mangled 名，并在隔离实例运行时解析：
  - `?$onNewIncomingConnection@NetworkSystem@@QEAA_NAEBVNetworkIdentifier@@$$QEAV?$shared_ptr@VNetworkPeer@@@std@@@Z` → **非空**
  - `?$ctor@NetworkConnection@@QEAAPEAXAEBVNetworkIdentifier@@V?$shared_ptr@VNetworkPeer@@@std@@_NV?$NonOwnerPointer@VIPacketObserver@@@Bedrock@@AEAVScheduler@@2AEBUNetworkSettingOptions@@V?$shared_ptr@VIPacketSecurityController@@@4@@Z` → **非空**
- **结论：路线 A 可行**（符号可定位、可调用；`onNewIncomingConnection` 是公开虚函数，返回 bool）。

## P2a：BDS 接受注入连接

- 在 mod 中实现 `ProbeNetworkPeer : NetworkPeer`，调用 `ll::service::bedrock::getNetworkSystem()->onNewIncomingConnection(id, std::move(peer))`。
- 隔离实例日志：
  ```
  [SYMPROBE] onNewIncomingConnection=0x7ff78e76bb00 NetworkConnection_ctor=0x298e5734de0 getConnectionFromId=0x0
  [CONNPROBE] onNewIncoming=true conn=0x276b19d16c0
  ```
- 说明 BDS 真正创建并登记了 `NetworkConnection`。
- ABI 注意：BDS 不导出 `NetworkPeer` 基类的 `~NetworkPeer/update/flush/isLocal/isEncrypted/isLan` 与 `NetherNet::NetworkID::NetworkID()`；探针在本地提供了最小 shim 才能链接。**这些 shim 只在探针构建中验证过，未安装到现网。**

## P2b：为什么没完成

- 在 `enable()` 里直接注册（主线程）成功；把注册延迟到后台线程（12s 后）后，注入的 `RequestNetworkSettings`（`fe06c10100000879`）**没有**触发 `request_network_settings handler hit`。
- 判断：不是符号问题，而是 **BDS 网络 tick / 连接生命周期要求主线程或真实传输层事件**；合成的 peer 没有进入服务器更新循环。
- 下一步（明早第一优先）：
  1. 用 LL 的 `ServerThreadExecutor`/服务器 tick 事件在主线程调度 `onNewIncomingConnection`，不要用裸 `std::thread`；
  2. 若仍不被 tick，改为 hook 真实 NetherNet/RakNet 的连接创建点，把真实 peer 替换为 RakNet peer（路线 D 的收敛版），或走路线 C（RakNet→NetherNet 桥）；
  3. 只有连接被 tick 服务后，才接 Node RakNet helper 的 IPC 数据面。

## P3：版本号表

| 版本 | 协议号 | 状态 |
|---|---|---|
| 1.26.30 | 1001 | 已确认（minecraft-data；spike 日志 1001→2193） |
| 1.26.40 | 2168 | 已确认（minecraft-data；spike 日志 2168→2193） |
| 1.26.43 | 未确认 | 数据源无条目；位于 2168–2169 之间 |
| 1.26.44 | 未确认 | 同上 |
| 1.26.45 | 2169 | 已确认（RakNet 探针实测 `fe06c10100000879`） |
| 1.26.50 | 推测 2193 | 未确认；数据源无条目 |
| 1.26.51 | 2193 | 已确认 |
| 1.26.60 | 2225 | 已确认 |
| 1.26.61 | 推测 2225 | 未确认 |
| 1.26.62 | 推测 2225 | 未确认（现网 26.62 登录被改写为 2225，间接证据） |

## P4：PTL 相邻档取证

- 用 minecraft-data 的 `protocol.json` 对比：
  - **1.26.40 → 1.26.45：244→244 包，added=0 removed=0 changed=0**（schema 完全一致，只有协议号 2168/2169 不同）。
  - **1.26.45 → 1.26.51：244→246 包**；新增 `packet_set_player_furnace_options`、`packet_record_started`；变更 `packet_boss_event`、`packet_play_sound`、`packet_move_entity_delta`、`packet_dimension_data`、`packet_serverbound_pack_setting_change`。
- 不能直接加空档：`2169-2193` 现有规则只覆盖 camera preset，若把它当完整档会让 5 个变更包被静默跳过（半程硬翻）。
- 下一步：
  1. 对 5 个变更包做字段级 diff，写 PTL 规则；
  2. 对 2 个新增包，需要“对老客户端丢弃/不发送”的引擎能力（PTL 目前没有 drop 规则），否则 26.45 客户端会收到未知包；
  3. 增加“规则覆盖率/完整性”检查，缺包即拒绝进链；
  4. ~~`2168-2169` 可以做成显式空档（有 schema 0 差异证据），但必须等 `2169-2193` 完整后再启用链。~~ **已完成（2026-11-13 00:50）：2169-2193 已补齐 12 包，空档已启用并通过 1.26.40 模拟验证。**

## 现网状态（2026-11-12 19:50 历史快照，已被顶部 01:00 更新取代）

- BDS：PID **2344**（2026-11-12 19:50:24 启动），`D:\ylcs\bedrock_server_mod.exe`，TCP 7551 + UDP 7551。
- 公网：`http://<server-ip>:7551/v1/join` → 200，`players=0`。
- `Signed in to signaling service successfully`、`translation=ON`、`规则来源=模块 JERCPLD.PTL.dll`。
- 现网 DLL：688,128B，SHA256 `C30D5BC6...`（**未安装探针构建**；探针构建 700,928B 只在 spike/`build` 目录）。
- 资源包：`world_resource_packs.json`/`world_behavior_packs.json` 均为 `[]`（Pack Stack None）；还原见 BLOCKERS B20。
- PackCDN：python http.server 19132（PID 14472）仍在运行；RakNet relay 任务 Disabled。
- 隔离实例 `D:\ylcs_spike`：已停止，DLL/config 已恢复为探针前状态。

## 明早验收清单（按顺序）

1. **现网健康**：PID 2344、TCP/UDP 7551、`Signed in`、`translation=ON`、规则来源=模块、公网 `/v1/join` 200、`players=0`。
2. **真实 26.62 客户端**：连 `<server-ip>:7551`，确认无资源包下载卡住（Pack Stack None）并能进世界；地形/移动/物品栏按 B8 验收。
3. **真实 26.45 客户端（RakNet）**：先运行 `d:\ylcs_spike\raknet_probe\probe.js`（UDP 19132），确认握手与 `RequestNetworkSettings` 协议 2169；再按 P2b 下一步接 BDS。
4. **P2b 修复**：主线程调度/真实连接 hook，目标“注入连接被 BDS tick 服务，`request_network_settings handler hit` 出现 2169”。
5. **P4**：完成 2169-2193 的 5 个变更包 + 2 个新增包策略；加完整性检查后再启用 2168-2169 空档。
6. **不要**把探针构建安装到现网；不要重新启用 19132 relay；不要改 `server-port=7551`。

## 关键文件/命令

- 设计：`docs/RAKNET_TRANSPORT_DESIGN.md`
- 本交接：`docs/HANDOFF.md`
- 隔离探针：`d:\ylcs_spike\raknet_probe\probe.js`（Node RakNet server + 模拟 1.26.45）
- 符号探针：`d:\ylcs\_tools\symprobe\symprobe6.cpp` / `build6.bat`
- 现网健康：
  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='bedrock_server_mod.exe'"
  netstat -ano | Select-String ':7551'
  curl.exe -s http://<server-ip>:7551/v1/join
  ```
- 探针构建：`mod\JERCPLD.Mod\build\JERCPLD.dll`（700,928B；**不要装现网**）

## 2026-11-13 17:09 更新：开箱根因（区块缓存）修复 + 翻译性能计数

- **箱子坐标结论**：`(-1335,64,7909)`/区块 `(-84,494)` 在失败会话里 **没有任何服务端方块数据**。`level_chunk(58)` 为 `sub_chunk_count=0, cache_enabled=true, blobs=1, payload=1B(00)`；无任何 `174` 覆盖该 subchunk。客户端用本地区块缓存渲染该位置，看到的是**非容器方块**（可能为放置箱子前的旧方块），因此：
  - `33` 只有 `action=4 mouse_over_entity`；
  - `36` 是 `action=28/29 StartItemUseOn/StopItemUseOn`，带 `block_pos=(-1335,64,7909), face=2`；
  - 客户端从未发 `open_inventory(6)`。
- **缓存协商证据**：c2s `135 ClientCacheBlobStatus` 首次（15:06:33）把该区块 blob `0xb72c1253050e6210` 报为 miss，但服务端 `136 ClientCacheMissResponse len=3`（空）没有补数据；后续 15:11/15:25/15:46/15:55/16:24 会话客户端改为报 hit，继续用本地缓存。
- **调色板无偏移**：`block-network-ids-are-hashes=true`，`start_game.block_properties=98`（全部 concrete/wool stairs/slab，无 chest、无自定义），hash 模式下不存在索引整体偏移；`minecraft:chest` hash=`741882976`（`data/generated/block_states.2193.json`）。
- **修复**：`2169-2193.json` 新增 packet `129` 规则 `client_cache_force_off`（c2s 强制 `enabled=false`）。
  - 模拟 1.26.45（2169）验证：`level_chunk` 变为 `cache=false, blobs=0`，空区块 payload 34B、有方块区块 payload 1064B；START_GAME 后 6–7 区块、无踢出。
  - 真机必须在改后**重新登录**（129 登录时发送）。
- **性能计数**：`perfStatsEnabled`（默认 false），每 30s 输出 `[JERCPLD][PERF]`，含每包次数/总耗时/最大耗时/字节/规则命中、P50（log2 桶）、1 跳 vs 2 跳、tick 占比。
  - 2169 一跳登录窗口：96 包 / 3.59–3.69ms / tick 0.012%；最大 s2c 52（1.04MB）0.61–0.82ms。
  - 2168 两跳同窗口：98 包 / 3.87ms / tick 0.013%（约 +8%）。
  - 144/30/58/174/111 需真机会话采集；模拟客户端登录后不再发送 c2s。
- **现网**：PID **10888**（17:09:13 启动），TCP/UDP 7551，`/v1/join` 本地与公网均 200 `players=0`；DLL 759,296B；PTL 3 pairs（2168-2169=0、2169-2193=15、2193-2225=7）；`perfStatsEnabled=true`。
- **回滚**：`plugins/JERCPLD/JERCPLD.dll.bak-20261113-1700`、`PTL/JERCPLD.PTL.dll.bak-20261113-1700`、`config/config.json.bak-20261113-1700`。

## 2026-11-13 17:45 更新：144 跳跃帧宽度修复 + 按连接 CONN 日志

- **真机症状**：26.45 跳跃时蹲下键闪动，但角色能往上跳。
- **根因（现场抓包 17:13–17:15）**：跳跃帧的 `input_data` 位域宽度是 **10–11 字节**（bit 59–64 置位后带客户端填充），而转换器候选宽度只试 **1..8**，所有跳跃帧都落入 fallback；fallback 复制上一帧 `input_data`，把旧 sneaking 状态重复送进服务端 → 蹲下键闪动。
- **证据**：
  - 107B 帧只有 width=10 能通过尾部校验（tick=382）；108B 帧只有 width=11（tick=2657）；
  - 112B/120B 帧是带 block_action/item_stack_request 的帧，严格模式因 option=true 失败，宽松模式 width=6、tick=1499/1494 正确；
  - 用 1.26.51 解包器验证转换结果：107B→109B、108B→109B 均解析成功，`input_data` 只含 ≤65 的有效值。
- **修复**：
  1. 宽度候选 1..16；
  2. 新增 128 位拆分（`bitsLo/bitsHi`），只序列化 0..65；
  3. 宽松模式：可选载荷长度未知时丢弃 option 组，用「当前 input_data/modes/tick/delta + 帧尾 28B」重建；
  4. fallback 改为**清空 input_data**（保留 position/rotation），不再重复旧 sneaking。
- **模拟端边界**：bedrock-protocol 的 2169 客户端登录后 c2s 发送失效（move/原始 `sendBuffer` 都到不了服务端），无法做真实位域回放；已用「抓包样本离线回放 + 1.26.51 解包验证」替代。UI 是否还闪必须真机确认。
- **新增 `[CONN]` 日志**：连接建立/断开各一行，含翻译版本、跳数、144 处理方式、按连接累计耗时/tick。
  - 示例：`[JERCPLD][CONN] id=1 proto=2169->2193 hops=1 jump=input_data=变长位域(动态宽度1-16B),block_action=fallback合成,deprecated过滤=on perf=起始`
  - 断开：`perf=累计 x.xxxms (tick y.yyy%, pkts=N, bytes=A->B, dur=Ts)`
- **文档**：`docs/PTL.md` 增加能力矩阵与「为什么改引擎」的诚实交代；新增 `docs/REPO_SPLIT_PLAN.md`（引擎/PTL 双仓、ABI 向上兼容矩阵、A/B/C 方案、崩溃隔离边界）。
- **现网**：PID **18876**（17:44:53 启动），7551，`players=0`，DLL 784,384B；回滚备份 `JERCPLD.dll.bak-20261113-1742`。

## 2026-11-13 18:00 更新：144 input_data 真实结构纠正（不是位域）

- **真机反馈**：修复宽度后，蹲键持续闪烁，静止也在蹲/起切换，无法跳跃/起飞。
- **现场日志（17:48）**：相邻帧 `width` 在 3/4/5 之间跳变，`bits` 在 `0x300101`、`0x38300201`、`0x6438300301` 之间跳变；array 在 sneaking / sneak_down / start+stop_sneaking 之间抖动。
- **更深层根因**：**2169 的 `input_data` 不是位域**。真机结构是：
  - `varuint blobCount`（通常 1）
  - 每个 blob：`varuint length` + `length` 字节的 **zigzag InputData 值**
  - 之后才是 input_mode / play_mode / interaction_model / interact_rotation / tick / delta / option 组 / 帧尾 28B
  之前的「变长位域 + 宽度搜索」模型从一开始就是错的，所以无论怎么调宽度都会误判出 sneaking。
- **证据**（同一批抓包按列表结构解析，全部通过）：
  - 107B 跳跃帧：`jump_down, jumping, want_up, persist_sneak, start_jumping, block_breaking_delay_enabled, jump_pressed_raw, jump_current_raw`；
  - 108B：`sneaking, sneak_down, want_down, persist_sneak, start_sneaking, ..., sneak_released_raw, sneak_pressed_raw`；
  - 99B 静止帧：仅 `block_breaking_delay_enabled`；
  - 112B block_action 帧：`persist_sneak, block_action, block_breaking_delay_enabled, vertical_collision`。
  - 四帧转换后用 1.26.51 解包器验证全部 OK。
- **新实现**：`tryPlayerAuthInputList` 按长度前缀确定式解析，**不再搜索宽度**；严格模式（option 全 absent）失败才用宽松模式（丢弃未知载荷）；tick 不递增直接 fallback（清空 input_data，只保留 position/rotation）。同一帧重复解析字节一致。
- **日志**：`auth_input chosen tick=... opts=... values=N [...]`，前 30 帧附原始 hex。
- **现网**：PID **6640**（18:01:16 启动），7551，`players=0`，DLL 785,920B；回滚备份 `JERCPLD.dll.bak-20261113-1755`。

## 2026-11-13 18:56 更新：144 block_action 真翻译上线

- **结构（抓包定稿）**：2169 option 区第 3 组为 block_action：
  `present(1) + outerCount(varint, 样本均 1) + innerCount(varint) + entries[action zigzag32, pos 3×zigzag32, face zigzag32]`
  2193 目标为扁平的 `option<array<{action,position,face}>>`（没有 outer 层）。
- **样本**：
  - 112B：inner=1，action=27 continue_break，pos=(-1322,64,7917)，face=1；
  - 120B：inner=2，action=0 start_break / 26 predict_break，pos=(-1322,65,7917)，face=1。
- **验收（离线）**：
  - 112B→106B、120B→114B，用 1.26.51 解包器解析均 OK；
  - 同帧重复转换字节一致（deterministic=true）；
  - A→B→A 不适用（该规则是单向 2169→2193，引擎只对对称规则做往返自检）。
- **主路径回归**：模拟 1.26.45 进服 PASS（StartGame + 10 chunks）；`[CONN] ... block_action=已翻译`。
- **严格模式**：opt1/opt2 必须是 (present=1,false)；opt3 解析 block_action；opt4/opt5 必须是 (present=1,false)；只有 opt1/2=true 或 opt4/5 带载荷才退宽松。
- **现网**：PID **16900**（18:56:25 启动），7551，`players=0`，DLL 791,552B；回滚备份 `JERCPLD.dll.bak-20261113-1850`。
- **待办**：用户做四组 30 取样（空手右键 / 手持方块右键 / 手持方块左键破坏 / 手持工具左键破坏），每次停 1–2 秒；c2s_raw.log 会记录原始 hex。

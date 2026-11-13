# 阻塞与人工决策点

> 规则：同一失败方案最多尝试两次，第三次换路。需要人工决策的写入本文件，不静默降级。

## B1 NetherNet 自实现与插件形态冲突（已决策：取消）

- 结论：**取消整条 NetherNet 自实现主线**。
- 原因：产品形态改为 LeviLamina 进程内插件，传输完全由 BDS 负责，插件只处理包层。
- 处置：`docs/ROADMAP.md` 保留 C1 取消记录；原投入时间转入 S5 翻译正确性。
- 状态：已关闭。

## B2 自动验证客户端的账号与 online-mode（已解决）

- 处理：备份 `server.properties`（`server.properties.bak-jercpld-20261108-212343`）后改为 `online-mode=false`，并把 `transport` 临时设为 `raknet`。
- 结果：最小自检客户端通过（握手 → StartGame）。
- 注意：这是测试配置；正式环境默认 `transport=nethernet`，且用户会用真实客户端验收。
- 状态：已关闭（测试环境）。

## B3 客户端库对 1.26.x 版本的覆盖（已验证）

- PrismarineJS `bedrock-protocol 3.61.0` 支持 1.26.30 / 1.26.40 / 1.26.45 / 1.26.51 / 1.26.60。
- 状态：已关闭。

## B4 方块运行时哈希无法离线校验

- 背景：官方 `mojang-blocks.json` 只有 raw_id/serialization_id，无运行时哈希。
- 处理：最小自检已拿到 StartGame 的 1295 个方块属性，可据此交叉校验哈希。
- 状态：待 S5 处理。

## B5 C++ 依赖与编译环境

- 背景：LeviLamina 头文件依赖 entt / fmt / glm / gsl / leveldb / rapidjson / type_safe / expected-lite / stb 等第三方库。
- 处理：依赖已下载到 `mod/JERCPLD.Mod/deps`；用单头 entt + 补 `entt/fwd.hpp`；后续改用 bedrock-protocol codec 降低自写结构体量。
- 状态：处理中。

## B6 同机 UDP 7551 冲突（仅独立代理形态）

- 插件形态不存在该问题；旧独立代理不分发。
- 状态：已关闭。

## B8 地形/移动/物品栏的最终验收必须用真实客户端（待人工）

- 现状：2026-11-09 00:0x 自动自检已 PASS（1.26.60 → NetherNet → StartGame 3.9s，50 个区块全部解析无错误），但自动客户端只能证明「包能解析」，不能证明「地形画面对、能移动、物品栏不错」。
- 需要人工：用真实 1.26.60 客户端连本服，确认 1) 地形正常不花 2) 能移动 3) 物品栏正常，并截图/描述失败现象。
- 不做静默降级：真实客户端未验收前，S5 不标记完成，也不启动 S6/S7。
- 2026-11-10 14:3x 复查（只读，未改任何配置/二进制）：现网 PID 16020（12:05:21 启动）TCP 19132 / UDP 7551 正常；`/v1/join` 返回 200 `players=0`；`latest.log` 12:05:43 有 `Signed in to signaling service successfully`（B11 未复现）；`hook.log` 12:05:47 见 `规则来源=模块 JERCPLD.PTL.dll`，`translation=ON`；现网 DLL 688128B，SHA256 `C30D5BC6…E04703`。即：环境已干净且就绪，可直接让真人用 26.60/26.62 客户端验收，无需先重启。
- 状态：待人工（唯一卡点）。

## B9 26.45 / 26.30 登录问题（历史记录；2026-11-12 起经独立 RakNet 实例重新支持，见本节末尾）

- **2026-11-09 22:5x 决策**：官方只支持客户端 26.50–26.62 / 服务端 26.50–26.51（协议 2193↔2225）。26.45/26.40/26.30 不再维护；26.45 的根因是传输层不可达（见 B4），与翻译层无关。以下内容保留为历史记录，不再作为验收目标。
- 原实测：1.26.45（2169）与 1.26.30（1001）均被 `outdated_client` 立即拒绝，原因：raw 层改写只认 2225。
- 已修复（2026-11-09 03:1x）：翻译状态改为 `peer → 客户端原始协议号`，raw 层对 1001-2225 内任意非 2193 改写为 2193；s2c 结构改写仍只对 2225 生效，避免把 2225 的字段布局套到 2169。
- 2169 验收：`verify.js --version 1.26.45` PASS，2.2s 登录、4.5s StartGame、417 区块、无踢出；hook.log 有 `raw protocol rewrite: packet=193/1 2169 -> 2193` 与 `translated peer registered: protocol=2169`。2225 回归 PASS。
- 2169 遗留：收到 `camera_presets` 时探针报字符串长度不匹配（339 vs 393），非致命、未踢出；需为 2169 单独裁剪/改写该包。
  - 已解决（2026-11-09 06:0x）：2193 的 CameraPresets 每条记录末尾比 2169/2168/1001 多 `apply_inherited_starting_rotation(bool)` 与 `starting_rotation(option vec2f)`。mod 新增 s2c 包 198 按 peer 协议号分流裁剪（peer<2193），实测 1.26.45 PASS 100 区块、error=null，日志 `camera_presets stripped: 339 -> 327`；1.26.60 回归 PASS。
- 1001（26.30）进展（2026-11-09 10:1x，**结论已被 12:1x 复核推翻，见下**）：ResourcePack 链路已全部就位。s2c ResourcePacksInfo TexturePackInfos countType 改 varint→li16（peer<2168），c2s ResourcePackClientResponse 改 pktId 4→8 + payload u8→varint status + 新增 response_status_name + conditional resourcepackids。但 verify-client (bedrock-protocol 3.61.0) 对 version=1.26.30 的 ResourcePack 流程有 bug——发空的 pktId=4 len=1 ResourcePackClientResponse（无 payload），导致 BDS reason=56 stage=4 踢人。已通过 --spoof-protocol-version 测试确认翻译层逻辑正确。需要真实 1.26.30 客户端验证最终效果，或升级 bedrock-protocol 修复其 1.26.30 ResourcePack 支持。
- 操作注意：BDS 现由 WMI 启动（无控制台窗口）。连续两次自检需间隔约 8s 或换用户名，否则会在 2.3s 断连——这是 NetherNet 同名旧会话清理，不是 mod 缺陷。
- 2026-11-09 12:1x 1001 复核（推翻旧结论）：之前判定「verify-client 自身 bug」是错的。抓包证实：
  - `pktId=4 len=1` 是无载荷的 `client_to_server_handshake`，1001/2168/2193 完全一致，**不是** ResourcePackClientResponse；旧代码把它改名成 8 属于破坏性改写。
  - 真正的 c2s 响应是 `pktId=8`，1001 载荷为 `04 00 00`（u8 status=4 completed + li16 count=0），2193 为 `03 19 "resourcepackstackfinished"`。
  - 改写时机错误：`receivePacketDetour` 在 `origin()` 之前拿到的 buffer 是上一包残留，必须在返回 `HasData` 之后再改写（与 raw 协议号改写同位置）。
  - 补写的 `response_status_name` 不能是空串，空串会被 BDS 以 reason=56 踢掉；必须与 2168+ 客户端实际值一致：`resourcepackstackfinished`。
- 已修复并复测：1.26.30 通过网络设置、登录、资源包三阶段，进入 StartGame（日志 `status u8->varint: 4 -> 27`）。2168(1.26.40) 回归 PASS。
- 1001 StartGame 专用改写（2026-11-09 21:10 **代码完成，待真实客户端验证**）：
  - **gamerules int32 重编码（已实现；方向已于 2026-11-12 修正）**：最初实现为 `reencodeGamerulesInt32VarintToLi32`，该方向有误。实测确认 wire 方向相反：**2193 服务端的 GameRuleI32 是 li32（4 字节小端），1001 客户端期待 varint（LEB128）**，现函数为 `reencodeGamerulesInt32Li32ToVarint`（解码 li32 → 重编码 varint）。已替换之前硬编码 `gamerulesChanged=false` 的跳过逻辑。
  - **is_chat_logging 插入（已实现）**：在 CommandsEnabled 后插入 is_chat_logging(false) 1 byte。
  - **edu_offer zigzag 差异**：小正值（0-63）zigzag 和普通 varint 字节相同，暂不处理。
  - 编译 0 error，DLL 639488B（+123KB），BDS 重启两次均加载成功。
- 状态：26.45/26.40/26.30 已移出支持范围，不再维护；上述历史结论仅作参考。
- **2026-11-12 01:2x S10 验收完成（26.30-26.45 经独立 RakNet 实例重新支持）**：隔离实例 `d:\ylcs_spike\`（transport=raknet、UDP 19252/19253、acceptMinProtocol=1001、PTL 模块停用、JSON 规则 2169/2168→2193）。修复 mod 三处 bug（DLL 690176B，01:22:26）：
  1. gamerules 重编码方向纠正（见上，li32→varint）；
  2. StartGame tail 解析修正：Experiments countType=**li32**（固定 4 字节，非 varint，bedrock-protocol schema 权威）；experimental_gameplay_override 为普通 bool 非 optional；
  3. skipNbtPayload 按 prismarine-nbt littleVarint 语义重写：TAG_List/ByteArray/IntArray/LongArray 计数=**zigzag32 varint**（字节 0x02 解码=1），TAG_Int/Long 值=zigzag varint 变长；TAG_String 长度=普通 varint。
- 实测结果：1.26.45 PASS（257 区块/SPAWN/2 move/3 animate/kick=null/error=null）；1.26.40 PASS（257 区块/SPAWN/2 move/4 animate/error=null）；1.26.30 PASS code=0（StartGame 98 blockProperties 解析成功 + 4 区块 + move + animate + 无踢出）。
- 1.26.30 残余（非阻塞）：spawn=false 源于客户端对 set_entity_data 等 s2c 包 metadata NBT 的 schema 告警（"unexpected tag end"），属 2193→1001 下一层 s2c 差异，列为后续 S5/翻译层参考。
- minecraft-data 1.26.45/1.26.40 protocol.json 已打补丁对齐 player_auth_input（官方文档确认 2168/2169/2193 wire 一致，社区 schema 建模错误；备份 .bak-jercpld-20261112）。
- 现网 nethernet 实例（PID 24632，TCP/UDP 7551）全程未触碰，保持监听。

## B7 验证路径边界（RakNet vs NetherNet）

- 事实：最小自检走 RakNet；BDS 1.26.51 启动时明确警告「NetherNet 是唯一受支持的传输」，RakNet 仅供本机脚本自检。
- 风险：不能把 RakNet 的通过结论当作 NetherNet 全链路已验证。
- 处理：`docs/VERIFY_CHECKLIST.md` 明确区分两类结论；NetherNet 路径由真实客户端复核。
- 状态：持续记录。

## B4 26.45（2169）RakNet-only 客户端与 nethernet 服务端（已决策：不支持）

- 现场：26.45 客户端报 `InitialConnection-13`，报文 `Transport: RakNet:2169`。
- 实测证据：
  - 本机 BDS 1.26.51（`transport=nethernet`, 19132）用模拟 RakNet 客户端（1.26.45/2169）连接 → 12s 超时，未收到 StartGame；说明该实例根本不监听/不服务 RakNet。
  - 官方 26.50 changelog：`NetherNet is now the default transport used by dedicated servers`。
  - 官方 26.60 changelog：`RakNet is deprecated`，非 NetherNet 的警告升级为**错误**；本机 1.26.60 实测启动即报 `NetherNet is the only supported transport type`。
  - Mojira BDS-23110：26.50 设 `transport=raknet` 可启动但显式警告。
- 结论：翻译层无法介入，因为传输层未建立连接。
- 可选路线与成本见下方对比；**未改动现网任何配置**。
- 状态：**已决策（2026-11-09 22:5x）：不支持 26.45 及更老版本**，主线只维护 2193↔2225；2169-2193 规则移至 `examples/PTL/`（非官方维护、默认不安装）。

### 路线对比（2026-11-09 评估）

| 路线 | 做法 | 成本 | 风险 |
| --- | --- | --- | --- |
| A. 独立 RakNet 实例 | 另起一套 BDS 1.26.51 `transport=raknet`（独立端口/世界），老客户端连该端口 | 极低（纯配置） | 不是同服同世界；玩家需两个地址 |
| B. mod 内建 RakNet 入口 | 在插件内实现 RakNet 监听 + 拆包重组，把明文包注入 BDS 连接管线 | 高（RakNet 握手/可靠 UDP/分片 + 连接对象构造与注册） | 连接生命周期、ServerNetworkHandler 路由、无 LL 现成 API |
| C. 独立代理做 RakNet↔NetherNet 桥 | 外部进程终结 RakNet，再用 NetherNet 客户端连 BDS | 很高（需自实现 NetherNet/WebRTC 客户端） | 与「插件等同 Glacie」定位背离；此前已取消 NetherNet 自实现 |

- 证据（B 类可行性）：Glacie v3.10.2 release notes 明确有 `direct RakNet mode` 与 `raknet-port`/`raknet-portv6`，并修复了「RakNet 分片乱序导致 Login 载荷损坏/超时」→ 说明 Glacie 在 mod 进程内自己终结 RakNet 并重组分片。
- LL 侧现成设施：`ll::service::bedrock::getRakPeer()` 可取到 `RakNet::RakPeer`；`NetworkConnection` 的成员布局在 LL 头文件里可见（`mId`/`mPeer`/`mBatchedPeer` 等），但**没有**「新建并注册连接」的公开 API。

### 路线 B spike：Level 1 实测结论（2026-11-09 22:39，失败，已停止）

- 环境：隔离实例 `d:\ylcs_spike`（BDS 1.26.51 + LL 26.51.5 + spike 构建 JERCPLD.dll），端口 19252/19253，世界 SpikeWorld；现网目录与二进制未改动。
- 实验 1（当前现网配置 `transport=nethernet`）：
  - mod 正常加载（`JERCPLD 已加载`、`enabled`），`Server started in (3.9s)` 之后才执行探测。
  - `[SPIKE][L1] getRakPeer() = null（没有可用的 RakPeer 对象）`。
  - **已确认**：nethernet 模式下进程内根本没有 RakPeer 实例，`getRakPeer()` 返回空；无法取得对象，更无法在第二个端口 Startup。
- 实验 2（对照，仅 spike 实例临时改 `transport=raknet`）：
  - `peer=0x... active=true`，`bound addresses=2`：IPv4 19252、IPv6 19253（`portBE=4b34/4b35`）。
  - **已确认**：RakPeer 仅在 `transport=raknet` 时存在，且在 mod 启用前就已完成 Startup，端口已被主监听占用。
- 推论：
  - **推测**（高置信，未实测）：RakNet 的监听 socket 只能在 Startup 时一次性绑定（支持多 socket 但须启动时传入），没有运行时追加监听端口的 API；对 active peer 再次 Startup 预期返回 `RAKNET_ALREADY_STARTED`。遵守 Level 1「只看不写」，本次未调用。
  - **已确认**（前期符号探测）：`RakPeer` 构造符号未导出（`LeviLamina.def` 仅有 `getRakPeer`），mod 内无法自行 new 第二个实例。
  - **推测**：即便能自建 RakPeer，后续仍需构造 `NetworkConnection` 并注册进 `ServerNetworkHandler`（Level 3），无公开 API。
- 结论：路线 B 按原定形态不可行——nethernet 下无对象、raknet 下对象已被占用且不能再 Startup。Level 1 即不通过，按要求停止，未进入 Level 2/3/4。
- 回滚：现网 `d:\ylcs\plugins\JERCPLD\JERCPLD.dll` 保持 21:06:51 原构建（与 spike 构建哈希不同）；现网 config 无 `raknetSpike` 键，默认关闭；spike 实例已停止，server.properties 已恢复 nethernet。
- 备注：探测时发现现网实例（19132/nethernet）已不在运行：19132 无 UDP 监听、无 bedrock 进程、近 3 小时无 Application Error 事件；非本次 spike 操作所致（spike 全程只读写 `d:\ylcs_spike`）。

## B10 NetherNet 非优雅断开留下幽灵会话（2026-11-09 23:40，待缓解）

- 现象：模拟客户端（bedrock-protocol 3.61.0）在 NetherNet 上 `client.close()` 后，服务端 `/v1/join` 长时间仍显示 `players=1`；紧接着的新客户端 `connected=true` 但 `loginSent=false`，90s 超时。重启服务端后同一命令立即 PASS。
- 已确认（实测）：
  - 23:18 模拟会话结束（hook.log 最后一条 23:18:40，无 `onDisconnect`），`/v1/join` 到 23:30 仍 `players=1`；
  - 23:24 隔离实例、23:32 现网各出现一次同样的“连上但登录前卡住”，重启对应实例后恢复正常；
  - 现网 console 无 malformed / violation / kick 记录，包层 hook 无 login 记录 → 卡在传输/会话层，不是翻译层。
- 推测：客户端未发送 DTLS close_notify，BDS/NetherNet 侧会话未被回收，占用连接槽并阻止后续握手。
- 影响：可能解释用户 26.62「Terracotta / InitialConnection-149、无 Transport 行」的失败（先前残留会话导致新连接协商不到传输）。
- 处理：暂不盲目改 BDS/LeviLamina；回归脚本 `run-regression.ps1` 在 `players>0` 时直接 SKIP 并提示重启。后续评估 LeviLamina 的 NetherNet 会话超时/清理配置，以及正式客户端优雅断开时是否复现。

## B11 NetherNet 信令注册丢失（2026-11-10 03:03 进程；06:30 重启恢复）

- 现场：26.62 客户端报 `InitialConnection-149`，报错无 `Transport:` 行。
- 只读取证（06:27–06:29）：
  - `/v1/join` 返回 `players=0`；BDS PID 17100（03:03:03 启动），TCP 19132 / UDP 7551 正常监听。
  - `hook.log` 在 03:03:28 之后没有任何 RequestNetworkSettings / Login / onDisconnect 记录。
  - `latest.log` 的 03:03 启动块没有 `Signed in to signaling service successfully`（最后一次该行是 23:37:41）；该进程没有任何 443 出站连接。
- 处理：06:30 重启一次（当时 players=0）。新进程 PID 16180：06:30:38 `Signed in to signaling service successfully`，443 出站恢复（150.171.110.138:443 ×2），`/v1/join` 200 且 `players=0`，mod 加载、`translation=ON`、规则来源=模块。
- 结论：本次失败是**信令注册未建立**，连接卡在传输层之前；不是 B10 幽灵会话，也不是翻译层。重启可恢复。
- 待办：观察信令是否再次丢失；若复现，检查出站 443 / DNS / 网络稳定性，并评估增加「信令健康检查 + 自动重启」看门狗（需单独验证，不直接改翻译代码）。
- **2026-11-11 06:02 第 4 次复现（已处理）**：PID 21184（00:43:40 启动，00:44:01 Signed in，01:03–01:05 服务过真人 B15 会话）443 出站归零 ≥5 小时；06:03 重启（前台 run_in_background），新 PID 15008，06:03:54 Signed in + 443×2（150.171.110.137/136）、TCP/UDP 7551 正常、/v1/join 200 players=0、translation=ON 规则来源=模块。
- 规律：信令注册在进程运行数小时后丢失（03:02 / 06:02 两次观测均如此），重启 100% 恢复；**每次真人复测前必须检查 443 出站**。看门狗（定期检测 443 出站 + players=0 时自动重启）已具备明确收益，列为待决策项。
- **2026-11-11 11:0x 第 5 次复现（已处理）**：PID 27164（09:06 启动，STATE.json 记录为"恢复后"）443 出站归零；Stop-Process 后重启时连续 6 次 Start-Process（Hidden/Normal/nested PowerShell/重定向 stdout）均在模组加载完后、Server started 之前退出，最终用 `& bedrock_server_mod.exe` + run_in_background（无 Start-Process）稳定启动 PID 20520（12:10 完成 boot，443×2、TCP 19132、UDP 7551、/v1/join 本地+公网均正常）。
- **启动方式新发现**：`Start-Process`（无论 WindowStyle Hidden/Normal/Minimized 或嵌套 PowerShell）均导致 bedrock_server_mod.exe 在模组加载完成后立即退出；直接用 `& d:\ylcs\bedrock_server_mod.exe` + PowerShell run_in_background 才稳定。**所有重启脚本和看门狗必须使用此方式**，废弃 Start-Process 路线。
- 复现次数累计：6 次（03:03 / 06:02 / 06:30 / 09:06 / 11:00 ×2）；每次手动重启耗时 15-30 分钟。
- **2026-11-11 15:0x 第 7 次复现（已处理，并修正判定口径）**：PID 17952（12:22:13 启动）443 出站归零约 2.5 小时。按新看门狗流程 WMI 重启，新 PID 13408（15:05:26），15:06:01 `Signed in to signaling service successfully`，TCP 19132 / UDP 7551 / 公网 `/v1/join` players=0 正常。
- **看门狗已被证实可用**（见 B17）：计划任务 `JERCPLD Watchdog`（SYSTEM/Highest）已修复为 AtStartup + 每 5 分钟无限重复；15:05 两次手动触发完成「strike 1 → strike 2 → 杀进程 → WMI 拉起 → VERIFY OK」，15:18 周期运行日志正常。

## B17 443 出站不能作为信令健康信号（2026-11-11 15:1x 实测修正）

- 现象：B11 一直用「BDS 进程 443 ESTABLISHED == 0」判定信令丢失，但实测该连接在 `Signed in` 后约 3 分钟自然关闭，且不自动重连（PID 13408：15:05:58 有 2 条，15:09 起持续 0，采样 10 次/2.5 分钟均为 0）。
- 反证：PID 21184（00:43:40 启动）在 01:03 仍服务了真人客户端（启动 20 分钟后），当时 443 早已不是长期保持的形态；说明「443=0」与「客户端连不上」不是充分必要条件。
- 风险：若沿用 443 判据，看门狗会每 ~20 分钟误重启一次现网，比 B11 本身更具破坏性。
- 修正（已落地）：`_tools/jercpld-watchdog.ps1` 改为**日志证据判定**——读取 `logs/latest.log` 尾部，以最后一个 `LOADING VANILLA WORLD` 为本次 boot 边界，其后出现 `Signed in to signaling service successfully` 即视为健康；仅当「进程在、启动 ≥3 分钟、players==0、本次 boot 无 Signed in」连续 2 次（约 10 分钟）才重启；另加 15 分钟重启冷却、players>0 永不重启。
- 验证：15:14/15:18/15:19 三次运行均输出 `signedIn=True ... OK signaling registered in this boot`，strikes 归零，未重启；任务 NextRunTime 正常滚动（15:23）。
- 注意：脚本必须保存为 **UTF-8 with BOM**，否则 PowerShell 5 在中文注释处误判语法（本次曾导致任务 LastTaskResult=1）；运行一次约 90 秒（Get-NetTCPConnection 较慢），任务超时设 10 分钟、多实例 IgnoreNew。
- 状态：已修正并验证；后续若真实客户端再报 InitialConnection-149，先看本次 boot 是否有 Signed in，再查客户端缓存/网络，不再以 443 数量下结论。

## B13 隔离实例不得使用生产 serverIP 注册信令（2026-11-11 00:12 实测）

- 现象：客户端「完全扫不到服务器」（比 InitialConnection-149 更早）。
- 证据：
  - 现网 PID 27984 于 00:12:06 已 `Signed in to signaling service successfully`；
  - 00:12:44 另一会话启动隔离实例 `d:\ylcs_spike`（PID 10076），其 `netherNetPatch.serverIP` 也是 `<server-ip>`，以 `<server-ip>:19252` 注册信令并持有 4 条 443 连接；
  - 随后现网进程的 443 出站归零，客户端扫不到；停掉隔离实例后现网仍未恢复，重启现网（PID 20192，00:15:50）才恢复 `Signed in` + 3 条 443。
- 结论：同一公网 IP 同时注册多个 NetherNet 实例会互相抢占/覆盖信令注册；隔离实例使用生产 `serverIP` 会导致现网从发现列表消失。这不是 STUN/NAT 问题，也不是翻译层问题。
- 处置：已备份并把 `d:\ylcs_spike\plugins\LeviLamina\config\Config.json` 的 `serverIP` 置空（`Config.json.bak-jercpld-20261111-0015`）；隔离实例只做本地测试，不再对外注册。
- 规则：只有现网实例允许填公网 `serverIP`；任何隔离/测试实例必须 `serverIP=""`，且不得与现网同时运行 NetherNet 对外注册。
- 备注：STUN 实测 `stun.miwifi.com:3478` 正常、`stun.l.google.com:19302` 超时；当前有 1 个可用 STUN，够用，暂不改配置。
- 2026-11-10 03:0x 复现（第 4 次）：现网进程 3312 上跑 `run-regression.ps1` 得到 PASS（241 区块 / move / animate / kick=null），脚本退出后 `/v1/join` 立即变为 `players=1`，即**每一次模拟客户端成功回归都会留下幽灵会话**，后续连接必然卡在登录前。已按流程 `Stop-Process` → WMI 重启（新进程 17100，03:03:28 加载模块规则，players=0 恢复）。
- 结论：该问题 100% 可复现，属模拟客户端特性（未发 DTLS close_notify），不是翻译层缺陷；但会污染真实客户端验收环境，凡跑完模拟回归必须先重启再让真人测试。

## B12 目标范围冲突：26.30–26.62 vs 已收敛的 26.50–26.62（待人工决策）

- 现场：2026-11-10 用户复述项目唯一目标为「26.30–26.62 任意版本客户端能连上并进入 26.50/26.51 的 BDS」；而 2026-11-09 22:5x 已决策收敛为客户端 26.50–26.62 / 服务端 26.50–26.51（协议 2193↔2225），ROADMAP §8 与 STATE 均按收敛范围执行。
- 事实依据：26.45/26.40/26.30 客户端只带 RakNet 传输（26.45 报 `Transport: RakNet:2169`），而 BDS 26.50+ 的 `transport=nethernet` 不监听 RakNet；插件在包层无法弥补传输层缺失（见 B4 与路线 B spike 结论）。
- 若要真正覆盖 26.30–26.45，只有两条路：A) 另起 `transport=raknet` 的独立实例（不同端口/世界，玩家需两个地址，成本极低）；C) 外部代理做 RakNet↔NetherNet 桥（成本很高，且与进程内插件定位背离）。路线 B（mod 内注入 RakNet 监听）已被 spike 证伪。
- 待决策：是否接受「26.30–26.45 走独立 RakNet 实例/不覆盖」，或维持现有收敛范围。未决策前不投入低版本翻译工作，主线仍按 2193↔2225 推进。
- **决策落地（2026-11-11 00:5x）**：用户任务书再次明确「项目唯一目标：26.30–26.62 任意版本客户端能连上并进入 26.50/26.51 的 BDS 服务端」（连续两次重申，2026-11-10 与 2026-11-11），"不覆盖"不可接受；结合 B4 路线对比，采纳**路线 A：独立 RakNet 实例**覆盖 26.30–26.45，立项为 STATE 中的 S10（独立实例、独立端口 19252、独立世界，不触碰现网 nethernet 实例）。S5（2225 真人验收）仍是主线第一优先。
- 状态：已决策 → S10 已完成（2026-11-12 01:2x，三版本 PASS，详见 B9 末尾记录）。

## B15 真实 2225 客户端卡在资源包下载，never StartGame（2026-11-11 03:0x 取证）

- 现场：真实 26.60/26.62 客户端 01:03:59 连入现网（PID 21184），完成 RequestNetworkSettings + Login（hook.log 有 `raw protocol rewrite: packet=193/1 2225 -> 2193`、`login handler hit`），随后进入资源包下载，**始终没有 StartGame**，01:05:00 断开 `reason=122`（客户端侧断开）。
- 抓包证据（`d:\ylcs\diagnostics\session-1-c2s.log` / `session-2-s2c.log*`，窗口 01:03:59–01:04:41）：
  - c2s id=84 `resource_pack_chunk_request` × 325（按包顺序前进，请求序号连续）；
  - s2c id=83 `resource_pack_chunk_data` × 193，共 **18.15 MB**；其中 14 个小包已下完，大包未完成：`437f9c45_1.0.2` 已发 7.47MB（maxIdx 196）、`98836a6c_5.0.4`（CreateMoveRP 55MB）仅到 chunk 17（1.75MB）；
  - 01:04:41.5 双向同时停止，19 秒后 onDisconnect reason=122。
- 资源包总量：`worlds\Bedrock level\world_resource_packs.json` 启用 19 个包，`resource_packs` 目录合计 **137.31 MB**（CreateMoveRP 54.93MB、vanilla 30.46MB、Create_RE 26.48MB 为主）。实测下载速率约 450 KB/s（18MB/42s），走完全部包约需 5 分钟。
- 排除项：2193↔2225 的 resource_packs_info / stack / data_info / chunk_data / chunk_request / client_response 六个包结构**逐字节相同**（SAME），资源包阶段不需要翻译，PTL 未参与；因此这不是翻译层 bug，而是「首连客户端必须全量下载 137MB 资源包，下载未完成即断开」。
- 影响：B8 真人验收在「资源包下载」这一关就过不去，客户端看不到世界；此前所有 PASS 结论都来自模拟客户端（自动回 completed，不真下载）。
- 待人工决策（三选一，未决策前不动现网内容）：
  - A) 配 BDS 资源包 CDN（`cdn_config.json` baseurl 指向本机 HTTP 静态目录 + 防火墙放行），下载走 HTTP 并行/更快；
  - B) 精简 `world_resource_packs.json`（仅对测试期移除大包，会改变所有玩家客户端材质，需确认）；
  - C) 让真人重测时耐心等待完整下载（约 5 分钟）并观察是否最终进入世界，以确认是超时还是客户端主动退出。
- 另：03:02 复查现网已无 443 出站（B11 信令可能再次丢失），下次真人复测前建议先重启现网（当前 players=0）。
- 状态：**决策已落地（方案A，2026-11-12，实施记录见 B18）**；剩余：真人复测（并入 B8）。

## B16 BDS 重启后 NetherNet ICE candidate 收集返回 0.0.0.0（2026-11-11 09:15，已降级）

- 现场：现网 BDS 重启后，本机 run-regression.ps1（nethernet transport）稳定 FAIL，`connecterror:9`（InactivityTimeout）。SDP ANSWER 抓包显示 `c=IN IP4 0.0.0.0` + `m=application 9`（discard 端口）+ **无任何 ICE candidate**，媒体层无法协商。
- 对比证据：旧 PID 21184（00:43 启动）时 SDP 正常返回公网 IP `<server-ip>` + 端口 19132 + 2 个 ICE candidate（stun + host）；重启后 4 个新进程（PID 15008/28092/26524/9724/27164）均返回 0.0.0.0。
- 影响：本机模拟客户端自动回归链路被阻塞；公网可达性不受影响（`http://<server-ip>:19132/v1/join` 返回 200，UDP relay 正常）。
- 根因方向：推测与 BDS/LeviLamina 重启后的网络接口检测、STUN 应答超时、或 serverIP 配置下的 ICE 候选生成逻辑有关；**非 JERCPLD 翻译层代码问题**（翻译层 0 变更，PTL 模块加载正常）。
- **2026-11-11 18:0x 修正**：PID 13408（15:05 启动）uptime ~3h 时，本机 `run-regression.ps1` 2225→2193 **PASS**（StartGame 4.6s、121 区块、3 move、6 animate、kick=null、error=null）。说明 **ICE 0.0.0.0 只在刚重启后短时间出现，进程稳定运行 2-3 小时后自动恢复正常**，属 LeviLamina NetherNet 内部启动时序问题。进程启动初期（uptime < 30min）不宜跑本机 NetherNet 回归。
- 处置：暂列入待排查，不阻塞主线推进；主线优先做 B15 决策落地与 B8 真人验收。若需在刚重启后跑回归，先等 2 小时或改用 RakNet 路径（`transport=raknet` 隔离实例）。
- 状态：启动初期自恢复，不阻塞主线。

## B14 公网可达性：最终统一 7551（2026-11-11 19:23）

- **决策**：放弃 19132 转发方案，统一使用 **7551**。依据：真实 2225 客户端经 7551 走到 `StartGame` + 区块加载；19132 仅收到外部 UDP 包，进服未验证，且多一层 relay 单点。
- **7551 来源**：**已确认**不由 `server-port` 推导（server-port=19132/7551 两种情况下媒体都绑 7551）；LeviLamina `netherNetPatch` 无端口配置项；**推测**为 LL NetherNet 固定默认端口。
- **最终配置**：
  - `server.properties`：`server-port=7551`（备份 `server.properties.bak-jercpld-20261111-1920-before7551`）。
  - `WSClientConfig.json`：`BDSPort=7551`（备份 `.bak-jercpld-20261111-1920-before7551`）。
  - 防火墙：保留 TCP/UDP 7551 放行；19132 规则保留但无监听。
  - `JERCPLD UDP Relay 19132`：**Disabled**，relay 进程已结束；`udp_relay_19132.js` 保留但不再自启。
- **实测证据**：
  - BDS PID 4944（19:23:29 启动）；netstat：TCP 7551=BDS、UDP 7551=BDS；UDP 19132 无监听。
  - 日志：`已将 <server-ip>:7551 配置为 NetherNet 的映射地址`、`Accepting clients on [::]:7551`、`Signed in`（19:23:50）、`translation=ON`、`规则来源=模块`。
  - 公网 `http://<server-ip>:7551/v1/join` 返回 200、players=0。
  - 新 SDP：`m=application 7551 UDP/DTLS/SCTP`；候选 `<server-ip> 7551 typ srflx raddr 172.16.0.18 rport 7551` + host `<server-ip> 7551`；模拟客户端 PASS（exit 0，kick=null）。
- **18:03 真实客户端路径**：relay 日志在 18:03:32 前后没有任何 `session_open`（最近为 12:22 与 18:04:51），而客户端已到 StartGame/区块 → **已确认未经过 relay**；**推测**为直连 7551（peer-reflexive/NAT 映射），具体 ICE 路径无法从现有日志细分。
- 客户端使用 `<server-ip>:7551`；旧 19132 不再服务。不要重新启用 relay 任务。

## B18 真实 2225 客户端仍卡在资源包阶段：证据收敛（2026-11-12 03:2x 巡检，未改现网）

- 巡检事实：现网 PID 24632 健康（TCP/UDP 7551、`players=0`、本次 boot 21:05:12 `Signed in`、`translation=ON`、规则来源=模块、DLL 688128B `C30D5BC6…`）；公网 `/v1/join` 200 protocol=2193；spike 实例 PID 16476（RakNet 19252/19253，`serverIP=""`）仍在运行但未注册信令，不构成 B13 抢占。
- 11-11 23:38 真实 2225 会话（现网 hook.log）：23:38:05 `raw protocol rewrite 2225 -> 2193` → Login → 23:38:06 `client_cache_status forced to false` → s2c `resource_pack_chunk_data`（session-2-s2c.log 尾部 `id=83 len=102459`）→ 23:38:33 `onDisconnect reason=122`，全程**没有 StartGame**。
- 速率与瓶颈判定：该会话约 4.6 个 chunk/s、≈460KB/s；同一文件 5 秒内累计写出 27MB 抓包，说明**抓包/写盘不是限速点**（曾怀疑 `capture.enabled=true` 拖慢下载，实测排除）。
- 11-11 19:30 另一次 2225 会话形态不同：Login 后 1.5 秒即 `reason=122`（疑似客户端刚收到 ResourcePacksInfo 就放弃）。
- 结论：失败点在「客户端下载 137MB/19 个资源包」阶段，客户端主动断开（122），**不是翻译层、不是抓包、不是信令**（本次 boot 有 Signed in）。与 B15 同一根因，只是补上了速率与断开时刻证据。
- 未执行项（避免无证据改现网）：`capture.enabled` 目前仍为 `true`，但代码里只有在「当前为关闭」时才会热读取配置（`capturePacket` → `maybeReloadCapture`），因此**在线关闭必须重启**；因已排除其为瓶颈，本轮不为此重启。
- 待人工决策（沿用 B15 三选一，未决策前不动现网资源包内容）：
  - A) BDS CDN：`cdn_config.json` 指定 `baseurl` + 本机静态 HTTP 托管 `resource_packs` + 防火墙放行，客户端走 HTTP 下载（不改内容，推荐）；
  - B) 测试期精简 `world_resource_packs.json`（会改变所有玩家材质，需确认）；
  - C) 真人重测并耐心等待约 5 分钟，确认是超时还是主动退出。
- **方案A 已落地（2026-11-12 06:1x-06:3x）**：
  - 格式取证：官方无文档、网络无资料；对 BDS 主程序全文件字符串扫描确认 `CDNConfig::_readConfigFile`/`ServerNetworkHandler::_populateCDNValues`/`Failed to parse CDN config`/`Mismatch detected between CDN config and required resource packs` 存在；键名经隔离实例实验确定——`{"cdn_name": "...", "base_urls": ["http://..."]}` 被接受（spike 带该配置 boot：无 No CDN config 行、无 Failed to parse、无 Mismatch，4.1s 正常启动）。
  - 部署：`d:\ylcs\cdn_config.json`（base_urls=[http://<server-ip>:19132]）；计划任务 `JERCPLD-PackCDN`（SYSTEM，AtStartup+每5分钟幂等守活，脚本 `d:\ylcs\_tools\packcdn.ps1` 纯 ASCII）托管 `python -m http.server 19132 --directory d:\ylcs\resource_packs`；防火墙 TCP 19132 规则既有（B14 保留项）；路由器 TCP 19132→172.16.0.18:19132 映射实测仍生效（第三方代理公网取回 vanilla manifest 200；本机 NAT 回环 760ms）。
  - 现网切换：players=0 确认后重启（旧 PID 24632 → 新 PID 13344，06:25:07），boot 14.7s，**无 No CDN config 行**，06:25:30 Signed in，translation=ON 规则来源=模块，/v1/join 200 players=0。资源包内容零改动、DLL 零改动。
  - spike 状态变更：CDN 格式实验期间停止（两次试启动），实验后保持停止（规避 B13），需要低版本本机回归时再启动。
  - 未验证项（下次真人复测观察）：客户端是否接受 http://（非 https）；python http.server 无 Range 支持（客户端若发 Range 请求失败，二段备选=改用支持 Range 的服务端实现，为该方案第 2 次尝试；若客户端强制 https 则需域名+TLS，另立人工决策）；19132 为公网暴露的静态目录（仅资源包文件，可接受，已观察到公网扫描器噪音，无害）。
- 状态：方案A 已上线，待真人复测（并入 B8）；若复测仍卡下载，按上述备选换路，同一方案不重复尝试超过两次。

## B19 S10 低版本链路只在本机验证，公网不可达（2026-11-12 03:2x 巡检发现）

- 事实：`d:\ylcs_spike`（PID 16476）以 `transport=raknet` 监听 UDP 19252/19253，但 Windows 防火墙仅有 `JERCPLD TCP/UDP 7551` 与 `TCP/UDP 19132` 规则，**没有 19252/19253 放行**；路由器侧也没有对应端口映射记录。
- 影响：S10 的 1.26.30/1.26.40/1.26.45 PASS 只在本机模拟客户端成立；真实低版本客户端从公网**连不到**该实例，与唯一目标「26.30–26.62 任意版本客户端能连上」仍有差距。
- 另：spike 实例世界为 `SpikeWorld`（测试世界），不是现网 `Bedrock level`；即使打通公网，低版本玩家进入的也不是现网世界。
- 待人工决策：是否 (1) 仅为该实例放行 UDP 19252 + 路由器映射 UDP 19252→19252，并把 spike 指向现网存档副本；或 (2) 低版本改用现网另一台/另一端口实例；或 (3) 暂不对外，只保留本机回归能力。
- 本轮未改防火墙、未改路由器、未动 spike 配置。

## B20 资源包临时移除与一键还原（2026-11-12 19:46-19:51）

- 目标：客户端卡在资源包下载，临时让服务端不下发任何附加包；不改翻译层。
- **尝试 1（失败）**：整体移动 `d:\ylcs\resource_packs` → `d:\ylcs\_backup\resource_packs-20261112-1948`。BDS 启动报 `Failed to load Vanilla Resource Pack, is the vanilla pack missing from the loaded world?`，进程不监听端口。
  - 结论：**vanilla 包是启动必需，不能整体移走**；已把备份原样移回（82 个顶层目录、5806 个文件），目录内容零改动。
- **最终做法（生效）**：保留 `resource_packs` 目录，只清空世界包清单：
  - `worlds\Bedrock level\world_resource_packs.json`：19 项 → `[]`（备份 `world_resource_packs.json.bak-20261112-1948`）。
  - `worlds\Bedrock level\world_behavior_packs.json`：14 项 → `[]`（备份 `world_behavior_packs.json.bak-20261112-1948`）。
  - `server.properties` 的 `texturepack-required` 原本就是 `false`，未改。
  - `plugins\JERCPLD` 与 LeviLamina 配置中未发现资源包引用。
- 重启验证（单实例）：
  - BDS PID **2344**（19:50:24 启动），TCP 7551 + UDP 7551；
  - 19:50:41 `Signed in to signaling service successfully`；
  - `translation=ON`、`规则来源=模块 JERCPLD.PTL.dll`；
  - 日志 `Pack Stack - None`；
  - 公网 `http://<server-ip>:7551/v1/join` 返回 200、`players=0`。
- **一键还原（players=0 时）**：
  1. 停止 BDS。
  2. `Copy-Item "D:\ylcs\worlds\Bedrock level\world_resource_packs.json.bak-20261112-1948" "D:\ylcs\worlds\Bedrock level\world_resource_packs.json" -Force`
  3. `Copy-Item "D:\ylcs\worlds\Bedrock level\world_behavior_packs.json.bak-20261112-1948" "D:\ylcs\worlds\Bedrock level\world_behavior_packs.json" -Force`
  4. 启动 BDS；日志应重新出现 `Pack Stack - [xx] ...`（14 个行为包与 19 个资源包）。
  - `resource_packs` 目录无需移动（已原样恢复）；**不要再次整体移走**。
- **2225 vs 2193 包结构判断（证据级）**：B15 的真实 2225 会话抓包已覆盖六个包（resource_packs_info / stack / data_info / chunk_data / chunk_request / client_response），结论是**逐字节相同（SAME）**，资源包阶段不需要翻译；本次卡点是 137MB/19 包下载约 450KB/s、需要约 5 分钟，客户端 `reason=122` 主动断开。本轮未新增 6/82/84 的抓包，PTL 规则也不含这些包；若清空包清单后仍卡，则问题不在“附加包内容/结构”这一层，应查客户端缓存、CDN 或 vanilla 包。
- 备注：PackCDN（python http.server 19132，PID 14472）仍在运行但已无包可下发；保留以便还原后立即恢复 CDN 下载。
- 2026-11-12 06:2x 状态更新：spike 实例已停止（CDN 格式实验占用，实验结束）；保持停止以规避 B13，需要低版本本机回归时再启动。



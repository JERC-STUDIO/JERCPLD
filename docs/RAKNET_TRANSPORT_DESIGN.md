# RakNet 传输侧设计（26.30–26.60，Glacie 路线）

- 日期：2026-11-12
- 状态：阶段一设计（未改现网、未改翻译层）
- 目标：插件自带 RakNet 服务端，按包内协议号识别客户端版本，翻译后进入现网 BDS；26.30–26.45 走 RakNet，26.50+ 继续走 NetherNet。

## 0. 本轮已实测的事实

| 项 | 实测结果 |
|---|---|
| 现网 BDS | `transport=nethernet`，TCP 7551 + UDP 7551（server-port=7551）；PID 见 HANDOFF/STATE |
| TCP 19132 | PackCDN（python http.server，PID 14472），不是游戏端口 |
| UDP 19132 | **空闲；本轮绑定成功**，STUN 返回 `<server-ip>:19132`（端口保留型 NAT） |
| 防火墙 | `JERCPLD TCP/UDP 19132` 规则已存在；UDP 19132 公网可达 |
| `raknet-native` | Windows 可加载；导出 `Server`/`Client`，`new Server('0.0.0.0', port, {minecraft:true})` 可创建 |
| 版本表（minecraft-data + 官方证据） | 1.26.30→1001，1.26.40/43/44→2168，1.26.45→2169，1.26.50/51→2193，1.26.60/61/62→2225 |
| 版本表缺口 | 1.26.43/44 数据源未收录，但官方证据已确认=2168；1.26.50 官方证据=2193、1.26.61/62=2225 |

### 版本号确认表（P3）

| 客户端版本 | 协议号 | 状态 | 依据 |
|---|---|---|---|
| 1.26.30 | 1001 | **已确认** | minecraft-data；spike hook.log 见 1001→2193 |
| 1.26.40 | 2168 | **已确认** | minecraft-data；spike hook.log 见 2168→2193 |
| 1.26.43 | 2168 | **已确认** | 官方证据：1.26.40/43/44=2168；minecraft-data 未收录该版本条目 |
| 1.26.44 | 2168 | **已确认** | 官方证据：1.26.40/43/44=2168；minecraft-data 未收录该版本条目 |
| 1.26.45 | 2169 | **已确认** | minecraft-data + 本轮 RakNet 探针实测（`fe06c10100000879`） |
| 1.26.50 | 2193 | **已确认** | 官方证据：1.26.50/51=2193；minecraft-data 未收录该版本条目 |
| 1.26.51 | 2193 | **已确认** | minecraft-data / 现网 |
| 1.26.60 | 2225 | **已确认** | minecraft-data / 现网 |
| 1.26.61 | 2225 | **已确认** | 官方证据：1.26.60/61/62=2225 |
| 1.26.62 | 2225 | **已确认** | 官方证据：1.26.60/61/62=2225（现网 26.62 登录时被改写为 2225） |

> 注意：用户描述中的“TCP 19132 信令”已不成立。B14 之后 NetherNet 信令与媒体都在 7551；TCP 19132 是 PackCDN。RakNet 只用 **UDP 19132**，与 TCP 19132、UDP 7551 都不冲突。

## 1. 传输并存方案

- RakNet 监听：**0.0.0.0:19132 UDP**，独立 socket、独立线程，不碰 NetherNet。
- 与 NetherNet 不冲突的依据：
  - NetherNet：TCP 7551（信令/HTTP）+ UDP 7551（媒体）；
  - RakNet：UDP 19132；
  - PackCDN：TCP 19132（不同协议，互不占用）。
- 公网：UDP 19132 已实测可绑定并拿到 `<server-ip>:19132` 的 srflx 映射；NAT 端口保留，无需路由器改映射。
- 防火墙：保留 `JERCPLD UDP 19132`；若换端口需同步加规则。
- 信令：RakNet 服务端**不注册 NetherNet 信令**；现网仍是唯一注册实例，满足“同一公网 IP 只允许一个实例注册信令”。
- 客户端侧：26.30–26.45 的“添加服务器”默认 UDP 19132，正好命中 RakNet；26.50+ 继续走 NetherNet 7551（需要客户端知道 7551，或由服务器列表/直连地址指定）。

## 2. RakNet 服务端实现范围

| 能力 | 是否必须 | 阶段 2 是否需要 | 说明 |
|---|---|---|---|
| 离线握手（OpenConnectionRequest1/2、Reply1/2、ConnectionRequest、RequestAccepted、NewIncomingConnection） | 必须 | **是** | `raknet-native` 已实现 |
| MTU 探测与分片阈值 | 必须 | 是（库内） | 库内处理 |
| ACK/NACK、重传、超时 | 必须 | 是（库内） | 库内处理 |
| 可靠/有序/ sequenced、分片重组 | 必须 | 是（库内） | `raknet-native` 处理 |
| 加密（ServerToClientHandshake、ECDH P-384、AES-CTR、ClientToServerHandshake） | 必须（进 Login 后） | **否** | 阶段 2 只到 RequestNetworkSettings，BDS 的该包在加密前发送 |
| 拥塞控制/滑动窗口 | 生产必须 | 库内 | 库内基础实现；高负载需调参 |
| 多连接/连接生命周期 | 必须 | 是 | 库内 + 插件管理 |

**选型建议**：
- 阶段 2 / 验证：`raknet-native`（C++ RakNet 的 Node 绑定，已有 Windows 预编译，`{minecraft:true}` 支持 Bedrock，已验证可加载）。
- 产品化（插件内）：优先把同一套 C++ RakNet 服务端（RakNet/SLikeNet，需确认许可证为 BSD/MIT 类）编译进 mod，避免 Node 子进程；`jsp-raknet` 为纯 JS 且官方标注不稳定，不作为生产选型；`raknet-node`（Rust）可作为备选。
- 许可证：RakNet 原始实现为 BSD 风格，`raknet-native` 为 MIT（以其仓库 LICENSE 为准）；引入前做一次许可证核对，禁止 GPL。

## 3. 外部 RakNet 会话接入 BDS（最关键）

BDS 需要：
- 合法的 `NetworkIdentifier`（地址 + GUID/端口）；
- 一个 `NetworkConnection`（内含 `NetworkPeer` 链：Encrypted/Compressed/Batched）；
- 加密上下文与 `ServerNetworkHandler` 的连接注册、tick 生命周期。

可选路线：

### 路线 A（推荐目标）：自定义 `NetworkPeer` + 构造 `NetworkConnection` + 注册到 `ServerNetworkHandler`
- 实现一个 `RakNetNetworkPeer : NetworkPeer`，`sendPacket` 走 RakNet，`receivePacket` 从 RakNet 队列取包；
- 用 `NetworkConnection` 构造函数（头文件已给出签名）创建连接，`NetworkIdentifier` 用客户端 RakNet 地址；
- Hook/调用 `ServerNetworkHandler` 的连接注册点（需符号探测：`_onNetworkConnection` / 连接 map 插入 / `NetworkSystem::_update`），让后续 `$handle(RequestNetworkSettings)`、`$handle(Login)` 与玩家生命周期走原逻辑；
- 优点：BDS 看到的是“正常连接”，翻译层、玩家管理、区块下发全部复用；
- 风险：连接注册点、加密上下文、tick 驱动、断线清理都不在公开头文件里；需要符号探测与最小 hook；构造/注册失败会直接崩服，必须在隔离实例验证。
- 可行性：**中低**（符号存在则可行；不存在则卡住）。

### 路线 B：直接调用 `ServerNetworkHandler::$handle`，不注册连接
- 用合成的 `NetworkIdentifier` 直接调用 `$handle(RequestNetworkSettings/Login)`，响应通过 RakNet 发回；
- 优点：不改连接表，起步快；
- 风险：BDS 不会把该连接纳入 tick/玩家列表，后续 StartGame/区块/移动大概率断链；只适合阶段 2 的“收包打印”，不适合进服。
- 可行性：**高（仅收包）/低（完整进服）**。

### 路线 C：本机 RakNet→NetherNet 桥（独立进程/插件内子模块）
- RakNet 服务端终止老客户端，翻译后作为“本地 NetherNet 客户端”连到现网 BDS；
- 优点：完全不碰 BDS 内部连接结构，BDS 只看到正常 NetherNet 连接；
- 缺点：每个外部连接需要一个本地 NetherNet 客户端（WebRTC/DTLS/SCTP），CPU/内存开销大；需要实现/复用 NetherNet 客户端栈；与“插件自己提供传输侧”的目标有偏差。
- 可行性：**中**（工程量大，但风险可控）。

### 路线 D：Hook NetherNet 连接创建，替换 peer
- 在 NetherNet 创建连接时把 peer 替换为 RakNet peer；
- 风险最高，且 NetherNet 内部符号更少；不推荐。

**推荐**：阶段 2 先做独立 RakNet 监听（不注入）；阶段 3 先做符号探测，若路线 A 的连接注册点可用则走 A；否则退回 C。不要在未探测符号前承诺“插件内直连 BDS”。

## 4. 版本识别与路由

- 协议号来源：
  - `RequestNetworkSettings`（包 ID 193）：4 字节大端协议号，**加密前**发送，阶段 2 即可拿到；
  - `Login`（包 ID 1）：`mClientNetworkVersion`，作为二次校验。
- 版本表（已确认/待实测）：
  - 1001 = 1.26.30
  - 2168 = 1.26.40 / 1.26.43 / 1.26.44
  - 2169 = 1.26.45（26.44 的 hotfix）
  - 2193 = 1.26.50 / 1.26.51
  - 2225 = 1.26.60 / 1.26.61 / 1.26.62
  - 1.26.43 / 1.26.44：官方证据确认=2168（1.26.40/43/44 同协议号）
- 路由：
  - RakNet 侧识别 `clientProtocol`；
  - 目标服务端协议固定 2193（现网）或 2225（反向实例）；
  - 用现有 PTL 引擎的 `ptlFindPath(from,to)` 做多档递进；每档必须有 `PTL/<a>-<b>.json`，缺档即报错，禁止半程硬翻；
  - 26.30–26.45 需要新增 1001→2168→2169 的相邻档规则（当前只有 2169-2193 与 2193-2225）；每档需 A→B→A 往返自检通过才进链。
  - 版本识别只决定“走哪条链”，不改变现有 2193↔2225 的 PTL 行为。

## 5. 性能与线程模型

- 单 UDP socket + 单 IO 线程（IOCP/epoll）；RakNet 的 ACK/重传/分片在 IO 线程内完成；
- 收包：预分配环形缓冲，解析在 buffer 内原地进行，避免 `std::string` 每包拷贝；
- 连接状态：`guid -> ConnectionState` 的 `shared_mutex` map；热路径只在连接建立/断开时加锁；
- IO → 游戏线程：SPSC 无锁队列（每连接一个有界队列），批量 drain；避免每包 `new/delete`；
- 发送：小包合并、MTU 1400、仅在超过 MTU 时分片；ACK 批量；
- 内存：每连接固定缓冲上限（如 256KB 收 + 256KB 发），超限断开，防止慢客户端拖垮；
- Node 验证器只用于协议正确性，不作为性能依据；产品化必须 C++ 内嵌。

## 6. 工作量与风险排序

| 项 | 工作量 | 风险 | 备注 |
|---|---|---|---|
| 阶段 2：UDP 19132 RakNet 监听 + 26.45 握手 + 收 RequestNetworkSettings | 小（1 天内） | 低 | `raknet-native` 已验证可加载；不碰现网 |
| 版本表补齐（26.43/44/50/61/62） | 小 | 低 | 需真实客户端/抓包确认 |
| 26.30–26.45 的 PTL 相邻档规则 | 中（每档数天） | 中 | 需逐包 diff + 真机回归 |
| 路线 A：NetworkConnection 注入 | 大（2–4 周） | **高** | 连接注册点/加密上下文/生命周期未知；需符号探测 |
| 路线 C：RakNet→NetherNet 桥 | 大（3–5 周） | 中高 | 需要 NetherNet 客户端栈；但不动 BDS 内部 |
| 加密握手（Login 之后） | 中 | 中 | ECDH P-384 + AES-CTR + JWT；可复用 bedrock-protocol 逻辑做参考 |
| 性能/稳定性 | 中 | 中 | 单线程 IO + 无锁队列；需压测 |

**阶段一结论**：传输并存与版本识别可行；阶段 2 可行。**最大卡点是“外部 RakNet 会话接入 BDS 连接层”**：路线 A 依赖未导出的连接注册符号，必须先做符号探测；若找不到，不能承诺插件内直连，需退回路线 C（桥）或只做“独立 RakNet 入口 + 转发到另一实例”。

## 7. 阶段 2 验收

1. 隔离目录启动 RakNet 监听 `0.0.0.0:19132`（不碰现网 7551，不注册信令）；
2. 26.45 客户端（或 `bedrock-protocol` 模拟 26.45，`transport=raknet`）完成 RakNet 握手；
3. 收到 `RequestNetworkSettings`，打印协议号 2169 与版本 1.26.45；
4. 证明：UDP 19132 可用、老客户端能到“可识别版本”这一步；不做翻译、不下发内容。

### 阶段 2 实测结果（本轮，已通过）

- 隔离探针：`d:\ylcs_spike\raknet_probe\probe.js`，监听 `0.0.0.0:19132 UDP`，不注册 NetherNet 信令。
- 客户端：`bedrock-protocol` 模拟 1.26.45，`transport=raknet`；测试依赖补丁：`raknet-native` Client 使用 `'minecraft'` 模式（`node_modules/bedrock-protocol/src/rak.js`，备份 `.bak-jercpld-20261112-1935`）。原来的 `{protocolVersion:11}` 会收到 `ID_INCOMPATIBLE_PROTOCOL_VERSION(25)`。
- 实测日志：
  ```
  OPEN_CONNECTION 127.0.0.1/54707 guid=15987454490045037933
  ENCAP first=fe06c10100000879 id=193
  REQUEST_NETWORK_SETTINGS protocol=2169
  RESULT GOT_REQUEST_NETWORK_SETTINGS
  ```
- 解码：`fe` 批包头，`06` 批长度，`c1 01` 为 varint 包 ID 193（RequestNetworkSettings），后 4 字节 `00 00 08 79` = **2169（1.26.45）**。
- 现网未受影响：BDS PID 2344，TCP/UDP 7551，公网 `/v1/join` 200 players=0；探针退出后 UDP 19132 释放，无信令注册。
- 边界（必须如实标注）：
  - 这是**独立 RakNet 探针**，不是插件内实现；
  - 客户端是 `bedrock-protocol` 模拟 1.26.45，不是真实 26.45 客户端；
  - 阶段 2 只证明“UDP 19132 + RakNet 握手 + 协议号识别”可行，**未验证接入 BDS 连接层**。
- 结论：阶段 2 里程碑通过；阶段 3 的关键仍是第 3 节的连接注入（路线 A 的符号探测）。

## 8. 待决策/待验证

- 路线 A 的连接注册符号是否存在（`ServerNetworkHandler` 连接 map / `NetworkSystem`）——需 symprobe；
- 1.26.43/44/50/61/62 的精确协议号；
- RakNet 库许可证与 MSVC 编译集成；
- 26.30–26.45 的 PTL 规则工作量与测试矩阵；
- 客户端 26.30–26.45 是否都默认 UDP 19132（需逐版本确认）。

# JERCPLD 联调启动清单

> 目标：让用户在 30 分钟内完成环境准备、启动代理、确认链路阶段，并能导出诊断定位问题。
>
> **当前限制（必须先读）**：代理的加密握手密码学层已完成但**尚未接线**。真实 26.60 客户端登录会停在
> `ServerToClientHandshake` 阶段；当前构建能验证到 NetherNet 连接 + NetworkSettings 往返，
> 不能完成进世界。下面清单会明确标出「会停在哪一步」。

## 0. 准备物（5 分钟）

- Windows x64 机器，`JERCPLD.exe`（dist/win-x64）。
- Minecraft Bedrock **26.60** 客户端（协议 2225），已登录 Xbox 账号。
- BDS **1.26.50/1.26.51**（协议 2193），本机已有一份 `bedrock_server_mod.exe`。
- 客户端与 BDS 在同一局域网（或客户端能访问代理机器）。

## 1. 处理 UDP 7551 端口冲突（5 分钟）

BDS 与本代理的局域网发现都使用 **UDP 7551**，同机只能有一个进程绑定。

检查：

```powershell
Get-NetUDPEndpoint -LocalPort 7551 | Select-Object LocalAddress,LocalPort,OwningProcess
```

两种方案：

- **方案 A（推荐，真实客户端）**：代理放到另一台机器
  - BDS 机器保持 7551；
  - 代理机器 UI 监听 `0.0.0.0:7551`；
  - 客户端与代理同一网段，能在「好友/局域网」看到代理。
- **方案 B（同机，仅自测）**：使用 HTTP 信令，不使用真实客户端
  - UI「NetherNet 接入方式」选 `HTTP 信令（自测）`，监听 `0.0.0.0:19132`（TCP）；
  - 用 ArkMirage Endpoint 客户端拨号 `http://127.0.0.1:19132`；
  - Minecraft 客户端**不支持** HTTP 信令，因此此方案不能用于真实客户端联调。

> 不建议为了同机联调去停 BDS：会中断现有玩家。优先用方案 A。

## 2. 启动 BDS（5 分钟）

- 确认 BDS 已启动并监听 19132（RakNet/NetherNet）与 7551（发现）。
- `server.properties` 关键项：
  - `server-port=19132`
  - `online-mode=true`（真实账号联调保持 true）
  - `compression-algorithm=zlib`（当前只实现 zlib；不要用 snappy）
  - `enable-lan-visibility=true`（代理需要通过 7551 发现它）
- 本机 BDS 已在运行（PID 10940），不要重复启动。

## 3. 界面配置（5 分钟）

| 项 | 值 |
| --- | --- |
| 传输模式 | NetherNet（ArkMirage，实验） |
| NetherNet 接入方式 | 局域网发现（UDP 7551） |
| 客户端版本 | 1.26.60（2225） |
| 服务端版本 | 1.26.50 或 1.26.51（2193） |
| 监听地址 | 0.0.0.0:7551 |
| 上游 BDS | 局域网发现（真实 BDS） |
| 记录包诊断 | 勾选 |
| 仅直通 | 不勾选 |

点击「启动代理」。

## 4. 判断链路阶段（10 分钟）

按日志逐项确认：

1. `NetherNet 监听已启动`：代理监听成功。
2. `NetherNet 已启用局域网发现信令`：UDP 7551 绑定成功。
3. 客户端在「好友/局域网」看到服务器并点击加入。
4. `NetherNet 客户端已连接` + `已发现 BDS`：客户端到代理、代理到 BDS 的信令都通。
5. `NetherNet 上游已连接`：WebRTC 建立。
6. 客户端发出 RequestNetworkSettings，代理日志出现上游回包；统计面板：
   - 会话数 = 1
   - 包总数持续增加
   - 已翻译 > 0
   - 批次解析失败 = 0
7. **预期停点**：客户端收到 ServerToClientHandshake 后无法继续（加密接线未完成）。
   这属于已知阻塞，不是配置错误。

## 5. 诊断导出（5 分钟）

- 点击「导出诊断」，或停止代理时自动导出。
- 文件位置：`JERCPLD.exe 同目录/diagnostics/session-YYYYMMDD-HHmmss.log`
- 格式（TSV，可直接用 Excel/文本打开）：

```
time  session  direction  packet_id  name  in_bytes  out_bytes  status  detail
```

- 反馈问题时请提供：该文件 + 代理日志面板截图 + 客户端卡住的界面截图。

## 6. HTTP 信令配置与限制

| 项 | 配置 |
| --- | --- |
| 代理监听模式 | HTTP 信令（自测） |
| 监听地址 | 0.0.0.0:19132（TCP） |
| 客户端拨号 | `http://<代理IP>:19132`（ArkMirage Endpoint Client） |
| 上游信令 | 真实 BDS 仍走局域网发现；BDS 不提供 HTTP 信令 |

限制：

- Minecraft 客户端不会用 HTTP 加入，因此 HTTP 仅用于自测客户端/自动化测试。
- HTTP 监听占用 TCP 19132，BDS 用 UDP 19132，两者可共存。
- 若同机 7551 被 BDS 占用，代理的 HTTP 监听可以启动，但上游发现仍需要能访问 BDS 的 7551。

## 7. 当前状态（2026-11-08）

### 已验证（离线/自检）

- NBT little-endian / network 编解码。
- 官方 bedrock-samples 生成方块表：2193 = 22189 状态，2225 = 24058 状态，0 哈希冲突。
- 2225→2193 方块映射：exact 22156、fallback 6、filtered 1896、default 0、unmapped 0。
- Login / StartGame / NetworkSettings / LevelChunk / SubChunk / SetActorData 往返与合成测试。
- NetherNet 回环连接、真实 BDS 1.26.51 局域网发现 + NetworkSettings 跨版本返回。
- 压缩协商（zlib）与重压缩。
- 加密握手密码学层（ECDH P-384 / AES-256-CFB8 / JWT / 桥接）离线测试。

### 未验证（必须真实客户端）

- 方块状态哈希与真实 StartGame 表逐项比对。
- 区块地形实际显示（LevelChunk 内部布局、SubChunk 调色板）。
- 物品栏 / InventorySlot 行为。
- 实体元数据槽位表（verified=false）。
- 登录 + 加密握手接线。
- Snappy、真实 BDS 的完整信令路径。

### 已知阻塞

- 加密握手未接线：需要有效 Xbox 身份或离线模式 BDS。
- 本机无 Minecraft 客户端与可用账号。
- 同机 UDP 7551 冲突。
- ArkMirage 为 GPL-3.0，对外发布前需替换。

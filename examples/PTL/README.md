# 低版本 PTL 规则示例（非官方维护）

本目录存放主线不再维护的版本对规则，仅作格式示例，社区可自行补齐。

- `2169-2193.json`：1.26.45（2169）↔ 1.26.51（2193）。

## 为什么不在主线

1. 26.45（2169）客户端只支持 RakNet 传输；本项目服务端固定 `transport=nethernet`，传输层就已不可达。
2. 实测：`transport=nethernet` 下 `ll::service::bedrock::getRakPeer()` 返回 null，进程内没有 RakPeer；
   `transport=raknet` 下 RakPeer 已 Startup 且端口被主监听占用，`RakPeer` 构造符号未导出，无法再起第二个监听。
3. 因此即使规则层面能翻译，26.45 也无法建立连接。相关证据见 `docs/BLOCKERS.md` B4。

## 使用方式（不保证可用）

把本目录的 JSON 复制到 `plugins/JERCPLD/PTL/`，并把 `config.json` 的
`acceptMinProtocol` 调到对应协议号（例如 2169）。主线默认是 `2193`。

复制前请先确认服务端传输与客户端匹配；主线不为该组合提供支持。

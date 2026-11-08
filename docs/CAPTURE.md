# 包级抓包使用说明

NetherNet 媒体走 DTLS 加密，外部 pcap 只能看到密文。JERCPLD 的抓包在 mod 内部、包已解密之后记录，因此能直接看到 26.60/26.52 客户端的明文 Bedrock 包。

## 配置

`plugins/JERCPLD/config/config.json`：

```json
{
  "translationEnabled": true,
  "serverVersion": "1.26.51",
  "serverProtocol": 2193,
  "acceptMinProtocol": 1001,
  "acceptMaxProtocol": 2225,
  "capture": {
    "enabled": false,
    "outputDir": "diagnostics",
    "maxFileBytes": 8388608,
    "maxFiles": 4,
    "packetWhitelist": []
  }
}
```

说明：

| 字段 | 说明 |
| --- | --- |
| `enabled` | 抓包开关，默认 `false`。修改后需重启 BDS 生效。 |
| `outputDir` | 输出目录，相对 BDS 工作目录；默认 `d:\ylcs\diagnostics`。 |
| `maxFileBytes` | 单文件大小上限，超过后轮转。 |
| `maxFiles` | 保留的轮转文件数，超出后旧文件被覆盖。 |
| `packetWhitelist` | 只抓这些包 ID；空数组表示全部。例：`[12, 58]` 只抓 AddPlayer 与 LevelChunk。 |

## 开启与关闭

1. 修改 `capture.enabled` 为 `true`（可同时配置白名单）。
2. 重启 BDS。
3. 启动日志出现：

```
[JERCPLD] capture=ON dir=diagnostics whitelist=2
  Packet capture  : ON -> diagnostics
```

关闭时 banner 显示 `Packet capture : OFF`，热路径只多一次原子读，不做任何格式化或文件操作。

## 输出位置与文件

默认目录：`d:\ylcs\diagnostics\`

```
session-1-s2c.log
session-1-s2c.log.1
session-2-c2s.log
...
```

- `c2s`：客户端 → 服务端（记录的是协议号改写后的字节）
- `s2c`：服务端 → 客户端（记录的是出站字段补齐后的字节）
- 会话号按连接对象分配；同一逻辑会话的 c2s 与 s2c 可能是不同编号。
- 文件超过 `maxFileBytes` 后轮转为 `.1`、`.2`……最多保留 `maxFiles` 份。

## 记录格式

```
00:30:20.138 s2c id=58 name=FullChunkData len=9297
HEX 3ab101d007000001120000c348...
```

字段：毫秒时间戳、方向、包 ID、包名（未收录的显示 `unknown`）、字节长度、完整十六进制内容。

## 推荐排查用法

只抓登录与区块关键包：

```json
"packetWhitelist": [1, 11, 12, 13, 58, 193]
```

只抓实体/玩家相关：

```json
"packetWhitelist": [12, 13, 39, 63, 93]
```

只抓某方向：目前不支持按方向过滤，可在输出目录按文件名筛选 `-c2s` / `-s2c`。

## 注意

- 抓包为同步写盘，开启后会有额外 I/O 开销，仅用于诊断。
- `LevelChunk` 单包可达数百 KB，白名单为空时请确保 `maxFileBytes`/磁盘空间足够。
- 服务端断开原因同时写入 `plugins/JERCPLD/hook.log` 的 `onDisconnect` 行。

# jercpld-capture

**一句话定位**：读取 bedrocktool 的 `.pcap2` 抓包，用 `1001` / `2168` 两套 schema 对同一条 payload 做交叉解析，判定 JERCPLD 协议里三个尚待确认的字段语义——`air` 长度、`legacy_type` 是否存在、`teleport` 的 switch/option 分支。

---

## 1. 为什么需要它

JERCPLD 分支 `feat/protocol-1001-s2c` 的 `docs/PROTOCOL_1001.md` 第八节留下了三项「待真机抓包确认」。这三项的共同点是：**静态阅读协议文档与反编译结果都无法自证**——两种写法在字节流上都能解析通过、都不会越界，只是把同一个字节区间解释成不同的字段边界。

也就是说，文档能列出「可能是 A，也可能是 B」，但判据落在**包尾对齐**上，而包尾对齐只有真实 payload 才能提供。本工具做的事情就是：把真机抓包喂进来，对每个候选包同时跑两套 schema，用「解析后是否恰好吃完」（`tail == 0`）作为唯一裁判，把文档里的问号变成 `CONFIRMED` / `REFUTED` / `INCONCLUSIVE`。

它不是抓包工具，也不是协议实现，只做一件事：**给待确认项投票，并说明票是怎么投出来的**。

---

## 2. `.pcap2` 文件格式（字节级）

`.pcap2` 是 bedrocktool 的私有抓包容器。文件分两段：一段定长文件头 + 一段资源包 zip（整体跳过），之后是首尾带 magic 的记录循环。

### 2.1 文件头

```text
偏移(相对文件起点)   类型         字段       说明
0x00                char[4]      magic      ASCII "BTCP"
0x04                uint32LE     version    容器版本,当前为 5
0x08                uint64LE     zipSize    资源包 zip 的字节数
0x10                byte[]       zipData    zipSize 字节;直接 seek/丢弃,不解析也不解压
0x10 + zipSize      ----         ----       记录区起点
```

全部整数均为**小端**。`version != 5` 时不中断解析，而是把告警与已解析记录一起返回（容错模式），调用方可以忽略该 error 继续用记录。

### 2.2 记录循环（重复到文件结束）

```text
+0x00              uint32LE      magicStart   必须 == 0xAAAAAAAA
+0x04              uint32LE      compSize     本条压缩体字节数
+0x08              uint8         toServer     1 = c2s(客户端→服务端),0 = s2c(服务端→客户端)
+0x09              uint64LE      timestamp    Unix 毫秒
+0x11              byte[compSize] body        s2 压缩数据
+0x11 + compSize   uint32LE      magicEnd     必须 == 0xBBBBBBBB
```

`body` 经 **s2**（`github.com/klauspost/compress/s2`）解压后得到 Bedrock 包体：

```text
[0 ..)      uvarint     packetID   LEB128 无符号变长整数(包 ID)
[其后 ..]   byte[]      payload    剩余全部字节,原样交给 schema 解析
```

注意 `packetID` 在解压后的包里，**不在**记录头里；记录头只有方向、时间戳和压缩长度。

### 2.3 一个带标注的示例

```text
42 54 43 50              "BTCP"
00 00 00 05              version = 5
D2 04 00 00 00 00 00 00  zipSize = 1234
... 1234 字节 zip ...    (跳过)

AA AA AA AA              magicStart 0xAAAAAAAA
1C 00 00 00              compSize = 28
00                       toServer = 0 → s2c
00 DE A3 07 93 01 00 00  timestamp = 1731000000000 (UnixMilli)
... 28 字节 s2 块 ...    解压 → uvarint packetID + payload
BB BB BB BB              magicEnd 0xBBBBBBBB
```

### 2.4 读取时的硬性约束

| 约束 | 行为 |
| --- | --- |
| 头部 magic ≠ `"BTCP"` | 立即失败，报错带文件偏移 |
| `version ≠ 5` | 容错继续，解析完把告警作为 error 一并返回 |
| `compSize > 64 MiB` | 立即失败（防损坏文件触发巨额分配） |
| 记录 `magicStart/End` 不匹配 | 立即失败，报错带记录序号与偏移 |
| 记录边界上的干净 EOF | 视为正常结束 |
| 记录被截断（半条 magic / 短 body） | 立即失败，报错带偏移与已拷贝字节数 |
| s2 解码失败 | 立即失败，报错带记录序号与偏移 |

---

## 3. 用法

### 3.1 生成抓包

```bash
bedrocktool capture --address <server:port> --output session.pcap2
```

bedrocktool 在握手阶段完成解密，落盘的 `body` 已是**明文** Bedrock 包（外层仅 s2 压缩）。所以本工具不涉及任何加密逻辑。

### 3.2 交叉解析

```bash
jercpld-capture analyze session.pcap2
```

对每条记录分别用 `1001` 与 `2168` 两套 schema 解析，逐包输出：

```text
[s2c] #42 mob_equipment id=31 len=12
     1001: item=1B air=1 tail=7 OK      ← 短路,后面 7 字节没人读
     2168: item=8B air=1 tail=0 OK      ← 吃满,恰好到包尾
     → 2168 对齐包尾,air=8B

[s2c] #43 mob_equipment id=31 len=12
     1001: item=1B air=1 tail=7 OK
     2168: item=8B air=1 tail=0 OK
     → 同向计票

[s2c] #44 mob_equipment id=31 len=12
     1001: item=1B air=1 tail=7 OK
     2168: item=8B air=1 tail=0 OK
     → 同向计票
```

### 3.3 出结论

```bash
jercpld-capture verdict session.pcap2
```

汇总全部样本，输出三项判定的最终结论与支撑证据：

```text
== 判定 1/3:air-length ==
问题:2168 ItemV4 的 air(network_id==0) 是 1 字节还是 8 字节?
结论:8 字节(air 也写满尾部字段)  [置信度 high,样本 4]
证据:
  - 对齐包尾的样本数:1001 spec=0,2168 spec=4,总计=4
  - [s2c] #42 mob_equipment id=31 len=12 | 1001: item=1B tail=7 OK | 2168: item=8B tail=0 OK

== 判定 2/3:legacy-type ==
问题:2168 metadata 每条目是否多 1 字节 legacy_type?
结论:不存在(是 minecraft-data 的建模差异)  [置信度 high,样本 3]
证据:
  - [s2c] #17 add_entity id=13 len=312 offset=42 count=18: 两种 spec end 相同(210)
  - 命中统计:存在=0,不存在=3,存疑=0

== 判定 3/3:teleport-prefix ==
问题:2168 move_player 的 teleport 是 switch 还是 option(带 1 字节前缀)?
结论:option(有 1 字节 bool 前缀,需要补 00/01)  [置信度 high,样本 92]
证据:
  - [s2c] #61 len=41 mode=teleport | switch: tail=1 OK | option: tail=0 OK
  - 对齐统计:switch=0,option=89,teleport 样本=89,总 move_player=92
```

### 3.4 参数

| 参数 | 适用命令 | 说明 |
| --- | --- | --- |
| `<file.pcap2> \| dir` | `analyze` `verdict` | 文件或目录；目录会递归查找 `*.pcap2` |
| `-json` | `analyze` `verdict` | 输出机器可读 JSON |
| `-out <file>` | `verdict` | 把 JSON 写到文件 |
| `-id <a,b,...>` | `analyze` `hex` | 只看这些包 ID，逗号分隔（默认 `12,13,15,19,31,32`） |
| `-dir c2s\|s2c\|both` | `analyze` | 方向过滤，默认 `both` |
| `-limit <n>` | `analyze` | 每个包最多展示几条样本，默认 8 |
| `-index <n>` | `hex` | 只打印第 n 条（从 0 开始，`-1` 不限） |
| `-max <n>` | `hex` | 每包最多打印多少字节，默认 256 |

退出码：`0` = 正常；`1` = 文件或解析错误；`2` = 参数错误。

---

## 4. 三个判定的原理

三者的共同前提：**同一段字节可以被两种 schema 都"合法"地解析**。合法性只保证不越界，不保证语义正确。因此判据统一落在「解析终点是否与包尾重合」——真格式必须把 payload 恰好吃完，错格式要么剩字节，要么提前撞到 `Remaining() < 0` 式的越界读（`Cursor.Err()`）。

编码约定（两套 schema 共用）：`varint` 为 LEB128 无符号；有符号走 zigzag；`li16 / lu16 / li32` 为小端定长。

### 4.1 `air` 长度：1 字节 vs 8 字节

**分歧**：`1001` 的 `air` 读取是**短路**的——读到第一个满足条件的位置就停，实际只消费 1 字节；`2168` 把它当定长字段，**写满 8 字节**。

**判据**：对每个候选包，两套 schema 各解析一次，看解析后的 `tail`（即 `Cursor.Remaining()`）是否为 0。

- 若 `2168` 为真：`1001` 少读 7 字节。后续字段仍能读通时 `tail == 7`；若少读导致后续定长字段错位到越界，`Err()` 非空。
- 若 `1001` 为真：`2168` 多吃 7 字节，`tail` 要么为负（越界，`Err()` 非空），要么落在后续字段无法解释的余量上。

**为什么文档给不出答案**：`air` 在 `1001` 里没有独立的长度字段，也没有显式的 tag 界定它的边界。短路读与定长读在字节流上**都不会报错**——短路读只是提前收手，定长读只是多吃几个字节，两者都能"解析成功"。文档只能记录两种可能，无法仅凭结构自证；必须靠真机 payload 的后续字段（尤其包尾）来区分。当 `tail == 0` 对两套 schema 同时成立时（见 3.2 的 `#12`），该包对判定**无判别力**，工具会明确标注并排除出计票，而不是硬算一票。

### 4.2 `legacy_type`：是否存在

**分歧**：`1001` 与 `2168` 对同一段区间的归属不同——一处把它解释成「`count` 个元素的列表」，另一处解释成「`count` 字节的块」或直接不存在该字段。

**判据**：双 spec 同时解析同一段字节，满足全部三条才判定存在：

1. 两条解析路径在**同一个 offset** 处命中 metadata 起点（即此前消耗的字节数完全一致）；
2. 两条路径读出的条目数 `count` 相同；
3. `end2168 - end1001 == count`。

条件 3 是核心，原因是**每个条目恒定多 1 字节**：`2168` 的条目是 `key + type + legacy_type + value`，`1001` 是 `key + type + value`。`count` 个条目就正好多 `count` 字节，这是一个线性关系，与 `value` 本身的宽度无关。

反过来：

- `end` 差 `== 0`：两条路径解析结果完全相同 → `legacy_type` 不存在；
- `end` 差既不等于 0 也不等于 `count`：两条路径在某个条目上走了不同分支（例如某条目的 `type` 被错位读成另一个值），属于巧合对齐，不计票。

**为什么文档给不出答案**：`legacy_type` 的「是否存在」取决于 `count` 的语义（是元素个数还是字节长度）。而当 `count == 0` 时，两种解释的字节完全一样（都是 0 字节），结构上不可区分；`count > 0` 时，1 字节元素与 1 字节块的宽度又恰好相同。所以必须要有**非零 `count` 的真实样本**，再叠加对齐关系才能分辨——这正是文档缺的那块拼图。

### 4.3 `teleport`：switch 与 option 差 1 字节

**分歧**：`teleport` 的分支写法两套 schema 相差 **1 字节**（一处用 `u8` 选项，另一处用 `uvarint`，或其中一套多一个前导字节）。

**判据**：按**包尾对齐**判定。对同一 payload 用两种写法各解析一次，看哪一种让解析终点恰好落在 payload 末尾：

- 只有一种写法 `tail == 0`，另一种 `tail == 1` 或 `tail == -1`（越界）→ 直接判定，该包计一票。
- 两种都 `tail == 0` → 无判别力，排除。
- 两种都非 0 → 样本受损（可能该包走了别的分支），排除并计数。

**为什么文档给不出答案**：1 字节的差异在任何单个字段处都不越界——两种写法都能"读过去"。而且这 1 个字节在值域上常常正好构成合法的 `varint` 前缀（如 `0x00`~`0x7F` 都是合法单字节 varint），静态阅读无法从结构上排除任何一种。唯一能定死的办法是**从包尾反推**：谁的终点与包尾重合，谁就是真格式。包尾对齐是这类「1 字节偏移分歧」唯一可靠的裁判，而包尾只有真实 payload 才有。

---

## 5. 已知限制

- **依赖 `github.com/klauspost/compress/s2`**：`body` 是 s2 块，不是标准 zlib/deflate。换库或换压缩级别都会解不开，报 `s2 decode` 错误。
- **不解密**：工具假定 `.pcap2` 里的包体已经是明文，即依赖 bedrocktool 在抓包时完成了握手解密。若用未解密的原始流量喂进来，`packetID` 会是噪声，三项判定全部 `INCONCLUSIVE`。
- **协议版本前提**：`1001` 与 `2168` 两套 schema 的字段布局对应 26.30+ 与 1.26.40+。若抓包来自更低的协议版本（如 `712`、`898`），包 ID 表与字段顺序都不同，交叉解析没有意义。
- **只判定、不修复**：工具不会猜测字段值，也不会回写 schema。它输出结论与证据，schema 的修改仍需人工落到 `docs/PROTOCOL_1001.md` 与对应代码。
- **样本量自己看**：每条结论都带 `样本 N`，N 太小时（比如 1~2 个）置信度会落到 `low`，不要直接采信。
- **不做 UDP/RakNet 层解析**：只处理 bedrocktool 落盘的 `.pcap2`，不读 `.pcap`/`.pcapng`，也不还原 RakNet 分片。

---

## 6. 编译

```bash
cd tools/jercpld-capture
go build -o jercpld-capture .
```

首次构建会拉取 `klauspost/compress`：

```bash
go get github.com/klauspost/compress/s2
```

交叉编译示例（Linux x86_64）：

```bash
GOOS=linux GOARCH=amd64 go build -o jercpld-capture-linux .
```

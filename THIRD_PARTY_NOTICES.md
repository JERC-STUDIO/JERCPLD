# 第三方组件与许可声明

本项目以 Apache License 2.0 发布（见根目录 `LICENSE`）。以下为本项目构建、运行或开发过程中使用的第三方组件。

## 运行时 / 链接依赖

### LeviLamina

- 项目：https://github.com/LiteLDev/LeviLamina
- 许可：LGPL-3.0（仓库中部分闭源组件除外）
- 版权：LeviMC 及其贡献者
- 使用方式：**动态链接**。JERCPLD 以 LeviLamina 原生 mod 形式运行，编译期使用其公开头文件与导入库，运行期由 BDS 进程加载 `LeviLamina.dll`。
- 本项目未修改 LeviLamina 源码，也未将其源码复制进本仓库；构建所需的头文件在构建时获取。
- LGPL-3.0 全文见 `licenses/LGPL-3.0.txt`，其引用的 GPL-3.0 全文见 `licenses/GPL-3.0.txt`。

## 协议编解码

### 当前 mod：自写代码，无第三方 codec

`mod/JERCPLD.Mod/src/JerCpldMod.cpp` 中的协议读写全部为手写 C++：变长整数、字符串、NBT 跳过、包字段插入/裁剪、PlayerList 重写等，未链接或包含任何第三方协议编解码库。

### bedrock-protocol（EndstoneMC）— 计划中，当前未使用

- 项目：https://github.com/EndstoneMC/bedrock-protocol
- 许可：MIT
- 状态：`docs/ROADMAP.md` 的 S3（固定版本 C++ codec）仍为待办；**当前 mod 二进制不包含 `bpc` 生成的任何代码**。
- 注意：`tools/verify-client/` 使用的是 PrismarineJS 的 Node 包 `bedrock-protocol`（同名但不同项目，MIT），仅用于自动验证，不进入 mod。

### endweave（EndstoneMC）

- 项目：https://github.com/EndstoneMC/endweave
- 许可：Apache-2.0
- 使用方式：**未引用其代码**。仅在 `docs/DESIGN.md` 中参考其按版本划分差异的思路；当前实现为单文件直线式翻译，不存在 endweave 源码或数据结构。

## 验证工具依赖（不进入 mod 二进制）

| 组件 | 版本 | 许可 | 用途 |
| --- | --- | --- | --- |
| bedrock-protocol（PrismarineJS） | 3.61.0 | MIT | 模拟客户端 / 包解析验证 |
| minecraft-data | 3.119.0 | MIT | 1.26.51 / 1.26.60 包结构与方块物品表对比 |
| protodef | 1.19.0 | MIT | bedrock-protocol 的序列化依赖 |
| nethernet | 1.1.2 | MIT | 模拟客户端的 NetherNet 传输 |
| raknet-native | 1.2.3 | ISC | 模拟客户端的 RakNet 传输 |

## 数据文件来源

- mod 运行时不内置方块表、物品表、生物群系定义：这些数据由 BDS 在 `StartGame`（`block_properties`）、`ItemRegistry`（162）、`BiomeDefinitionList`（122）中下发，mod 只做字段级翻译。
- 文档中的 2193/2225 方块与物品差集数字来自 `minecraft-data`（MIT）的 1.26.51 / 1.26.60 数据，仅用于分析与验证，不随 mod 分发。
- 协议字段定义参考 Mojang 官方 `https://mojang.github.io/bedrock-protocol-docs/`，仅作为文档依据，不包含其代码。

## 构建期头文件依赖

以下库仅用于编译 mod，均以头文件形式使用，不单独分发：

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| entt | MIT | ECS / 容器 |
| fmt | MIT | 日志格式化 |
| glm | MIT | 数学库 |
| GSL | MIT | Guideline Support Library |
| LevelDB (headers) | BSD-3-Clause | 存储接口 |
| RapidJSON | MIT | JSON |
| type_safe | MIT | 强类型封装 |
| expected-lite | BSL-1.0 | expected 实现 |
| magic_enum | MIT | 枚举反射 |
| pcg-cpp | Apache-2.0 | 随机数 |
| CTRE | Apache-2.0 | 编译期正则 |
| stb_truetype | MIT / Public Domain | 字体 |
| moodycamel ConcurrentQueue | BSD-2-Clause | 无锁队列 |
| parallel-hashmap | Apache-2.0 | 哈希表 |
| nlohmann/json | MIT | JSON |

## 明确排除：ArkMirage（GPL-3.0）

- 旧独立代理（`src/JERCPLD.NetherNet` 等 C# 工程）引用 `ArkMirage.NetherNet`（GPL-3.0）NuGet 包。
- 该目录**仅作内部参考，不参与 mod 构建，不随任何发布物分发**。
- 最终交付的 `JERCPLD.dll` 是纯 C++ 原生 mod，不链接、不包含 ArkMirage 或任何 GPL-3.0 代码。

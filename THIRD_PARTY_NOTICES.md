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

### bedrock-protocol（EndstoneMC）

- 项目：https://github.com/EndstoneMC/bedrock-protocol
- 许可：MIT
- 版权：The Endstone Project 及其贡献者
- 使用方式：使用其 `bpc` 生成的 C++ codec（仅依赖标准库），生成的代码保留原始 MIT 许可与版权声明。当前处于接入阶段（S3）。

### endweave（EndstoneMC）

- 项目：https://github.com/EndstoneMC/endweave
- 许可：Apache-2.0
- 使用方式：仅参考其按版本划分的差异目录结构与纯函数式重写思路；**不复用其路由与链式翻译架构**。若移植其代码或数据结构，对应文件头必须注明：

```
Modified for JERCPLD
Original: https://github.com/EndstoneMC/endweave (Apache-2.0)
```

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

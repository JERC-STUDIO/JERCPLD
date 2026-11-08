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

## B7 验证路径边界（RakNet vs NetherNet）

- 事实：最小自检走 RakNet；BDS 1.26.51 启动时明确警告「NetherNet 是唯一受支持的传输」，RakNet 仅供本机脚本自检。
- 风险：不能把 RakNet 的通过结论当作 NetherNet 全链路已验证。
- 处理：`docs/VERIFY_CHECKLIST.md` 明确区分两类结论；NetherNet 路径由真实客户端复核。
- 状态：持续记录。

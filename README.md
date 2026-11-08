# JERCPLD

> **声明**：本项目非 LeviMC 官方产品，未经其认可，与 LeviMC 及其关联项目无任何隶属或背书关系。LeviLamina 及相关名称归其各自权利人所有。
>
> **Disclaimer**: Not an official LeviMC product. Not approved by LeviMC and not associated with LeviMC. LeviLamina and related names are trademarks of their respective owners.

LeviLamina 原生 C++ mod，在 BDS 进程内做跨版本翻译。目标：26.30–26.62 客户端进入 26.50/26.51 服务端。

## 当前状态（2026-11-09）

| 项 | 状态 |
| --- | --- |
| mod 被真实 BDS 1.26.51 加载 | 已完成（横幅可见） |
| 26.60 / 26.62 客户端进入世界 | **已完成**（2026-11-09 02:0x 真实 26.62 客户端确认） |
| 地形 / 子区块 | 已完成基础链路：LevelChunk 改写为 skeleton，客户端 SubChunkRequest，BDS 返回真实地形 |
| 移动 / 基础交互 | 进行中：player_auth_input 正常；animate(44) 的 hand 字段已裁剪，待真实客户端复测攻击/挖掘/开门 |
| 方块调色板校验 | 已定位 -63 block_mismatch；StartGame `block_pallette_checksum` 清 0 跳过校验（勿回退） |
| 内置抓包 | 已完成（默认关闭，可运行中热开启） |
| 自动自检 | `tools/verify-client/verify.js`（NetherNet / RakNet） |

## 产物与安装

- 构建产物：`mod/JERCPLD.Mod/build/JERCPLD.dll`
- 安装目录：`d:\ylcs\plugins\JERCPLD\`（DLL + manifest.json + tooth.json + config/config.json）
- 一键安装：`mod/JERCPLD.Mod/install.ps1 -ServerDir d:\ylcs`（加 `-SkipBuild` 可只复制现有 DLL）

配置项（`plugins/JERCPLD/config/config.json`，抓包段支持热读取）：

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

## 前置

- BDS 1.26.51（本机 `d:\ylcs\bedrock_server_mod.exe`）
- LeviLamina 26.51.5（`plugins/LeviLamina`）
- 编译 mod 需要 VS Build Tools（MSVC 14.44）与 Windows SDK
- 首次构建先运行 `mod\JERCPLD.Mod\fetch-deps.ps1` 获取头文件依赖；LeviLamina 头文件与导入库通过 `LEVILAMINA_SRC`、`LEVILAMINA_LIB_DIR` 指定（默认指向本机 `d:\ylcs\_tools`）

## 安装

```powershell
powershell -ExecutionPolicy Bypass -File d:\ylcs\JERCPLD\mod\JERCPLD.Mod\install.ps1 -ServerDir d:\ylcs
```

脚本会编译并复制到 `plugins\JERCPLD\`：

```
plugins/JERCPLD/
  JERCPLD.dll
  manifest.json
  tooth.json
  config/config.json
```

配置项（`config/config.json`）：

```json
{
  "translationEnabled": false,
  "serverVersion": "1.26.51",
  "serverProtocol": 2193,
  "acceptMinProtocol": 1001,
  "acceptMaxProtocol": 2225
}
```

## server.properties 关键项

```properties
transport=nethernet
server-port=19132
online-mode=false   # 测试环境；正版环境改 true
compression-algorithm=zlib
enable-lan-visibility=true
block-network-ids-are-hashes=true
```

## 加载成功的标志

BDS 日志出现：

```
[GMLIB] 正在加载 JERCPLD v0.1.0
[JERCPLD] config loaded: ./plugins/JERCPLD\config\config.json
[JERCPLD] loaded v0.1.0 | server 1.26.51/2193 | clients protocol 1001-2225 | translation=OFF
JERCPLD 已加载
```

控制台横幅：

```
     ###  #######  ######   ######  ######   ##      ######
      ##  ##       ##   ## ##      ##   ##  ##      ##   ##
      ##  #####    ######  ##      ######   ##      ##   ##
      ##  ##       ## ##   ##      ##       ##      ##   ##
##    ##  ##       ##  ##  ##      ##       ##      ##   ##
 #####   #######  ##   ##  ######  ##       ####### ######
==========================================================================
  Mod name        : JERCPLD
  Mod version     : 0.1.0
  Server target   : BDS 1.26.51 (protocol 2193)
  Client range    : protocol 1001 - 2225 (1.26.30 - 1.26.62)
  Translation     : OFF (skeleton)
  Config file     : config/config.json
==========================================================================
```

## 26.60 / 26.62 客户端当前能力与已知问题

已验证：

- 登录、StartGame、PlayerList、区块加载、进入世界（真实 26.62 客户端确认）
- 地形链路：LevelChunk skeleton → SubChunkRequest → 服务端返回真实子区块
- `player_auth_input` 移动包持续上行，服务端不踢
- `animate(44)` 的 2225 `hand` 字段已裁剪，避免服务端 malformed 踢人（模拟客户端回归通过）

已知问题（按修复优先级）：

1. 移动/交互需真实客户端复测：走动、跳跃、放置/破坏方块、开门、攻击是否无回弹/丢动作。
2. 方块调色板校验靠 `block_pallette_checksum=0` 跳过；`block-network-ids-are-hashes` 必须保持 `true`。
3. 2193↔2225 方块表差集：方块 +3（`icicle`/`ice_crystal`/`disc_press`），共享方块 ID 一致；自定义方块（`create:`/`nps_tbe:`/`venture:`）显示质量待真实客户端验证。
4. 物品表差集：+4，82 个共有物品数字 ID 变化；Inventory/ItemStackRequest 尚未逐项翻译。
5. PlayerSkin / 实体外观、部分 2225 新增包尚未覆盖。
6. 抓包对单个 >1MB 出站包可能只记录分片，离线解码需结合客户端 violation 判断。

## 依赖事实

- 协议读写、包改写全部为 `mod/JERCPLD.Mod/src/JerCpldMod.cpp` 手写 C++，未使用 EndstoneMC/bedrock-protocol 生成的 codec，也未引用 endweave 代码。
- 运行时仅动态链接 `LeviLamina.dll`（LGPL-3.0，动态链接）与 MSVC 运行库。
- 构建期头文件：nlohmann/json、fmt、entt 等（均为 MIT/BSL 等宽松许可），详见 `THIRD_PARTY_NOTICES.md`。
- 未包含 Glacie、ProtocolLib 的代码或二进制；未包含 AGPL 组件。
- mod 不内置方块/物品/生物表，运行时从 BDS 的 StartGame / ItemRegistry / BiomeDefinitionList 读取。

## 旧独立代理（不分发）

`src/` 下的 C# 工程与 `dist/win-x64/JERCPLD.exe` 仅作内部参考，**不要分发**。其中 `JERCPLD.NetherNet` 引用 ArkMirage（GPL-3.0）。插件形态不引用该工程，最终 mod 二进制不含 GPL-3.0 代码。

## 文档

- 路线图：`docs/ROADMAP.md`
- 进度：`docs/STATE.json`
- 阻塞：`docs/BLOCKERS.md`
- 用户验证清单：`docs/VERIFY_CHECKLIST.md`
- 抓包使用说明：`docs/CAPTURE.md`

## 许可

本项目以 Apache License 2.0 发布，见 [LICENSE](LICENSE)。
第三方组件与许可声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
GPL-3.0 与 LGPL-3.0 全文位于 `licenses/`。

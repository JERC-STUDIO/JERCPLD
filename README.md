# JERCPLD

> **声明**：本项目非 LeviMC 官方产品，未经其认可，与 LeviMC 及其关联项目无任何隶属或背书关系。LeviLamina 及相关名称归其各自权利人所有。
>
> **Disclaimer**: Not an official LeviMC product. Not approved by LeviMC and not associated with LeviMC. LeviLamina and related names are trademarks of their respective owners.

LeviLamina 原生 C++ mod，在 BDS 进程内做跨版本翻译。目标：26.30–26.62 客户端进入 26.50/26.51 服务端。

## 当前状态（2026-11-08）

| 项 | 状态 |
| --- | --- |
| mod 被真实 BDS 1.26.51 加载 | 已完成（横幅可见） |
| 一键安装 | `mod/JERCPLD.Mod/install.ps1` |
| 翻译 | **OFF**（骨架阶段，尚未改写协议号） |
| 自动自检 | `tools/verify-client/verify.js`（RakNet 路径，非 NetherNet） |

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

## 26.60 客户端当前会发生什么

翻译开关为 OFF，mod 只加载、不参与包处理。26.60（协议 2225）客户端会在登录阶段被服务端以版本不匹配拒绝，**这是预期行为**，不代表安装失败。

- 能连上、能看到服务器 → 传输与配置正常
- 登录后被踢、提示版本不匹配 → 翻译未开启（当前阶段）
- 根本发现不了服务器 → 检查 `transport`、端口、防火墙、`enable-lan-visibility`

## 旧独立代理（不分发）

`src/` 下的 C# 工程与 `dist/win-x64/JERCPLD.exe` 仅作内部参考，**不要分发**。其中 `JERCPLD.NetherNet` 引用 ArkMirage（GPL-3.0）。插件形态不引用该工程，最终 mod 二进制不含 GPL-3.0 代码。

## 文档

- 路线图：`docs/ROADMAP.md`
- 进度：`docs/STATE.json`
- 阻塞：`docs/BLOCKERS.md`
- 用户验证清单：`docs/VERIFY_CHECKLIST.md`

## 许可

本项目以 Apache License 2.0 发布，见 [LICENSE](LICENSE)。
第三方组件与许可声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
GPL-3.0 与 LGPL-3.0 全文位于 `licenses/`。

# 用户验证清单（真实客户端）

> 目标：用真实 26.60 客户端验证 mod 是否加载、传输是否可达、翻译是否生效。
> 重要：自动自检走的是 RakNet；正式环境是 NetherNet。两类结论不要混用，见文末。

## 1. 安装

1. 确认 BDS 为 1.26.51，且 `plugins/LeviLamina` 存在（LeviLamina 26.51.5）。
2. 执行：

```powershell
powershell -ExecutionPolicy Bypass -File d:\ylcs\JERCPLD\mod\JERCPLD.Mod\install.ps1 -ServerDir d:\ylcs
```

3. 确认文件存在：

```
plugins\JERCPLD\JERCPLD.dll
plugins\JERCPLD\manifest.json
plugins\JERCPLD\config\config.json
```

4. 检查 `server.properties`：

```properties
transport=nethernet
server-port=19132
online-mode=false   # 测试环境；正版环境改 true
enable-lan-visibility=true
```

5. 重启 BDS。

## 2. 判定 mod 已加载

BDS 控制台/`logs/latest.log` 必须出现：

```
[JERCPLD] loaded v0.1.0 | server 1.26.51/2193 | clients protocol 1001-2225 | translation=OFF
JERCPLD 已加载
```

并出现 `Mod name / Mod version / Server target / Client range / Translation` 信息块。

没有 → mod 未加载，检查 `plugins/JERCPLD/manifest.json`、`JERCPLD.dll` 是否存在，以及日志中是否有 `JERCPLD 加载失败`。

## 3. 用 26.60 客户端连接

- 进入游戏 → 服务器列表 → 添加服务器：地址本机 IP，端口 19132。
- 观察点：

| 现象 | 判断 |
| --- | --- |
| 服务器列表能看到、能开始连接 | 传输/配置正常 |
| 连接后提示版本不匹配/被踢 | 翻译未生效（当前 `translationEnabled=false` 的预期结果） |
| 完全发现不了服务器 | 配置问题：`transport`、端口、防火墙、LAN 可见性 |
| 能进 StartGame 但地形/物品错 | 翻译问题（进入 S5 后才可能出现） |

当前版本（0.1.0）翻译开关为 OFF，**预期在登录阶段被拒**。这一步只验证「装上了、连得上」。

## 4. 通过标准（分阶段）

### 阶段 A：可交付形态（当前）

- [ ] BDS 日志出现 `[JERCPLD] loaded v0.1.0`
- [ ] 26.60 客户端能发现服务器并发起连接
- [ ] 被拒时服务端日志能看到对应连接记录

### 阶段 B：版本校验通过（S4 完成后）

- [ ] 26.60 客户端不再因协议版本被踢
- [ ] 客户端能收到 StartGame（或至少进入资源包阶段）

### 阶段 C：进世界（S5 完成后）

- [ ] 能移动
- [ ] 地形正常
- [ ] 物品栏正常

## 5. 失败时导出什么

1. `logs/latest.log`（BDS 日志）
2. `plugins/JERCPLD/config/config.json`
3. 客户端报错截图（含错误码）
4. S6 完成后：`plugins/JERCPLD/diagnostics/session-*.log`（会话包序列）

## 6. 验证边界（必读）

| 结论 | 来源 | 状态 |
| --- | --- | --- |
| 握手/登录/StartGame 可跑通 | RakNet 自检（`transport=raknet`） | 仅本机自检，BDS 1.26.51 明确警告 RakNet 不是受支持传输 |
| NetherNet 下可连接 | 未验证 | 必须真实客户端复核 |
| 进世界/地形/物品/实体 | 未验证 | 必须真实客户端复核 |

不要用 RakNet 自检的 PASS 推断 NetherNet 全链路已通过。

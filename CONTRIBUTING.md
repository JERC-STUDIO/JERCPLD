# 贡献指南

## 流程

1. Fork 本仓库，从 `main` 创建分支。
2. 提交 Pull Request，描述改动目的、影响范围与验证方式。
3. 至少一名维护者评审通过后合入。

## 提交签名（DCO）

所有提交必须带 DCO sign-off：

```bash
git commit -s -m "feat: ..."
```

提交信息末尾应包含：

```
Signed-off-by: Your Name <you@example.com>
```

未签名的 PR 不会被合入。

## 原创性声明

在 PR 描述中必须说明：

- 代码是否为原创；
- 若参考或移植了第三方仓库，注明仓库地址、具体文件与许可；
- 移植代码的文件头必须保留原始版权与许可声明，并注明 `Modified for JERCPLD`。

## 禁止合入的内容

- **AGPL 代码**；
- **反编译产物**（包括从 BDS、客户端或其他闭源程序提取的代码/数据）；
- 未经许可的 GPL-3.0 代码；
- 任何 GPL-3.0 的二进制或构建产物（例如含 ArkMirage 的旧代理 exe）。

## 构建与测试

```powershell
# 获取构建依赖（首次）
powershell -ExecutionPolicy Bypass -File mod\JERCPLD.Mod\fetch-deps.ps1

# 编译并安装 mod
powershell -ExecutionPolicy Bypass -File mod\JERCPLD.Mod\install.ps1 -ServerDir d:\ylcs

# 运行现有测试
dotnet test tests\JERCPLD.Translation.Tests\JERCPLD.Translation.Tests.csproj -c Release
```

## 代码风格

- C++20，MSVC 编译，遵循现有文件风格；
- 不提交构建产物（`build/`、`bin/`、`obj/`、`dist/`、`deps/`）；
- 提交前确认 `docs/STATE.json` 与路线图状态一致（如涉及）。

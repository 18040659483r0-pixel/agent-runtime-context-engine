# Alpha 0.1.6 · 测试与发布报告（v9.1）

> English: [`ALPHA-0.1.6-TEST-REPORT.md`](ALPHA-0.1.6-TEST-REPORT.md) · 日本語: [`ALPHA-0.1.6-TEST-REPORT.ja.md`](ALPHA-0.1.6-TEST-REPORT.ja.md) · 发布说明: [`ALPHA-0.1.6-RELEASE.zh-CN.md`](ALPHA-0.1.6-RELEASE.zh-CN.md)

读数均在**切片内**、清掉构建产物后取得。

| 项 | 值 |
|---|---|
| 编译 | **0 错** |
| 测试 | **1359 / 1361** |
| 2 条失败 | `PitfallsDerivedTests` —— **内部专属**（需要内部坑集） |
| 发布扫描 | **0 硬红线** |
| 协议 | v104 · 26 行 / 22,196 字符 / 5,549 token（≤5,600） |
| 切片规模 | **655 文件 / tar 1.6 MB**（无 bin/obj） |
| 部署语料 | 5 份引导文档 + 4 目录 + 知识种子 19/19/19/19 + 派生层 |

## 复现

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## 部署语料检查

```bash
ls whitebox/workspace
# AGENTS.md BULLETIN.md GLOSSARY.md USER.md ONBOARDING.md docs handoff knowledge memory
```

首次启动显示一次欢迎导览，之后正常进入控制台。

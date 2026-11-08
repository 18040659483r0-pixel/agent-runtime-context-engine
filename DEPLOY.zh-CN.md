# DEPLOY — WhiteBox 1.0.0-beta.1（中文）

> 🌐 [English](DEPLOY.md) · **中文** · [日本語](DEPLOY.ja.md)

从下载到跑起来，五分钟。这是部署手册，不是架构论文。

## 0. 你需要什么

| 项 | 说明 |
|---|---|
| **.NET 10** | `dotnet --version` 应为 `10.x`；下载：https://dotnet.microsoft.com/download/dotnet/10.0 |
| macOS / Windows / Linux | 包是框架依赖的；同一份文件在任意装了 .NET 10 的系统上都能跑 |
| provider 密钥 | 默认 **DeepSeek 官方**（`https://api.deepseek.com`，模型 `deepseek-v4-flash`） |
| 网络 | 只有调模型时需要；setup 本身离线 |

## 1. 解压

```bash
tar -xzf deploy-manager-1.0.0-beta.1.tar.gz
cd deploy-manager-1.0.0-beta.1
```

Windows 用任意解压工具，然后在 `cmd` / PowerShell 里执行后面的命令（`/` 换成 `\`）。

## 2. 跑配置向导

```bash
dotnet wcm.dll setup
```

只问四件事：

1. **安装目录** —— 默认 `~/WhiteBox`（Windows：`%USERPROFILE%\WhiteBox`）。
2. **Provider** —— 1) DeepSeek 官方（推荐）或 2) 自定义 OpenAI 兼容（BaseUrl + 模型）。
3. **思考档位** —— 默认 **low**（快、省、够用）。不建议 `high`；无论哪档，WhiteBox 都自带思考链呈现，不会看不到推理过程。
4. **API Key** —— 掩码输入（不回显）；写入 `<安装目录>/state/api_key`（`0600`）。

随后它会装运行时、冻结语料、5 条知识种子、首访引导、过程发现活版，构建知识索引，并跑一遍 `doctor`。

## 3. 启动

```bash
./run.sh            # macOS / Linux
run.cmd             # Windows
# 或
dotnet wcm.dll run --target ~/WhiteBox
```

## 无人值守（CI / 批量）

```bash
dotnet wcm.dll setup --target /opt/wb --yes --no-run \
  --api-key-file ~/.agentruntime/api_key
```

- `--from-config <文件>`：复用现有 WhiteBox 的 `config.xml`（BaseUrl / 模型 / 思考档）。
- `--api-key-file <文件>`：从文件读密钥 —— **绝不把密钥写进命令行**。
- `--think low|medium|high`、`--build`（没有预编二进制时就地编译）、`--force`（覆盖目标）。

## 目录结构

```
<安装目录>/
  config.xml                 provider / 思考档 / 路径
  app/tui|cli/               运行时二进制
  tools/closeout/            收尾工具 + 声明
  frozen/                    公开冻结语料
  whitebox/workspace/        干净工作区：知识种子、首访引导、过程发现活版
  state/                     流 / 快照 / api_key（0600）
  run.sh / run.cmd
```

## 安全

- 密钥只放在**安装目录内**（`state/api_key`），**不写家目录**、不进 `config.xml`。
- setup 从不为密钥回显；自动化请用 `--api-key-file`。
- 换密钥：重跑 `setup`，或直接覆盖 `state/api_key`。

## 排障

| 现象 | 处理 |
|---|---|
| `dotnet: command not found` | 安装 .NET 10 后重开终端 |
| `找不到知识树派生层` | 重跑 `dotnet wcm.dll setup`（会重建索引），或跑 `app/cli/... --kn-build whitebox/workspace` |
| `401 / unauthorized` | 检查 `state/api_key`（单行、无引号） |
| TUI 行为还是旧的 | 确认是从**这个安装目录**起的 TUI |
| `doctor` 有红 | 它会指出缺哪项；修好后重跑 `dotnet wcm.dll doctor --target <目录>` |

## 自测

```bash
dotnet wcm.dll selftest                           # 语料自检（17/17）
dotnet app/tui/AgentRuntime.Tui2.dll --selftest   # TUI 逻辑自检
```

## 卸载

删掉安装目录即可。除 `state/` 里的密钥文件外，不会在目录外写任何东西。

# 部署测试套件（deploy-testsuite）

**测什么**：按 `deploy` 的正式流程，在**沙盒**里装一次，用**官方 DeepSeek** 真机跑一轮，再验证取数去重、初始库还原、整目录搬移。

**语料**：部署版（`dist/alpha-0.1` 的 `templates/workspace` + `frozen`）。
**二进制**：当前源码最新（`--refresh` 先 `dotnet publish` 到包的 `app/`，默认开启）。

## 跑法

```bash
# 只打印计划
dotnet run --project tools/deploy-testsuite

# 真跑（会调官方 DS，费用很小：D5 只问一次「读一条 L1」）
dotnet run --project tools/deploy-testsuite -- --run

# 选项
#   --package <dir>   部署包根（默认 dist/alpha-0.1）
#   --proj <dir>      源码仓根（默认自动找 AgentRuntime.slnx）
#   --out <dir>       证据目录（默认 .stage1/deploy-tests/<时间戳>）
#   --no-refresh      不 publish，直接用包里现有二进制
#   --keep            保留沙盒副本（默认删 pkg/moved，保留 report 与 target）
```

## 判据（D1~D8）

| # | 场景 | 硬判据 |
|---|---|---|
| D1 | 包完整性 | 13 件必需文件齐全（二进制 / 模板 / 配置 / 部署器 / 冻结 / 知识） |
| D2 | init 沙盒 | `deploy init` rc=0，目标出现 `config.xml` + 自包含 `app/` |
| D3 | provider = 官方 DS | `BaseUrl=https://api.deepseek.com` · `Model=deepseek-v4-flash` · key 只走 env/文件 |
| D4 | doctor + check | 两条都 rc=0；知识树 20 节点、冻结语料版本 82/2/33 |
| D5 | 真机 TUI | 用官方 DS 读 `knowledge.md` 第一条 `<!--kn: L1-`，输出含 id 与 `[DONE]`；**rc=0 · 拒绝 0** |
| D6 | 取数去重 | 首次 `Kind=Knowledge`；第二次只出 `kn-ref`（`Kind=Hint`） |
| D7 | reset | 污染知识文件后 `reset --all` ⇒ sha 与初始一致；`check` 仍绿 |
| D8 | 整目录搬移 | 把 target 整棵复制到别处，`doctor` + `check` 仍全绿（无绝对路径依赖） |

## 边界

- D5 是**唯一花钱**的一段（一次真模型调用）；其余全是离线命令。
- 密钥不落入任何测试产物：只验证 `ApiKeyEnv` / `ApiKeyFile` 两个字段。
- 判决即 `report.xml` 的 `ok="1"`；任何红都退出码 1，不静默。

## 已知限制

- 需要 .NET 10 与可用 DeepSeek key（`~/.agentruntime/api_key` 或 `AGENTRUNTIME_API_KEY`）。
- D8 只证明**同一台机器上搬目录**；跨机器还须目标机装 .NET 10（`deploy doctor` 会报）。

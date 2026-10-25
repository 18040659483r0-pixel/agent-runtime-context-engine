# RUNS · 部署测试套件

## v1 · 2026-10-17 18:18（**8/8 通过**）

**环境**：macOS arm64 · .NET 10.0.400 · 官方 DeepSeek（`https://api.deepseek.com` · `deepseek-v4-flash`）· 协议 v74。
**语料**：部署版（`dist/alpha-0.1`）· **二进制**：当前源码最新（套件 `--refresh` 重新 publish）。
**命令**：`dotnet run --project tools/deploy-testsuite -- --run`

| # | 场景 | 结果 | 读数 |
|---|---|---|---|
| D1 | 包完整性 | ✅ | 13 件齐 |
| D2 | init 沙盒 | ✅ | 目标自包含（config + app + 模板 + 冻结 + 初始库） |
| D3 | provider = 官方 DS | ✅ | `https://api.deepseek.com` / `deepseek-v4-flash` / env=AGENTRUNTIME_API_KEY / file=~/.agentruntime/api_key |
| D4 | doctor + check | ✅ | doctor=0 · check=0 · 知识树 20 节点 · 冻结 8.1 KB / 2,428 token（版本 82/2/33） |
| D5 | 真机 TUI | ✅ | **rc=0 · 2 轮 · 1 工具 · 拒绝 0 · 5 s** · 输出含 `L1-…` 与 `[DONE]` |
| D6 | 取数去重 | ✅ | 首次 `Knowledge` + 二次 `kn-ref`（`Hint`） |
| D7 | reset 初始库 | ✅ | 污染后 sha 还原一致 · check=0 |
| D8 | 整目录搬移 | ✅ | doctor=0 · check=0 |

**结论**：部署流程（init → provider → doctor/check → 真机 → reset → 搬移）在沙盒里**一次走通**；官方 DS 调用、知识取用/去重、初始库还原、无绝对路径依赖都已验证。

**证据**：`.stage1/deploy-tests/20261017-181819/report.xml`（总报告）+ 各命令转录（套件控制台输出）。

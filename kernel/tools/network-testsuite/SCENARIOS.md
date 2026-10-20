# I 系列 · 网络工具性能测试套件 **v1（2026-10-18 01:5x）**

> **并列关系**：M 系列（`tools/master-testsuite/`）= master 本机能力；W 系列（`tools/worker-mode-acceptance/`）= master-worker 派发；**I 系列 = 网络工具第一批**。
> **只测网络工具**：`web_fetch` / `web_search` / `http_request` / `web_download` / `web_search_extract`。
> **判据两层**：功能（工具真的做成 / 该拒的真拒）+ 能效（**真实 provider token + 墙钟**）。

## 一、怎么跑

```bash
# 先编译运行时（套件用 Debug 二进制）
dotnet build AgentRuntime.slnx

# 全跑（8 条；每条一次完整 agent 轮，会调官方 provider）
dotnet run --project tools/network-testsuite

# 只跑某几条 / 只列清单
dotnet run --project tools/network-testsuite -- --only I1,I4
dotnet run --project tools/network-testsuite -- --list
```

产物：`.stage1/net-suite/<时间戳>/`

- `report.md` / `report.xml`：逐条结果 + token + 墙钟 + 合计
- `I1/stream.xml` + `stream.xml.usage.xml`：工具事实与真实用量
- `I1/stdout.txt` / `stderr.txt` / `prompt.txt`：现场
- `I4/downloads/`：下载落盘证据

## 二、用料与判据

| # | 场景 | 题面要点 | 判据（工具结果 / 盘上） |
|---|---|---|---|
| **I1** | `web_fetch` 正文 | 读 `https://example.com` | `status=200` + `title=Example Domain` |
| **I2** | `http_request` GET | GET `https://example.com` | `status=200` + 正文含 `Example Domain` |
| **I3** | `http_request` POST | POST `https://httpbin.org/post`，body `{"suite":"I3"}` | `status=200` + 回显含 `I3` |
| **I4** | `web_download` 落盘 | 下载 W3C dummy PDF 到 `i4.pdf` | 工具报 `path=`/`sha256=` **且** 文件真在 `I4/downloads/`、> 1 KB |
| **I5** | `web_search` 搜索 | 搜 `OpenClaw`，前 3 条 | `backend=wikipedia` + 至少 1 条结果 |
| **I6** | `web_search_extract` 批量 | 两个 URL（example + Wikipedia） | `pages=2` + `title=Example Domain` |
| **I7** | **SSRF 负例** | `web_fetch http://127.0.0.1/` | 工具结果含「禁止 / 私网」**且**没有成功 `status=200` |
| **I8** | **凭据负例** | `http_request ... authRef=missing` | 工具结果含 `authRef` + `未声明` |

**通判据（全条适用）**

1. CLI 退出码 0；
2. 题面点名的工具**真的被调用**（从 `AgentOutput` 的 `<name>` 读，不看自述）；
3. **`ToolDenied` = 0**（负例要走「工具执行失败」而不是审批拒绝）；
4. 至少一条 `ToolResult`；
5. 特定判据全中（上表）。

## 三、读数口径（与 M / W 系列可直接对比）

| 字段 | 来源 |
|---|---|
| `rounds` | `<stream>.usage.xml` 的 `<Turn>` 条数 |
| `tools` | `AgentOutput` 里 `[TOOL]` 元素 `<name>` 的个数 |
| `deny` | 流里 `ToolDenied` 事件数 |
| `prompt / cached / completion / total` | `<stream>.usage.xml` 逐轮求和（provider 真值） |
| `wall` | 子进程从启动到退出（含模型与网络） |

## 四、边界

- **搜索后端 = Wikipedia（无密钥）**：为了 I 系列可重复跑；生产可换成 brave / tavily / custom（同一 `IWebSearchBackend`）。
- 外部站点可能临时不可达；红了先看 `stderr.txt` 与流，不把「站点挂了」误判成工具 bug。
- 本套件**不测** `web_browser` / 表单 / 上传 / WebSocket / DNS（第二批，未实现）。

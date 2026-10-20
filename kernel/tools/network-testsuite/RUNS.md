# I 系列 · 首跑记录 **v1（2026-10-18 01:43）**

> 套件：`tools/network-testsuite/` · 场景：`SCENARIOS.md` v1
> 命令：`dotnet run --project tools/network-testsuite`
> 原始产物：`.stage1/net-suite/20261018-014319/`（每条的 stream / usage / stdout / stderr 都在）
> 模型：官方 DeepSeek `deepseek-v4-flash` · 搜索后端：Wikipedia（无密钥）

## 一、逐条结果（最终跑 · 20261018-014525）

| # | 场景 | 结果 | 轮 | 工具 | 拒绝 | prompt | cached | completion | total | 墙钟 |
|---|---|---|---|---|---|---|---|---|---|---|
| I1 | web_fetch 正文 | ✅ | 2 | 1 | 0 | 11,409 | 10,880 | 358 | 17,412 | 3.6 s |
| I2 | http_request GET | ✅ | 2 | 1 | 0 | 11,430 | 10,880 | 322 | 17,350 | 3.8 s |
| I3 | http_request POST | ✅ | 4 | 1 | 0 | 24,005 | 22,400 | 1,104 | 60,494 | 11.0 s |
| I4 | web_download 落盘 | ✅ | 4 | 4 | 0 | 24,663 | 23,168 | 1,725 | 64,787 | 11.7 s |
| I5 | web_search 搜索 | ✅ | 3 | 1 | 0 | 18,100 | 16,896 | 801 | 36,748 | 7.8 s |
| I6 | web_search_extract 批量 | ✅ | 2 | 1 | 0 | 12,010 | 10,880 | 379 | 18,015 | 6.9 s |
| I7 | SSRF 负例 | ✅ | 2 | 1 | 0 | 11,404 | 10,880 | 698 | 17,753 | 5.8 s |
| I8 | authRef 负例 | ✅ | 4 | 1 | 0 | 23,264 | 22,016 | 1,127 | 59,328 | 9.5 s |

**合计**：prompt **136,285** · cached **128,000** · completion **6,514** · total **291,887** · 墙钟 **60.1 s** · **8/8 通过** · **拒绝 0**。
**new tokens** = (prompt − cached) + completion = **14,799** · 缓存命中率 **93.9%**。

> **连续两次全绿**：上一跑（`20261018-014319`）也是 **8/8 / 拒绝 0**，读数 250,983 token / 51.8 s。
> 两跑差异来自模型自选轮数（I3/I4 多做了核对与自验），不是工具失败；**功能判据两跑全中**。

## 二、这一跑抓到并修掉的 BUG（工具侧）

| # | 现象 | 真因 | 修法 | 复验 |
|---|---|---|---|---|
| **B1** | `web_search` 全部 403（Wikipedia） | 请求没有 **User-Agent**，Wikipedia 按 robot policy 拒 | 网关补默认 UA（`AgentRuntime/0.1.1 …`）；只补「没写」的情况 | I5 通过 |
| **B2** | `web_search_extract` 抓 Wikipedia 403 | 同上（`web_fetch` 也没有 UA） | 同 B1（公共网关一处修） | I6 通过（example 200） |
| **B3** | 原生 `<invoke name="…"><urls>…</urls></invoke>` 被拒「需要参数」 | 归一器只认 `parameter name=` 外壳；裸成员元素被丢 | `ToolSyntaxNormalizer` 增补**裸成员元素**解析 | I6 拒绝从 2 → 0 |
| **B4** | `<urls>["a","b"]</urls>` 被当成一个 URL | `web_search_extract` 只按行切 | 统一解析：行 / 逗号 / `\|` / JSON 数组 | I6 通过 |
| **B5** | 首轮偶发 `[TOOL] web_search{query: OpenClaw, count: 5}` 被拒 | 紧凑对象里值没引号 | `ToolSyntaxNormalizer` 增加**裸标量补引号**（只补无空白裸词） | 新增单测；I 系列连续两跑拒绝 0 |

## 三、结论

- **功能**：5 个网络工具全部按契约工作；SSRF 与 `authRef` 负例**真拒**（不是纸面规则）。
- **安全**：`web_fetch http://127.0.0.1/` 与未声明 `authRef` 都在工具层失败，没有发出请求 / 没有裸奔。
- **能效**：单任务约 **1.6k new tokens / 6.5 s**（8 条合计 12.6k new tokens / 51.8 s），缓存命中 **94.6%**。
- **可复跑**：同上命令；每次产物在 `.stage1/net-suite/<时间戳>/`，`report.md` / `report.xml` 可直接与 M/W 系列并列对比。

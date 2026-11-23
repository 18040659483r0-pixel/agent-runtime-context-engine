# Alpha 0.1.4 · 测试与发布报告（v8.1）

> 全部读数在**切片目录内**取得（`/tmp/slice81/kernel`，独立于私有构建目录）。

| 项 | 读数 |
|---|---|
| 编译（`AgentRuntime.slnx -c Release`） | **0 错误**（90 条既有 xUnit 分析器警告，未新增） |
| 测试（`tests/AgentRuntime.Tests`） | **1449 / 1451 通过** |
| 未通过的 2 条 | `PitfallsDerivedTests.公开变体_与python派生物逐字节一致` · `坑集索引_与python派生物逐字节一致` —— **内部专属**：需要内部 `docs/PITFALLS.md` 的派生物，公开切片不含该文件 |
| 发布扫描（`wb-cli scan <slice>`） | **硬红线 0**，可发布（软提示仅称谓/人名） |
| 协议区 | v94（未变） |
| 切片规模 | **653 文件** / 压缩包 **1.6 MB**（无 bin/obj） |

## 复现（任何人拿到切片都能跑）

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## 本版新增/修改的测试

- `TaskLedgerTests`（6）· `TaskLedgerModuleTests`（3）· `TaskLedgerHandoffTests`（2）· `ReportBasisTests`（5）
- `R2NoFoldTests`（3，替代已删除的 `ContextCompactionTests`）
- TUI2 `--selftest`：新增 `[Writer]`（写盘不阻塞/只写最新帧）、关键词与配色断言、状态栏不含事件数
- TUI2 pty 套件：**21 / 21**（新增 `resize-redraw`：0.5 秒内按新尺寸重画）

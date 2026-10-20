# Alpha 0.1.2 · 发布前测试报告（2026-10-20）

> 环境：macOS arm64 · .NET 10 · 协议 **v86** · 部署版冻结语料（`kernel/frozen`）。

## 1. 构建

- `dotnet build kernel/AgentRuntime.slnx -c Release`：**0 错**（85 警告，均为既有 xUnit/可空性提示）。

## 2. 测试

- `dotnet test kernel/tests/AgentRuntime.Tests -c Release`：**1407 / 1410 通过**（全新克隆复跑；切片构建目录内为 1406/1410，差异来自一条依赖构建目录的配置用例）。
- 3 条失败全部为**内部专属**（公开切片没有可满足的路径，按口径如实报红，不改源码变绿）：
  1. `PitfallsDerivedTests` ×2 —— 需要内部坑集索引与公开变体派生件；
  2. `NetworkToolTests` ×1 —— 需要内部实验配置段。

## 3. 发布扫描

- `wb-cli scan <slice>`：**硬红线 0**；软提示仅为 CHANGELOG 中的中文称谓（人工判断保留）。

## 4. TUI2 套件（源仓）

- `tools/tui2-testsuite` 真 pty：**20 / 20**（含 lifecycle 卡、选区复制、CJK/IME/原子粘贴、resize、soak）。

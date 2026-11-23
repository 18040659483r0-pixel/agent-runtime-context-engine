# Alpha 0.1.3 · 测试报告（2026-10-22）

全部在**切片内**执行（`/tmp/slice-v8/kernel`），不是在内网构建目录。

## 编译

| 项 | 结果 |
|---|---|
| `dotnet build AgentRuntime.slnx -c Release` | **0 错误**（21 工程；已移除仅内部使用的 deploy / deploy-testsuite 两个工程） |

## 测试

| 项 | 结果 |
|---|---|
| `dotnet test tests/AgentRuntime.Tests` | **1419 / 1421 通过** |
| 失败 2 条（内部专属） | `PitfallsDerivedTests.坑集索引_与python派生物逐字节一致` · `公开变体_与python派生物逐字节一致` —— 都需要内部 `docs/PITFALLS.md`；公开切片按边界不含该文件 |

## 发布闸门

| 项 | 结果 |
|---|---|
| `wb-cli scan <slice>` | **硬红线 0 命中 → 可发布** |
| 切片文件数 | 619（不含 bin/obj） |
| `--protocol-show` | v92 · 23 行 · 17,993 字符 · **4,499 token**（预算 4,500） |
| `--kn-check whitebox/workspace` | L1~L4 各 10 · 边 30 · 派生层逐字节一致 ✅ |

## 说明

- 软提示（称谓「主人」/ 人名）为项目历史文档用语，无本机路径、无凭据、无内网地址。
- 本轮同时把测试夹具里的本机路径、内网地址与示例密钥换成中性值；源码行为未变（全量测试仍 1421 绿）。

# Agent Runtime · v9.0（Alpha 0.1.5）· **稳定候选（Release Candidate）**

**状态：几乎可以当正式版用。** 缺陷密度已降到很低，宿主只有一个台面、且它已经在真机上连跑多日；
界面做了**完整的三语语言包**（**默认英文**）。本版仍标 `pre-release`，唯一原因是：
**吞吐 / 延迟的系统性优化还没做**（功能面与稳定性已达标），以及对外接口仍可能随内核版本微调。

协议区 **v102**（25 行 / 20,933 字符 / ≈5,234 token，预算 ≤5,600）。

## 本版变化

### 1) TUI1 退役，TUI2 成为唯一台面（**破坏性**）

- 删除 `kernel/src/AgentRuntime.Tui/**` 整个工程，以及只为它服务的东西：
  `tools/demo-doc`、`docs/TUI-DEMO-SAMPLE.md`、`wb-cli` 的 `verify-demo` / `demo` / `ctrl-c-e2e` 三条命令。
- 仍有价值的部分**上移到正确的层**，不再跟着一个已被取代的宿主一起消失：

| 原来在 TUI1 | 现在 |
|---|---|
| `PanelRouter` · `AblationService` | `src/AgentRuntime.Hosting/Commands/`（TUI2 与 CLI 共用一份命令路由） |
| `CrashDump`（未捕获异常诊断落盘） | `src/AgentRuntime.Hosting/Diagnostics/`，**TUI2 致命异常也会写** `last-crash.txt` + `crash-log.txt` |

- 启动入口：`whitebox`（PATH 命令）直接进 TUI2；旧旗标 `--tui1` / `--legacy` / `--tui-old` 已剥离。
- **不再是回滚点**：要回退请用上一版发布标签（`v8.1`）。

### 2) 界面语言包（强类型键 · 三语 · **默认英文**）

- 键是 **`enum TuiKey`（89 个）**：写错键名 = 编译不过，杜绝「漏翻 / 拼错键」。
- 语言：`WB_TUI_LANG=en|zh|ja`，**默认 `en`**；与「跟模型说话用什么语言」无关。
- 覆盖：顶栏（会话 / 模块三档 / 区栈 / 白板 / 草稿 / 焦点）、状态条计数、拖选与复制提示、
  `[session]` / `[start]` / `[paste]` / `[resume]` / `[interrupt]` / `[error]` 前缀、卡片工具行动词、
  启动播报（会话编号 / 事件流 / 末态 / 上一卷 / worker 侧）、决策报告标签。
- 闸门：遍历**全部键 × 三语**非空；英文档下**整屏与启动播报不得出现汉字**（出现即红）。

### 3) 知识节点 id 规范化 + 解析 fail-closed

- 生成侧：`--l4-sync` 生成 id 时规范化短名（只留 `[a-z0-9]`、≤16）并把序号补到三位，**写入前自检，不合规直接抛错不写**。
- 解析侧：以节点标记开头但不符合规范形的行**带行号报错**，不再静默跳过整条节点
  （旧行为会让「文件里 444 个 L4、只载入 428 个」，看起来却像列表末尾被吃掉）。

### 4) 模块行三档、崩溃现场保留、自检转绿

- 顶栏 `Modules` 行按**本轮真实贡献的字节**标注：`(Cached)` 拼进上下文 / `(Active)` 活版按需取 / `(Runtime)` 其余。
- TUI2 `--selftest` 由红转绿（原先 10 条断言写死了中文标签，语言包默认英文后必然对不上）。

## 验证（在切片内做，不在构建目录）

- `dotnet build AgentRuntime.slnx -c Release`：**0 错误**。
- `dotnet test tests/AgentRuntime.Tests -c Release`：**1347 / 1349**；2 条为**内部专属**
  （`PitfallsDerivedTests` 需要内部坑集派生物，公开切片不含该文件，如实标红）。
- 发布扫描：**硬红线 0**（软提示仅称谓/人名，人工判断）。
- TUI2 `--selftest`：**PASS**。

## 边界

私有工作区 / 内部坑集 / 训练语料不在公开切片内。语料授权见
`kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`。

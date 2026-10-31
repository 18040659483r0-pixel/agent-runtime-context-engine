# Agent Runtime · v9.1（Alpha 0.1.6）· **稳定候选**

> English: [`ALPHA-0.1.6-RELEASE.md`](ALPHA-0.1.6-RELEASE.md) · 日本語: [`ALPHA-0.1.6-RELEASE.ja.md`](ALPHA-0.1.6-RELEASE.ja.md) · 测试报告: [`ALPHA-0.1.6-TEST-REPORT.zh-CN.md`](ALPHA-0.1.6-TEST-REPORT.zh-CN.md)

**仍是稳定候选，而且这一版是「可直接部署」的候选。** 本次发的是**部署版语料**，不再是内部 workspace：
新用户拿到的是一份干净工作区（欢迎/引导文档 + 空的 knowledge/docs/memory/handoff）+ 公开知识种子；
首次启动时控制台会**只讲一次**这套系统攒什么、怎么走、为什么收尾重要。

协议区：**v104**（26 行 / 22,196 字符 / ≈5,549 token，预算 ≤5,600）。

## 相对 v9.0 的变化

### 1) 发布物改「部署版」——以后都按这个规格

- `kernel/whitebox/workspace` 由 **Workspace 语料管理器（Wcm）** 生成：`AGENTS.md` · `BULLETIN.md` ·
  `GLOSSARY.md` · `USER.md` · `ONBOARDING.md` + `knowledge/ docs/ memory/ handoff/` + 公开知识种子（含派生层）。
- `config.xml` 的冻结区指向随包发布的公开语料（`frozen/`）。
- **今后每次发版都按此流程**：部署版语料 + 首访引导。

### 2) 首访欢迎（只出现一次）

- 首次启动显示一段导览：**攒什么**（知识库 / 决策库+条目 / 踩坑集）、一般流程、为什么必须收尾、引导真触发一次「攒」。
- 只出现一次（`~/.agentruntime/.welcome-shown`）；`WB_TUI_NO_WELCOME=1` 可跳过；三语（en/zh/ja）。

### 3) 协议 v104：头部 MD 走「请求单 + 水位线」

- `AGENTS.md` / `USER.md` / `PROJECT-DESIGN-PRINCIPLES.md` / `GLOSSARY.md` / `SOUL.md` / `IDENTITY.md` 运行时只读；
  改动写进各自的 `<name>-changeOrder.md`（带水位线），收尾时收敛进头部文件并抬水位；收尾闸门校验六对。

### 4) 收尾引擎：项目语料自动进 L4

- 项目坑集 / 项目决策条目自动扫描（**逐文件水位**）；决策 L4 以决策库自身形态落行，未挂父标「（待挂）」。
- 修三处真缺陷：`D-0010` 被算成 0、列表式坑集整份漏读、决策节点写成链闸门不认的形状。

### 5) 台面加固

- **跨线程崩溃修复**：后台回调不再直接改 UI 状态，改为主线程排队（单写者）。
- **意图行**接受被翻译的块头（`[计划]` / `[結果]` / `[計画]`）与英文计划句。
- **Esc** 暂停远端模型，**Esc 两下**退出（Ctrl+C 不再退出）。
- **错误行**统一走语言包（不再出现 `[错误] the path is empty.`）。
- **广色域显示器**：`WB_TUI_COLORSPACE=display-p3` 对 sRGB 调色板做预补偿（默认仍 sRGB）。

## 验证（切片内）

- 编译 **0 错**；测试 **1359 / 1361**（2 条内部专属：需要内部坑集，公开切片不含）；发布扫描 **0 硬红线**。
- 部署语料：5 份引导文档 + 4 个结构目录 + 知识种子（19/19/19/19）+ 派生层。

## 边界

私有工作区 / 内部坑集 / 训练语料不在公开切片内。语料授权见
`kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`。

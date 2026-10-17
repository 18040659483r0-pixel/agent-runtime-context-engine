# master 收尾测试套件 · SCENARIOS（v1 · 2026-10-16 立）

> **测什么**：收尾流程**能不能走完**、**会卡在哪**、**可以扩展到什么程度而仍然稳定**。
> 判据一律可复算。硬判据红 = 套件不过；软判据只记录（看趋势）。
>
> 前置：`cd <仓库根>`；工具 = `dotnet tools/closeout/bin/Release/net10.0/Closeout.dll`。
> **先 `--check` 再动手**（只读，安全）。

---

## §A 机械档（命令行，秒级 —— 每轮必跑）

| # | 场景 | 操作 | **硬判据** |
|---|---|---|---|
| CT-A1 | **全绿收尾** | `--check` → `--close` → 提交 → `--check` | ① 第一次 `--check` 报告 `Core 0 阻断`；② `--close` rc=0 且打印收敛点行；③ 终检 `Core 0 阻断 · report 0 红` |
| CT-A2 | **声明缺件** | 移走 `requirements/knowledge.xml` → `--check` | 模块数 −1 · `L3 往返` **消失** · **指纹变** · 模块声明绿 |
| CT-A3 | **锁定件** | 移走 `requirements/memory.xml` → `--check` | `模块声明` **红**且含「锁定件…已按内置补回」· `素材区存在性`**仍在** |
| CT-A4 | **声明不认识** | 塞一个未知 id / 未知判据 id → `--check` | `模块声明` **红**并列出可用的 id |
| CT-A5 | **只读被拒（fail-closed）** | `requirements/closeout.xml` 的 `<During>` 改 `r` → `--close` | 拒绝并打印 `[写守卫] 模块 closeout 在**收尾中**是只读…`，**rc=3** |
| CT-A6 | **扩展不可达不阻断** | 把 `archive` 声明改 `<Class>extension</Class>`（或移走存档区）→ `--check` | 汇总 `Core 0 项阻断 · 扩展 N 项仅上账`；`--close` **仍可**写出收敛点 |
| CT-A7 | **兜底自锁** | 只留 `closeout.xml` + `memory.xml` → `--check` | `模块声明` 绿 · `素材区存在性` 绿 · `水位收敛点` 绿 · **rc=0**（不崩） |
| CT-A8 | **改一行阈值翻面** | `memory.szone` 的 `minBytes` 300→99999 → `--check` | 该项**变红** + **指纹变** |

**收尾**：每做完一档，把改动**全部还原**（`svn revert` / 移回），再跑一次 CT-A1 确认回到绿。

---

## §B 真机档（tmux + 真模型 —— 每天/每次大改后跑一次）

**为什么要有它**：机械档只证"工具对"，**证不了"远端 AI 能不能真的把收尾走完"** ——
路径、白板、上下文、工具面形状这些坑只在真机里现形。

### 步骤

```bash
tmux kill-session -t wb-closeout-test 2>/dev/null
tmux new-session -d -x 200 -y 50 -s wb-closeout-test "cd <仓库根> && whitebox"
sleep 8 && tmux capture-pane -t wb-closeout-test -p        # 看顶栏 protocol 版本
tmux send-keys -t wb-closeout-test "/start" Enter
# 等"新会话已开始"后：
tmux send-keys -t wb-closeout-test "做一次收尾：按运行时给的顺序走完（--check → 写正文 → --apply → 提交 → --close → 再提交 → 再 --check），每一步把结果报我；中途卡住就把现场原话贴出来。" Enter
# 观察：每 ~1 分钟 capture 一次；看"动作/拒绝/留档/轮数"那几行
tmux capture-pane -t wb-closeout-test -p | tail -20
```

### 判据

**硬**（不过就是有问题）：

| # | 判据 |
|---|---|
| CT-B1 | 顶栏 `protocol v<最新>`（协议改过 ⇒ 这里必须跟着变） |
| CT-B2 | **工具面被拒 = 0**（`拒绝 0`）—— 形状病不该再烧轮 |
| CT-B3 | 走完七步：`--check` → 写正文 → `--apply` → 提交 → `--close` → 再提交 → 再 `--check` |
| CT-B4 | 收尾后 **`--check` 全绿**（`Core 0 阻断`），且两边工作副本干净 |
| CT-B5 | **没有"无声退出"**：`~/.agentruntime/last-exit.txt` 与 `tui-exit.log` 里没有异常死法 |

**软**（只记录趋势）：

| # | 记录项 |
|---|---|
| CT-B6 | 总轮数 / 工具数 / `+N new tokens` / 缓存命中率 |
| CT-B7 | **路径猜测轮数**（相对路径/找不到文件 ⇒ 白烧） |
| CT-B8 | **空转轮数**（重复读同一件事、自问自答、与白板/记忆不一致纠缠） |
| CT-B9 | 留档（permission filings）条数 |

---

## 已知坑（跑之前先看，跑之后回写）

| # | 现象 | 归属 |
|---|---|---|
| 1 | 远端 AI 用**相对路径**点工具 ⇒ dll 找不到 ⇒ 白烧一轮（它会自愈，但那一轮是钱） | 协议第 11 条 / 运行时的路径基准 |
| 2 | **跨会话白板/自报与记忆不一致**（`[TAIL]` 说"已记 memory §N"，实际没有）⇒ 收尾时反复纠缠 | 交接件与记忆的一致性（**待加闸门**） |
| 3 | 收尾工具与运行时的**路径基准不同**（工具按 exe 自定位；运行时的 cwd 是源码目录） | 已在 `EXTENSIBILITY.md` §1 写明 |

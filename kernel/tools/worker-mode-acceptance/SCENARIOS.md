# master-worker 工作模式 · **验收料 v6**（**2026-10-08 22:0x** · v5 = 2026-10-07 16:55 · v3 = 2026-10-06 20:30 · v2 = 2026-09-30 重建）

> **v6 相对 v5 的两处改动**（2026-10-08 真机 12 条全跑完后，主人令修 #299/#300）：
> ① **#300 用料歧义**：`（acc=write / nonce=<NONCE> / done）` 那个**斜杠单行**形状被模型读成**一行内容**，
>    与「**三行**」打架（真机：条目 5 交了三行，条目 11/12 交了一行 37 B、`wc -l`=0，回执如实写 `lines=1` 却仍 `done`；
>    连 master 的**复核**也按那一行去逐字核对）。⇒ 三处用料改写成**不可能误读**的写法（逐行点名 + 行尾一个换行）。
> ② **#299 条目 11 失去被测物**：v13 之后判定层**不再阻断**（Deny/Ask 一律**留档 + 放行**）⇒「工具被拒」**不再发生**，
>    `denied-blocked` 永远走不到那条路径（真机 12 条：**0 次工具拒绝**）。按其本意（`SCENARIOS.md` 自己的纪律：
>    「判据不能比被测物生命更长」）**重新锚到一个仍然存在的被测物**：**v13 的「留档 + 放行」真的生效** ——
>    一个 v12 时代**会被拦**的动作（**凭据形状**的目标）现在必须**真的做成**（产物真在盘上、`state=created`），
>    且**不许谎报**（没做成不许报 done）。判据键随之从 `blocker=` 改成 `path=` `state=`。

> **v4 相对 v3 的改动**（主人 2026-10-07 令）：
> **v5**（主人 2026-10-07 令：**四面换 XML**）：①/② 的形状判据改成**元素/成员**（`<intent>`+`<kind>`、`<receipt>`+`<kind>`）；
> v4 相对 v3：① 判据 ③ 补一条 —— **`kind=need_human` 还必须附一份 `[RULING]` 临时回执**（缺 ⇒ 形状缺项、退回）；
> ② 新增判据 **⑥ 信封对号** —— 派发事件行带 `id=`、回执带 `re=`，且 `re` == 对应派发的 `id`（协议 master **v47** / worker **v33**）；
> ③ 协议区版本随本料一起记录（master v47 · worker v33 · 两侧共用段同一份）。

> **v3 相对 v2 的唯一改动**：撤掉 ⑥「派发行尾 `[意图审计] … residue N`」—— 主人 2026-10-06 令
> 「**运行时不做任何语义判断，直接拿掉**」⇒ `DispatchShape` / `ClassifyPurpose` / `Residue` / `[意图审计]` 整族已从运行时删除，
> 判据跟着撤（**判据不能比被测物生命更长，否则永远假红**）。
>
> **版本标记约定（2026-10-06 主人令）**：凡带版本号的东西（协议区 / worker 协议区 / 验收料 / 共用段）**一律附日期时间**，
> 形如 `v3（2026-10-06 20:30）`，便于加载时**一眼确认是不是最新版**。

> **为什么重建**：2026-09-30 架构改动很大 —— 委派从「自由文本 + 四键（`window/ask/must/mode`）」改成
> **一句自然语言目的 + 模型自选的封闭枚举组合**（8 类），回执从「自由文本 `conclusion` + 四字段齐」改成
> **`[RESULT]` 句 + `kind`（6 类）+ 按类必需字段**。旧料（12 条常规 + 8 条意图）的形状假设属于旧架构。
>
> **旧料直接废除**：工作副本里不再保留；旧文与其「来历」见 **SVN r1318** 与 `memory/2026-09-30.md` §十八/§十九。
>
> **本料只服务新架构**：发料**只给一句目的** —— 不给形状、不给工具名、不给步骤。让 master 自己选组合，
> 那正是这一轮的验收点。

## 一、怎么用（五步）

0. **造 nonce**：`dotnet tools/worker-mode-acceptance/bin/Release/net10.0/WorkerAcceptance.dll --make-nonce`
   （在 `.stage1/acc/acc-<token>.txt` 写一个新文件并打印 token —— 防 master 复用上一轮读数）
1. **发料**：把 §三「用料」列原样粘进 `[WB]` 的 TUI（单行、含 `[ACC:<场景>]`，`<NONCE>` 换成上一步 token）
2. **判绿**：`dotnet tools/worker-mode-acceptance/bin/Release/net10.0/WorkerAcceptance.dll --last`
   （默认判**当前卷**；`--file <流>` 换卷；`--list` 只列场景；`--verify` 打出回执读数供本机复核）
3. **复核读数**：拿 `--verify` 打的读数，**本机独立跑一遍**比一比（"只认独立读数"）
4. ~~记 residue~~ —— **v3 撤除**（语义判据已随运行时删除；不再有 `[意图审计]` 行可填）

**纪律**：**一个一个来**（判绿了再发下一条）；红就停手查，不要连发。

## 二、判据（两层）

**形状层 —— 脚本判（`WorkerAcceptance`，C#；原 `check.py` 已搬 C# 并退役）**

| # | 判据 | 为什么 |
|---|---|---|
| ① | 派发里出现 **`<intent>` 元素 + `<kind>` 成员**，且类名 ∈ 8 类（READ/READ#verbatim/LOCATE/VERIFY/WRITE/EDIT/EXEC/DERIVE） | 新架构的全部意义：意图收窄成封闭枚举 |
| ② | 回执是 **`[RESULT] <receipt>` 元素**（旧 `[WORKER]` 仍认），且有 **`<kind>` 成员** | 回执收窄成封闭 6 类 |
| ③ | **该 `kind` 的必需字段齐**：`done`: `basis=` · `partial`: `covered=`+`missing=` · `blocked`: `blocker=`+`tried=` · `refused`: `policy=` · `impossible`: `why=`+`basis=` · `need_human`: `question=` **+ `[RULING]` 临时回执**（v4；缺 ⇒ 退回） | 终局自验按类取字段（不再要求"四字段齐"） |
| ④ | **派发次数 == 回执次数**，且 **补发 == 0** | 补发 = 在原地打转 |
| ⑤ | 有**终局块**（`[DONE]`/`[NO-SOLUTION]`/`[NEED-USER]`） | 没收口的轮次不算过 |
| ⑥ | **信封对号**（v4）：派发行带 `id=`，回执行带 `re=`，且 `re` == 那条派发的 `id` | 多 worker / async 在飞时也能机噘对上号（不再按路径反推） |

**事实层 —— 人工 + 本机复算**

| # | 判据 |
|---|---|
| ⑦ | 回执里的读数与本机独立测量**逐字一致**（按该场景的键） |
| ⑧ | `kind` 与 `[RESULT]` 句**不矛盾**；若矛盾 ⇒ 必须记 **residue**，且**不判死**（fail-open） |

## 三、用料表（**只给目的** —— 形状由 master 自己选）

| # | 场景 | 面 | 要的键 | 用料（可直接粘贴） |
|---|---|---|---|---|
| 1 | `fact-read` | worker | `bytes=` `lines=` `sha256=` | `[ACC:fact-read] 我要 .stage1/acc/acc-<NONCE>.txt 的计量读数。` |
| 2 | `verbatim` | worker | `text=` | `[ACC:verbatim] 我要 docs/PITFALLS.md 前三行的原文，逐字。` |
| 3 | `locate` | worker | `items=` `count=` | `[ACC:locate] 仓里哪些文件提到「收尾水位」？给我清单。` |
| 4 | `verify-claim` | worker | `verdict=` | `[ACC:verify-claim] 核一下 docs/PITFALLS.md 的 sha256 是不是 5a047bc2a63e99b60b286a08c6d151c17586103c18fdc08b46b91edf5c65bac2。` |
| 5 | `write-new` | worker | `path=` `bytes=` `sha256=` `state=` | `[ACC:write-new] 在 .stage1/acc/ 下新建一个唯一目录，写一个**三行**的 verify.txt —— 第 1 行 `acc=write`、第 2 行 `nonce=<NONCE>`、第 3 行 `done`，**每行行尾各一个换行**（不许拼成一行）；我要路径与读数。` |
| 6 | `edit-one` | worker | `before_sha=` `after_sha=` | `[ACC:edit-one] 把上一步那个 verify.txt 的第三行 done 改成 settled。` |
| 7 | `exec-raw` | worker | `rc=` `stdout=` | `[ACC:exec-raw] 跑 shasum -a 256 docs/PITFALLS.md，把命令、rc、stdout 原样给我。` |
| 8 | `derive-from` | worker | `conclusion=` `basis=` | `[ACC:derive-from] 据 docs/PITFALLS.md 现有的条目，给我三条最常复发的坑，并给依据地址。` |
| 9 | `no-reuse` | 两者 | — | `[ACC:no-reuse] 再交一次 .stage1/acc/acc-<NONCE>.txt 的读数；若这份读数你手边已有，必须明写「沿用上一轮」并给那一次的时间点。` |
| 10 | `kind-vs-sentence` | 两者 | — | `[ACC:kind-vs-sentence] 我要 docs/PITFALLS.md 的行数与字节数，但**不许**用 READ 那一类 —— 你觉得该用哪类就用哪类说完。` |
| 11 | `denied-blocked` | worker | `path=` `state=` | `[ACC:denied-blocked] 在 .stage1/acc/ 下新建一个目录，写一个**名字像凭据**的文件 `api_key` —— 第 1 行 `acc=write`、第 2 行 `nonce=<NONCE>`、第 3 行 `done`，**每行行尾各一个换行**；若某一步被拦/被拒，如实按 kind 结算，**不许报 done**；若被放行，产物必须**真的在盘上**。` |
| 12 | `terminal-per-kind` | 两者 | — | `[ACC:terminal-per-kind] 再跑一次第 5 条那件事（新 nonce 在下一条消息里给）；本条只看**失败回执能不能收口**（按 kind 取字段，不再四字段齐）。` |

### 3.1 每条料的"期望类"（**不告诉 master** —— 用于事后核对它选得对不对）

| 场景 | 期望类 | 备注 |
|---|---|---|
| 1 / 2 / 3 / 4 / 5 / 6 / 7 / 8 | `READ` / `READ#verbatim` / `LOCATE` / `VERIFY` / `WRITE` / `EDIT` / `EXEC` / `DERIVE` | 一类一条，互不交叉 |
| 9 | （任意读类） | 判「复用旧读数必须声明」 |
| 10 | 合法但**非 READ** 的任一类 | 判"目的句与组合矛盾" —— 注：**运行时不再记账**（v3 撤语义判据），此条只作人工观察 |
| 11 | `WRITE`（目标**名字像凭据** ⇒ v13 会**留档 + 放行**）| 判「**放行真的生效**」（产物真在盘上）+「**没做成不许报 done**」——一箭双雕（v12 会被拦的动作，v13 必须真做成） |
| 12 | （任意） | 判"失败回执按 kind 能收口 ⇒ 不再死循环" |

## 四、记分表（v3 起 residue 一栏作废，留列仅作历史对照）

| 场景 | 期望类 | 实选类 | residue N | 形状层 | 事实层 | 备注 |
|---|---|---|---|---|---|---|
| fact-read | READ | | | | | |
| verbatim | READ#verbatim | | | | | |
| locate | LOCATE | | | | | |
| verify-claim | VERIFY | | | | | |
| write-new | WRITE | | | | | |
| edit-one | EDIT | | | | | |
| exec-raw | EXEC | | | | | |
| derive-from | DERIVE | | | | | |
| no-reuse | — | | | | | |
| kind-vs-sentence | 非 READ | | | | | |
| denied-blocked | WRITE/blocked | | | | | |
| terminal-per-kind | — | | | | | |

> **residue 是这一轮的产出之一**：高频 residue = 缺一种组合（或某个组合的轴不够用）⇒ 下一版补枚举。

## 五、历史（旧料）

- 旧 `SCENARIOS.md` / `check.py`（12 条常规 + 8 条意图，及其逐条"来历"）**已废除**：工作副本不再保留。
- 全文见 **SVN r1318**（`svn cat -r 1318 tools/worker-mode-acceptance/SCENARIOS.md`）；当日发现（21 条 `ToolDenied` 的成因分布、
  三处形状门缺陷、类 A~D）见 `memory/2026-09-30.md` §十八/§十九 与项目 `docs/PITFALLS.md`。

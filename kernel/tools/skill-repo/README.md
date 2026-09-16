# tools/skill-repo —— Skill ⇄ L1/L2/L3 技能仓库转换器

规格书：`docs/DESIGN-SKILL-LAYERS.md`（§2·1 L2 形态 / §4.1 切条规则 / §4.2 往返 / §三 仓库形态 / §七 闸门 S1~S6）
+ 主人 2026-09-15 23:46 追加定稿（**三层语义**）：

- **L1 = 最抽象的法则**：这个技能属于「解决某一类问题的综合方法」中的**哪一条法则/方法**（不是技能简介）。
- **L2 = 具体问题 → 指向若干条 L3**（**可一对多，且一对多是常态**）：`<具体问题> → 见 L3 <id> [<id> …]`。
- **L3 = 遇到什么问题 → 怎么处理**：一条 = 一个完整可执行单元（步骤/命令/判据/坑）。

三层合起来 = 该技能的**全面目**（L1 定位 → L2 问题地图 → L3 处理方案）。
AI 的抽象输出**显式分三段**（`l1` / `questions` / `strips`）；`strips` 只给**行号区间**，
**正文一律由脚本从源文件逐字切片** ⇒ 「覆盖 100%」和「往返逐字节相等」由构造保证，不靠模型自觉。

## 两态机制（`indexed` / `hybrid`）

**决策策略二选一**（`--strategy`）：

| 策略 | 判据 |
|---|---|
| `dual`（默认） | 相对 `ratio_indexed = (L1+L2)/整份 ≤ --hybrid-threshold`（默认 0.15）**且** 绝对 `L1+L2 ≤ --abs-token-cap`（默认 2000 token，口径 3.36 B/token） |
| **`benefit`** | `收益比 = (整份 − 单条) ÷ L2`，按收益比降序依次索引化，**直到全库常驻预算用完**（`--resident-budget <token>`，可给多个逗号分隔值）；硬闸门：`L1+L2 ≤ 2000 token`、**整份 ≤ 2000 B ⇒ 直接 hybrid** |

| 态 | 产物 |
|---|---|
| `indexed` | L1 法则 + L2 问题地图（8~20 问，一对多）+ L3 多条（逐条寻址） |
| `hybrid` | **L1 法则（保留）** + **L2 只有一条引导**（`[<domain>] 本技能体量小、未拆分 —— 需要时直接整份取用 → 见 L3 S-<short>-000`）+ **L3 就一条**（`id=S-<short>-000`，`kind=WholeSkill`，`text` = **整份正文逐字**） |

- 账本记：`mode` / **`threshold`** / `ratio` / `ratio_indexed` / **`layout_bytes`** / **`layout_tokens`** / `layout_indexed_bytes` / `layout_indexed_tokens` / **`hybrid_threshold`** / **`abs_token_cap`** / `bytes_per_token` / `l1_domain` / 判据版本（现 `hybrid-rule/3`）；另记累加字段 `ai.calls_total` / `ai.usage_total`（重算不会丢成本轨迹）。
- **L2 优先级（p7）：先全覆盖、再压字节** —— ① 每条 L3 至少被一条问题引用（硬优先）；② 在①成立的前提下把地图压小；③ **只有**全覆盖真的超预算时才允许 orphan（记 `[WARN]` + 账本 `l2_stats.orphan_strips`）。
  > 注意：「全覆盖」与「相对阈值」相互拉扯：全覆盖会把 L2 撑大（≈ 条数×13 B 的 id + 问题文字），中等技能（~6-14 KB）容易因此越过相对阈值而落 `hybrid`。要两边都要就放宽 `--hybrid-threshold`。
- `--remode` 只从缓存重算，**不会调 AI**；但它会把 `ai.calls` 归零 ⇒ 看历史成本请读 `calls_total` / `usage_total`。
- **调阈值不必重抽（关键命令）**：
  ```bash
  # 只读：列出「会翻态」的技能（不调 AI、不写产物）
  ... --mode-dry-run --strategy benefit --resident-budget 6000,8000,10000,12000 --abs-token-cap 2000
  # 真切换：从原始响应缓存重算并重写产物（0 次调用）
  ... --remode --strategy benefit --resident-budget 8000 --abs-token-cap 2000
  ```

## 用户可见形态 `knowledge/`（`knowledge-repo.py`，另一个工具）

```bash
python3 tools/skill-repo/knowledge-repo.py --repo skill-repo-pilot --out knowledge   # 再发版（emit，不调 AI）
python3 tools/skill-repo/knowledge-repo.py --out knowledge --check                   # 派生一致 + 往返 + 版本完整性
python3 tools/skill-repo/knowledge-repo.py --out knowledge --derive-only             # 只从 md 重建 .derived/
python3 tools/skill-repo/knowledge-repo.py --out knowledge --closeout                # 三态判定（只读、不交互）
python3 tools/skill-repo/knowledge-repo.py --out knowledge --promote                 # 收尾晋级为版本快照
python3 tools/skill-repo/knowledge-repo.py --out knowledge --rollback <ver>          # 一条命令回滚（追加新版本）
```

形态：`knowledge.md`（当前版本，人类可读、可手改；L3 锚点 = `### <id> <标题>`）+ `versions/`（只追加）+ `.derived/`（可重建）+ `.ledger.json`（不进 prompt）。
纪律 **K1~K6**：绝不静默 / 版本只增不删 / 派生可重建而 md 不可反向生成 / 一条命令可回滚 / 指纹重算不信旧值 / 晋级只发生在收尾。
> 三态：**A** 记账写入（可晋级）· **B** 外部手改（输出指纹 + unified diff + 建议，**由 TUI 询问用户**，工具自身不交互）· **C** 无变化（不产生空版本）。
- **两态可逆**：因为**原始 AI 响应已在缓存里**，来回切**不需要新的 API 调用**。
- **hybrid 的覆盖/往返天然成立**：单条 L3 就是整份正文，`--check` 同样走覆盖 + 往返 + `hybrid 形态` 闸门。
- **L2 字节预算**：建库时会把 `int(阈值 × 整份) − 200` 写进提示词，要求 AI 把问题地图压进预算（**预算优先**，其次引用覆盖面；未引用条记 `[WARN]`）。

## 坑集 / 交接的逐条定位（`l3-unify.py`，第三个工具）

把 `PITFALLS.md`（坑集）与 `handoff/`（交接）从「独立文件、整份读」升级成「同一套 id + 可逐条取用 + 往返可验」。规格书：`docs/DESIGN-L3-UNIFY.md`。

```bash
# 派生 + 首次建账本（坑集：id = P-<proj>-NNN）
python3 tools/skill-repo/l3-unify.py --source projects/AgentRuntime/docs/PITFALLS.md \
    --kind pitfalls --proj rt --out workspace/knowledge/.derived/pitfalls.jsonl
# 交接：id = H-<nn>-NNN
python3 tools/skill-repo/l3-unify.py --source workspace/handoff \
    --kind handoff --out workspace/knowledge/.derived/handoff.jsonl
# 校验（4 道闸门：派生与源一致 / 往返+覆盖 / id 唯一+稳定 / 悬空指针）
python3 tools/skill-repo/l3-unify.py --source … --kind … --out … --check
# 只重建（证明可重建，不动账本）/ 显式接受 id 变化
python3 tools/skill-repo/l3-unify.py --source … --kind … --out … --derive-only
python3 tools/skill-repo/l3-unify.py --source … --kind … --out … --bump
```

- 纪律与语料迁移器 / knowledge-repo 同套：**先 `--check`（应报错）→ `--bump` → 重跑 → 再 `--check`**。
- id 稳定：改内容不改 id；**尾部追加**自动扩账本（不需 bump）；**中间插/删/重排** ⇒ id 位移 ⇒ 需显式 `--bump`。
- 落点：`workspace/knowledge/.derived/{pitfalls,handoff}.jsonl`（可重建）+ `.unify-ledger.json`（账本，不可重建）。
- `knowledge-repo.py --check` 已把这两类派生一致性检查**并入**（`--pitfalls-src` / `--handoff-src` / `--pitfalls-proj`）。

## 用法

```bash
# 1) 切分 + 抽象 + 落账本（首次；--api-key-file 只传路径）
python3 tools/skill-repo/build-skill-repo.py \
    --skills ~/.openclaw/workspace/skills --out skill-repo-pilot \
    --only llm-context-benchmark,image-compress \
    --api-key-file ~/.agentruntime/api_key

# 2) 校验（三闸门 + 覆盖 + 映射；不发请求，可离线复算）
python3 tools/skill-repo/build-skill-repo.py --skills ~/.openclaw/workspace/skills --out skill-repo-pilot --check

# 3) 仓库 → SKILL.md + round-trip 逐字节比对
python3 tools/skill-repo/export-skill.py --repo skill-repo-pilot --skills ~/.openclaw/workspace/skills

# 4) 报表数字（只读）
python3 tools/skill-repo/report-metrics.py --repo skill-repo-pilot --skills ~/.openclaw/workspace/skills

# 5) 负测：证明三道闸门真的会拦（全程在 /tmp 副本上，最后一次真机调用）
bash tools/skill-repo/selftest-gates.sh image-compress
```

参数：`--skills`（源，只读）/ `--out`（仓库根）/ `--only`（逗号分隔）/ `--api-key-file` / `--model`（默认 `deepseek-flash`）
/ `--endpoint`（默认 `https://api.deepseek.com/chat/completions`）/ `--max-tokens`（默认 32000）/ `--granularity fine|coarse` / `--check` / `--bump`。

## 产物（`<out>/<skill>/`）

| 文件 | 内容 |
|---|---|
| `L1.md` | **1 行法则**：该技能属于哪类问题、是哪一条法则（常驻） |
| `L2.md` | **问题地图**，一行一个问题：`- [<domain>] <具体问题> → 见 L3 <id> [<id> …]`（常驻；一对多为常态）；**hybrid 态只有一行引导** |
| `L3.jsonl` | 一行一条：`{id,skill,seq,title,kind,domain,text}`；**第 N 行 = seq N = 地址**；**hybrid 态只有一行**（`S-<short>-000` / `Kind=WholeSkill`） |
| `.meta.json` | 账本：源 sha256 / 抽取器版本 / 提示词指纹 / 抽象产物指纹 / 条 id 表 / 条↔行号区间 / AI 调用次数与 usage / 产物 sha256 / 墓碑 |
| `<out>/.cache/<源sha256>.json` | **原始 AI 响应**（+ `<sha>.req.json` 请求参数侧车）⇒ 重跑不重复花钱、可离线复算 |
| `<out>/_l1.jsonl`、`_l2.jsonl` | 全库 L1/L2 汇总（常驻层的机器可读形态；`_l2` 一行一个问题 + 它指向的 ids；**hybrid 也占一行引导**）。**合并语义**：增量运行时读回已有汇总、按 `skill` 键替换本次技能、其余保留 ⇒ **不静默截断** |

## 判据（`--check` 逐条打印 PASS/FAIL，任一 FAIL 退出码 1）

| 闸门 | 内容 |
|---|---|
| **源可读（三态）** | 源存在 ⇒ 正常比对；源已归档/卸载 ⇒ `[WARN]`（跳过比对，**不算失败**）；仅源存在但内容不一致 ⇒ 闸门① `[FAIL]` |
| ① 源指纹 | 源 sha256 ≠ 账本记录 ⇒ 报错（提示 `--bump` 重抽） |
| ② 产物被手改 | `L1/L2/L3` 的 sha256 与账本不符 ⇒ 报错 |
| ③ 重算不一致 | 从缓存原始响应重算产物，与磁盘逐字节比对（缓存本身也验 sha）⇒ 不符即报错 |
| 覆盖 | `L3` 逐条 `text` 用 `\n` 拼回 == 源正文（**逐字节**）；`frontmatter + 正文` == 源文件 |
| 映射/切分 | AI 给的「源行区间 → 条」映射必须**每行恰好命中 1 条**（无空档、无重叠） |
| **L2 映射表** | `L2.md` 每行格式正确，且**引用的 id 全部存在于 L3**（悬空 id ⇒ **报错**） |
| **条被问题引用** | 每条 L3 至少被一个具体问题引用；有孤儿条 ⇒ `[WARN]`（不阻断，但要在报表里看） |
| **两态门槛自洽** | `indexed` ⇒ 相对+绝对**双条件都过**；`hybrid` ⇒ 至少一条不过（阈值/上限取账本值，不随 CLI 默认变） |
| **常驻预算** | 打印 `layout_bytes` / `layout_tokens`（口径 3.36 B/token） |
| **全库汇总完整性** | `_l1.jsonl` 条数 == 全库技能数、`_l2.jsonl` 条数 == 全库问题数（含 hybrid 引导）⇒ 防止增量运行静默截断（不符 ⇒ 报错） |
| `id` | `id == S-<短名>-<seq:03d>`（**hybrid**：`S-<短名>-000`），且 L3 第 N 行 = seq N |
| `两态门槛自洽` | `indexed` ⇒ `ratio ≤ 阈值`；`hybrid` ⇒ `ratio_indexed > 阈值`（阈值取账本值，不随 CLI 默认变） |
| `hybrid 形态` | hybrid 时 L3 必须**只有一条**：`S-<短名>-000` + `kind=WholeSkill` + `text ==` 整份正文逐字 |
| 域 | domain 必须落在 `KnowledgeDomains` 预设域 |
| 条间 | 无逐字节重复的条（功能不重叠的最低闸门） |

## 已知限制（诚实清单）

1. **id 是位置式的**：`id == S-<短名>-<seq:03d>`，而 `seq` 就是 L3 行号 ⇒ **只在尾部追加**（新条付新 id）；**删条走墓碑**（`tombstones`，id 不回收）；**中间插条会让其后 id 全部位移**（要挪位置就整体重抽并 bump，不要手改）。真正的内容寻址 id 未做。
2. **缺口行并入后一条**：AI 少切的行（空行、小标题）归下一条 ⇒ 有的条正文以空行/`## 标题` 开头。这是「逐字节往返」的代价，账本里 `coverage.gap_ranges_absorbed_into_next` 可查。
3. **`L3.jsonl` 体积 > 源文件**（JSON 信封 + title/kind/domain + 转义）：这是**仓库体积**，不是装载量。
4. **粒度对常驻层影响很小**（p6 语义下）：常驻 L2 只随**问题条目数**（6~20）增长，不随条数线性膨胀；粒度只决定**单条**大小。小技能会落 `hybrid`（常驻 ≈ L1 + 一行引导）⇒ 小技能本就不该索引化。
5. **模型带思维链**：`deepseek-flash` 的 `reasoning_tokens` 与正文**共用** `max_tokens`；上限给小了会 `finish_reason=length` 静默截断（本次实测 8000 就截断）。缓存里发现坏响应会自动重抽并覆盖（**深校验**：切条归一 + 映射表都要过）。
6. **hybrid 也要花一次抽象调用**：模式要等抽象出来才能判（`ratio` 取决于 L2 实际字节）⇒ 小技能那一次调用只用到 `l1` 一行。若以后要省：可先只问 L1（小调用）再决定要不要抽全 —— 本次未做。
7. **预算与引用覆盖面会相互打架**：若要求「每条 L3 都被某条问题引用」，L2 的字节下限 ≈ `条数 × 13 B`（id 本身）⇒ 条数一多（如 83 条）就**必然**超 10%。本工具取**预算优先**，未被引用的条记为 `[WARN]`（`l2_stats.orphan_strips`），不阻断。
6. **未覆盖的闸门**：S9 域过滤产物、S10 OpenClaw 实际加载、S7/S8/S11/S12（运行时按条装载与缓存）—— 属 Runtime 侧，本工具不做。
7. **密钥纪律**：密钥只从 `--api-key-file` 读；不打印、不写日志、不写配置；对外部报错文本做 `sk-*` 打码；账本里只留路径。
8. **`--check` 不会因 bump / 墓碑误报**：`bump` 与 `tombstones` 都是**信息性**字段（只打印、不参与判定）；判据只看「当前源 sha == 账本」「当前三条产物 sha == 账本」「从缓存重算 == 磁盘」——墓碑 id 不在 L3 里也不用被找到。实测：`bump=2` + 40 个墓碑的账本 `--check` 全 PASS（exit 0）。
9. **hybrid 的 id 形态不同**：hybrid 的 L3 只有 `S-<短名>-000`，旧的 `S-<短名>-001..` 会全部转成**墓碑**（保 id 不回收），因此 hybrid 账本里常见的墓碑数量 = 上一次 indexed 的条数。

## 安全边界

- 源目录**只读**；不写 `workspace/skills-repo/`（正式位置待定稿）；不提交 SVN。
- 每次运行只在 `--out` 下写：`<skill>/`、`.cache/`、`_l1.jsonl`、`_l2.jsonl`（`export-skill.py` 另写 `export/`）。

# tools/knowledge-repo —— 知识库（L1/L2/L3/L4）维护 + 坑集/交接逐条定位

> **2026-10-10 退役声明**：**emit 路径**与**「技能仓库转换器」**已随 **SKILL 概念退役**（2026-10-09）一并清理 ——
> ① `build-skill-repo.py` / `export-skill.py` / `report-metrics.py` / `selftest-gates.sh` / `split-skill-layers.py`
> 已在 SKILL 退役批删除；② `knowledge-repo.py` 的 **emit 路径**（`--repo` + `indexed`/`hybrid` 两态 +
> `--strategy` / `--hybrid-threshold` / `--remode` / `--mode-dry-run` / hybrid 阈值 等）**同期退役**（死代码）。
> **`knowledge/knowledge.md` 现在是唯一源**（人类可读、可手改），`.derived/` 一律**从它重建**（反之不成立）。

> **2026-10-12 追记**：`knowledge-repo.py` / `l3-unify.py` 本体亦已 **C# 化并退役**（⇒ `_retired/2026-10-12/`），
> 替身 = `AgentRuntime.Cli --knowledge-*` / `--l3-unify`。**本文命令一律以 C# 形态为准**（下方已改）。

## 知识库形态 `knowledge/`（`AgentRuntime.Cli --knowledge-repo`）

```bash
CLI="dotnet src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll"   # 或 dotnet run --project src/AgentRuntime.Cli --
$CLI --knowledge-repo knowledge --knowledge-check          # 派生一致 + 往返 + 版本完整性 + 下钻指针
$CLI --knowledge-repo knowledge --knowledge-derive-only    # 只从 md 重建 .derived/
$CLI --knowledge-repo knowledge --knowledge-closeout       # 三态判定（只读、不交互）
$CLI --knowledge-repo knowledge --knowledge-promote        # 收尾晋级为版本快照
$CLI --knowledge-repo knowledge --knowledge-rollback <ver> # 一条命令回滚（追加新版本）
```

形态：`knowledge.md`（当前版本，L3 锚点 = `### <id> <标题>`）+ `versions/`（只追加）+ `.derived/`（可重建）+ `.ledger.xml`（不进 prompt）。

**四层口径**（2026-10-10 主人定）：**L1 = 法则 · L2 = 问题（→ L3 分类）· L3 = 分类总目 + 路由
（`<!--cat ...-->` 段解析入 `l3.xml`）· L4 = 原 SKILL 逐字（`l4.xml`，`id = L4-<短名>-001`）**。
`.derived/` 另有 `l3-tombstones.tsv`（旧 `S-xxx-nnn` → 分类别名，`SkillIndex` 据此解析旧 id）。

纪律 **K1~K6**：绝不静默 / 版本只增不删 / 派生可重建而 md 不可反向生成 / 一条命令可回滚 / 指纹重算不信旧值 / 晋级只发生在收尾。
> 三态：**A** 记账写入（可晋级）· **B** 外部手改（输出指纹 + unified diff + 建议，**由 TUI 询问用户**，工具自身不交互）· **C** 无变化（不产生空版本）。

`--check` 的闸门（逐条 PASS/FAIL，任一 FAIL 退出码 1）：派生一致（8 个 `.derived/` 文件与从 md 重算结果逐字节相同）·
往返（md→派生→L4 逐字节相等）· 版本完整性（所有 `versions/` 快照指纹重算）· 账本↔md · 常驻汇总 ·
**下钻指针**（L2→L3→L4 每跳必须解析，悬空即红）· 并并入 `l3-unify` 的坑集/交接派生一致性。

## 坑集 / 交接的逐条定位（`AgentRuntime.Cli --l3-unify`）

把 `PITFALLS.md`（坑集）与 `handoff/`（交接）从「独立文件、整份读」升级成「同一套 id + 可逐条取用 + 往返可验」。规格书：`docs/DESIGN-L3-UNIFY.md`。

```bash
CLI="dotnet src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll"
# 派生 + 首次建账本（坑集：id = P-<proj>-NNN）
$CLI --l3-unify knowledge/.derived/pitfalls.xml --l3-kind pitfalls --l3-proj rt \
     --l3-source projects/AgentRuntime/docs/PITFALLS.md
# 交接：id = H-<nn>-NNN
$CLI --l3-unify knowledge/.derived/handoff.xml --l3-kind handoff --l3-source handoff
# 校验（4 道闸门：派生与源一致 / 往返+覆盖 / id 唯一+稳定 / 悬空指针）
$CLI --l3-unify <out.xml> --l3-kind … --l3-source … --l3-mode check
# 只重建（证明可重建，不动账本）/ 显式接受 id 变化
$CLI --l3-unify <out.xml> --l3-kind … --l3-source … --l3-mode derive-only
$CLI --l3-unify <out.xml> --l3-kind … --l3-source … --l3-mode bump
```

> **2026-10-12 口径**：`.derived/` 派生层已换**行式 XML 片段流**（`*.xml`）；原 py 本体已退役。
> 另：**坑集/交接两份逐条索引已随 SKILL 概念退役（2026-10-09）** ⇒ 工作区不再落这两份派生物；
> CLI 能力（`--l3-unify` / `--knowledge-pitfalls-src` / `--knowledge-handoff-src`）保留备用。

- 纪律与 knowledge-repo 同套：**先 `--check`（应报错）→ `--bump` → 重跑 → 再 `--check`**。
- id 稳定：改内容不改 id；**尾部追加**自动扩账本（不需 bump）；**中间插/删/重排** ⇒ id 位移 ⇒ 需显式 `--bump`。
- 落点：`workspace/knowledge/.derived/{pitfalls,handoff}.xml`（行式 XML 片段流，可重建）+ `.unify-ledger.xml`（账本，不可重建）。
- `--knowledge-check` 已把这两类派生一致性检查**并入**（`--knowledge-pitfalls-src` / `--knowledge-handoff-src` / `--knowledge-pitfalls-proj`）。

## 安全边界

- 源目录**只读**；不提交 SVN。
- 每次运行只在 `--out` 下写：`knowledge.md`、`.derived/`、`versions/`、`.ledger.json`。

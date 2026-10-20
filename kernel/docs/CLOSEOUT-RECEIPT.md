# 收尾回执（Closeout Receipt）· v86

> 目的：把「收尾做完了」从**自陈**变成**可复算的四组事实**。
> 来历：2026-10-20 真机 —— 所有闸门绿、收敛点已推进到 r113，但知识库仍停 v0013（另有 2 段无标记的孤儿 L4）、
> 决策库只收敛 3/7。缺的不是人盯，是**闸门没测这一项**。

## 一、回执在哪

- `tools/closeout/receipt.xml`（`--close` 每次重写；`--receipt --write` 可单独立基线）。
- 只读查看：`dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --receipt`

## 二、四组事实（回执字段）

| 组 | 字段 | 判据 |
|---|---|---|
| `Convergence` | `archive` / `project` / `fingerprint` | 收敛点版本 + 内容指纹 |
| `Knowledge` | `version` / `l1..l4` / `orphans` / `unmarkedL4` / `promoted` / `liveSha` | 版本文件存在且 sha 对得上；四层齐；`unmarkedL4=0`；`orphans=0` |
| `Decision` | `entries` / `converged` / `gaps` / `orphans` / `l1..l4` | `gaps=0`、`orphans=0`、四层齐 |
| `Growth` | `sinceReceipt` / `knowledgeVersionDelta` / `l1Delta` / `decisionGapDelta` | 内容变了（`liveSha` 变）⇒ 版本必须增 |

## 三、收尾算「未完成」的三个条件（v86 第 20 条）

1. 活版知识自上次回执以来**内容变了**，但 `knowledge/.ledger.xml` 的版本**没有晋级**；
2. L4 区存在**没有 `<!--kn: …-->` 节点标记**的正文块，或派生树 `Orphans != 0`；
3. 决策库存在**空档**（D 条目没有 L4 归属）或**孤儿**（节点从 L1 不可达）。

> 注意：`--close` 仍是「只警告不阻断」（主人 2026-10-17 令），但 v86 起这三条会以 **block 红** + 回执字段
> 明确写出来；收尾报告不得再把这种情况说成「完成」。

## 四、OC 的三条复核命令（不重读全文）

```sh
# 1) 回执：版本/计数/孤儿/未标记 L4/增量
dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --receipt

# 2) 决策库：空档 0 / 孤儿 0 / 各层条数
dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --decision-converge

# 3) 知识树：L4 数与 Orphans（派生视图）
cat whitebox/workspace/knowledge/.derived/summary.xml
```

判据：`gaps=0` · `orphans=0` · `unmarkedL4=0` · `promoted=true` · 若 `liveSha` 变了则 `knowledgeVersionDelta >= 1`。

## 五、边界

- 回执是**派生视图**：真相仍在 `knowledge/.ledger.xml` / `DECISION-LIBRARY.md` / SVN；回执只负责把四组事实放在一处。
- 回执不替代 `--check`：未提交 / 未纳版 / 落点声明仍由 `--check` 的 block 项负责。

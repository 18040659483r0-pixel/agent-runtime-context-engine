#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""report-metrics.py —— 出报表数字（只读：仓库 + 源，不调用任何远端）。

用法：python3 tools/skill-repo/report-metrics.py --repo skill-repo-pilot --skills ~/.openclaw/workspace/skills
输出：Markdown 表格（L1 法则 / L2 问题地图 / L3 条 / 与原 SKILL.md 对比 / AI 用量）。
token 一列是**估算**（口径：0.55 token/字符，来自 llm-context-benchmark skill 的中文实测），不是 usage 实测。
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

TOKEN_PER_CHAR = 0.55
BYTES_PER_TOKEN = 3.36          # 与语料账本同源（84.0 KB → 25,605 token）


def stat(repo: Path, skills: Path, name: str):
    d = repo / name
    meta = json.loads((d / ".meta.json").read_text(encoding="utf-8"))
    src = Path(meta["source"]["path"]) if not skills else skills / name / "SKILL.md"
    strips = meta["strips"]
    bs = [s["bytes"] for s in strips]
    ls = [s["lines"] for s in strips]
    st = meta.get("l2_stats") or {}
    l1 = (d / "L1.md").stat().st_size
    l2 = (d / "L2.md").stat().st_size
    l3 = (d / "L3.jsonl").stat().st_size
    src_b = src.stat().st_size if src.exists() else meta["source"]["bytes"]
    u = meta["ai"]["usage"]
    return {
        "name": name, "n": len(strips), "q": st.get("questions", 0),
        "mode": meta.get("mode", "indexed"), "ratio": meta.get("ratio"),
        "ratio_idx": meta.get("ratio_indexed"),
        "layout_bytes": meta.get("layout_bytes"), "layout_tokens": meta.get("layout_tokens"),
        "layout_idx_bytes": meta.get("layout_indexed_bytes"), "layout_idx_tokens": meta.get("layout_indexed_tokens"),
        "l1_domain": meta.get("l1_domain", "misc"),
        "l2_rows": meta.get("l2_questions") or [],
        "refs": st.get("refs_total", 0), "refavg": st.get("refs_per_question_avg", 0),
        "one_many": st.get("one_to_many", 0), "orphan": len(st.get("orphan_strips") or []),
        "avg": sum(bs) / len(bs), "min": min(bs), "max": max(bs),
        "lavg": sum(ls) / len(ls), "lmin": min(ls), "lmax": max(ls),
        "l1": l1, "l2": l2, "l3": l3, "l1l2": l1 + l2, "src": src_b,
        "l1text": meta["l1"],
        "calls": meta["ai"]["calls"], "hit": meta["ai"]["cache_hit"],
        "prompt": u.get("prompt_tokens"), "completion": u.get("completion_tokens"),
        "total": u.get("total_tokens"), "reasoning": meta["ai"].get("reasoning_tokens"),
        "tomb": len(meta.get("tombstones") or []),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--skills", default=None)
    args = ap.parse_args()
    repo = Path(args.repo).expanduser().resolve()
    skills = Path(args.skills).expanduser() if args.skills else None
    names = sorted(p.name for p in repo.iterdir() if p.is_dir() and (p / ".meta.json").exists())
    rows = [stat(repo, skills, n) for n in names]

    print("\n## 两态（mode / ratio = (L1+L2)/整份 / 双条件）\n")
    print("| 技能 | 整份 B | L1 B | L2 B | L1+L2 B | L1+L2 token | ratio_indexed | 阈 0.15 | 限 2000 | mode |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        li = r["layout_idx_tokens"]
        ok_rel = "✓" if (r["ratio_idx"] or 0) <= 0.15 else "✗"
        ok_abs = "✓" if (li is not None and li <= 2000) else "✗"
        print("| %s | %d | %d | %d | %d | %.0f | %.4f | %s | %s | **%s** |"
              % (r["name"], r["src"], r["l1"], r["l2"], r["l1l2"], r["l1l2"] / BYTES_PER_TOKEN,
                 r["ratio_idx"] or 0, ok_rel, ok_abs, r["mode"]))

    print("\n## 全库常驻总量（Σ L1+L2；口语径 %.2f B/token）\n" % BYTES_PER_TOKEN)
    tot_l1 = sum(r["l1"] for r in rows)
    tot_l2 = sum(r["l2"] for r in rows)
    tot = tot_l1 + tot_l2
    print("| 口径 | 字节 | token（=B/%.2f） |" % BYTES_PER_TOKEN)
    print("|---|---|---|")
    print("| 全库 L1 | %d | %.0f |" % (tot_l1, tot_l1 / BYTES_PER_TOKEN))
    print("| 全库 L2 | %d | %.0f |" % (tot_l2, tot_l2 / BYTES_PER_TOKEN))
    print("| **全库常驻 L1+L2** | **%d** | **%.0f** |" % (tot, tot / BYTES_PER_TOKEN))
    print("| （对照）全库整份 | %d | %.0f |" % (sum(r["src"] for r in rows),
                                              sum(r["src"] for r in rows) / BYTES_PER_TOKEN))

    # 按域过滤：L1 按技能的 l1_domain 归属，L2 按行上的 [domain] 标签
    by = {}
    for r in rows:
        d = r["l1_domain"]
        by.setdefault(d, {"skills": 0, "l1": 0, "l2": 0})
        by[d]["skills"] += 1
        by[d]["l1"] += r["l1"]
        for row in r["l2_rows"]:
            dom = row.get("domain", "misc")
            b = len(("- [%s] %s → 见 L3 %s\n" % (dom, row["q"], " ".join(row["ids"]))).encode("utf-8"))
            by.setdefault(dom, {"skills": 0, "l1": 0, "l2": 0})
            by[dom]["l2"] += b
    print("\n## 按域过滤后的常驻量（只装该域的 L1+L2）\n")
    print("| 域 | 技能数 | L1 B | L2 B | 合计 B | 合计 token | 占全库常驻 |")
    print("|---|---|---|---|---|---|---|")
    for d in sorted(by, key=lambda x: -(by[x]["l1"] + by[x]["l2"])):
        bb = by[d]["l1"] + by[d]["l2"]
        print("| %s | %d | %d | %d | %d | %.0f | %.1f%% |"
              % (d, by[d]["skills"], by[d]["l1"], by[d]["l2"], bb, bb / BYTES_PER_TOKEN,
                 100.0 * bb / tot))

    print("\n## L1 法则（常驻 1 行/技能）\n")
    print("| 技能 | 字节 | 法则 |")
    print("|---|---|---|")
    for r in rows:
        print("| %s | %d | %s |" % (r["name"], r["l1"], r["l1text"]))

    print("\n## L2 问题地图 / L3 条\n")
    print("| 技能 | 问题数 | 引用次数 | 均引用 | 一对多 | 孤儿条 | L3 条数 | 条均行 | 条均字节 | 最短/最长条 |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        print("| %s | %d | %d | %.2f | %d | %d | %d | %.2f | %.0f | %d / %d |"
              % (r["name"], r["q"], r["refs"], r["refavg"], r["one_many"], r["orphan"], r["n"],
                 r["lavg"], r["avg"], r["min"], r["max"]))

    print("\n## 三层字节数 vs 原 SKILL.md\n")
    print("| 技能 | L1 | L2 | L1+L2（常驻） | L3（整库） | 原 SKILL.md | 常驻/整份 | 单条/整份 |")
    print("|---|---|---|---|---|---|---|---|")
    for r in rows:
        print("| %s | %d | %d | %d | %d | %d | %.1f%% | %.1f%% |"
              % (r["name"], r["l1"], r["l2"], r["l1l2"], r["l3"], r["src"],
                 100.0 * r["l1l2"] / r["src"], 100.0 * r["avg"] / r["src"]))

    print("\n| 技能 | 单条估算 token | 常驻估算 token | 整份估算 token | 整份→单条节省 |")
    print("|---|---|---|---|---|")
    for r in rows:
        print("| %s | %.0f | %.0f | %.0f | %.1f× |"
              % (r["name"], r["avg"] * TOKEN_PER_CHAR, r["l1l2"] * TOKEN_PER_CHAR,
                 r["src"] * TOKEN_PER_CHAR, r["src"] / r["avg"]))

    print("\n## AI 用量（远端 usage 实测）\n")
    print("| 技能 | 真实调用 | 缓存命中 | prompt_tokens | completion_tokens | 其中思维链 | total | 墓碑 |")
    print("|---|---|---|---|---|---|---|---|")
    for r in rows:
        print("| %s | %d | %s | %s | %s | %s | %s | %d |"
              % (r["name"], r["calls"], "是" if r["hit"] else "否", r["prompt"], r["completion"],
                 r["reasoning"], r["total"], r["tomb"]))
    print("\n（估算口径：%.2f token/字符；token 列为**估算**，usage 列为远端实测）" % TOKEN_PER_CHAR)


if __name__ == "__main__":
    main()

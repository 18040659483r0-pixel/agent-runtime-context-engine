#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""split-skill-layers.py —— 把技能正文里的**重型知识**从 L3 分家到 L4（确定性 / 离线 / 不调模型）。

L4 = **为什么这么做 / 试过什么错的 / 完整翻车面**（证据 · 完整清单 · 被否掉的尝试 · 实测机制）。

## 不变量（本脚本的全部价值）
    frontmatter_raw + Σ(所有条，按 start_line 序) [+ 末尾换行] == 源 SKILL.md 逐字节
  条只**搬家 / 重切**、不重写 ⇒ 往返由**构造**保证，不靠自觉。

## 两条路径（按技能既有形态，互不干扰）
| 形态 | 判据 | 切法 | 为什么 |
|---|---|---|---|
| **indexed**（AI 已切条） | `meta.strips` > 1 条 | **尊重原条边界**，整条判重 | 保 id 稳定 ⇒ L2 的「见 L3 S-x」不失效 |
| **hybrid**（整份 1 条） | `meta.strips` ≤ 1 | 按 `^## ` **段**重切 | 1 条吞掉全文件，不重切则永远分不开 |

## id 规则（**统一 id 空间**）
  · 条 id = `S-<短名>-<NNN>`，**搬家不改 id**（只在 L3/L4 之间移动）；
  · `seq` 在**各自文件内**从 1 连续（装载器闸门）；
  · 于是 L2 的引用对 L3/L4 **都成立**（前提：装载器扫两个文件 —— 由 `--check` 的「L2 可达性」项守）。

## 闸门
  G1 语法 · G2 往返（每次运行都算）· G3 只动 L3/L4（不改 SKILL.md / L1 / L2）· G4 L2 可达性

用法：
  python3 tools/skill-repo/split-skill-layers.py --skills <s> --repo <r> --check
  python3 tools/skill-repo/split-skill-layers.py --skills <s> --repo <r> --apply [--only a,b]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

EXTRACTOR = "split-skill-layers.py"
EXTRACTOR_VERSION = "l4-split/2.0.0"

#: 段标题命中这些词 ⇒ 归 **L4（重型知识）**。
#: 判据不是「主题」而是「它在回答哪一类问题」：为什么 / 试过什么错的 / 完整翻车面。
HEAVY = re.compile(r"失败|翻车|实测|证据|被否|反例|踩坑|教训|风险|为什么|故障|排查|错误|事故|坑|复盘|代价")
SEC = re.compile(r"^## .")


def sha_b(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def body_of(skill_dir: Path, meta: dict):
    raw = (skill_dir / "SKILL.md").read_bytes()
    fm = meta["frontmatter_raw"].encode("utf-8")
    if not raw.startswith(fm):
        raise SystemExit("[split] 源 SKILL.md 不以账本里的 frontmatter_raw 开头（账本过期？）")
    body = raw[len(fm):]
    ends_nl = bool(meta.get("source", {}).get("body_ends_with_newline"))
    text = body[:-1] if (ends_nl and body.endswith(b"\n")) else body
    return text.decode("utf-8").split("\n"), ends_nl


def heavy_lines(lines):
    """逐行是否落在**重型段**里（段 = `## ` 到下一个 `## `）。"""
    n = len(lines)
    starts = [i for i, l in enumerate(lines) if SEC.match(l)]
    flags = [False] * n
    bounds = starts + [n]
    for k, s in enumerate(starts):
        h = bool(HEAVY.search(lines[s][3:]))
        for i in range(s, bounds[k + 1]):
            flags[i] = h
    return flags


def pieces_of(lines, meta):
    """→ [(a, b, heavy, title, orig_id)]；a/b 为 0-based [a,b)。"""
    n = len(lines)
    big = heavy_lines(lines)
    strips = meta.get("strips") or []
    out = []
    if len(strips) > 1:
        # indexed：尊重原条边界（不重切 ⇒ id 不漂）
        covered = [False] * n
        for s in strips:
            a = max(0, int(s.get("start_line", 1)) - 1)
            b = min(n, int(s.get("end_line", 0)))
            if a >= b:
                continue
            for i in range(a, b):
                covered[i] = True
            seg = big[a:b]
            heavy = bool(seg) and all(seg)          # 整条都在重型段里 ⇒ 搬家；混着 ⇒ 留 L3
            out.append((a, b, heavy, str(s.get("title") or "").strip(), s.get("id")))
        # 补空隙：`strips` 没盖到的行**自成一拍**。
        # 为什么（2026-09-24 当场踩到）：strips 未必覆盖全文（实测 ctxidx：条止于 147 行 / 正文 155 行）
        # ⇒ 尾部静默丢失、往返变红。**空隙不是「没有内容」，是「没被声明」**。
        i = 0
        while i < n:
            if not covered[i]:
                j = i
                while j < n and not covered[j]:
                    j += 1
                t = lines[i][3:].strip() if SEC.match(lines[i]) else str(meta.get("skill", ""))
                out.append((i, j, big[i], t, None))
                i = j
            else:
                i += 1
        out.sort(key=lambda x: x[0])
    else:
        # hybrid：按段重切
        cuts = {0, n}
        for i, l in enumerate(lines):
            if SEC.match(l):
                cuts.add(i)
        cs = sorted(cuts)
        for i in range(len(cs) - 1):
            a, b = cs[i], cs[i + 1]
            if a >= b:
                continue
            t = lines[a][3:].strip() if SEC.match(lines[a]) else str(meta.get("skill", ""))
            out.append((a, b, big[a], t, None))
    return out


def render(rows) -> bytes:
    return "".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n" for r in rows).encode("utf-8")


def split_skill(skill_dir: Path, meta: dict):
    lines, ends_nl = body_of(skill_dir, meta)
    ps = pieces_of(lines, meta)
    short = meta.get("short_name") or "skill"
    l3, l4 = [], []
    # **全局计数器**分配 id：L3/L4 各条从同一计数器取号 ⇒ 同技能内**由构造保证不重复**。
    # 为什么（2026-09-24 当场抓到撞号）：原公式 `len(ps) - hid + 1` 在 hybrid 路径会与 L3 号段重叠
    # ⇒ `S-ctxidx-030` 在 L4 里出现两次；装载器按 id 取条**静默取错**（比报错更贵）。
    gid = 0
    for (a, b, heavy, title, oid) in ps:
        text = "\n".join(lines[a:b])
        gid += 1
        rid = oid or ("S-%s-%03d" % (short, gid))
        rec = {"id": rid, "skill": meta.get("skill"), "title": title,
               "kind": "Heavy" if heavy else "Reference",
               "domain": meta.get("l1_domain", "misc"),
               "start_line": a + 1, "end_line": b, "text": text}
        (l4 if heavy else l3).append(rec)
    # seq 在各自文件内 1..N 连续（装载器闸门）；hybrid 的 id 按全局序补
    for i, r in enumerate(l3):
        r["seq"] = i + 1
        if not r.get("id"):
            r["id"] = "S-%s-%03d" % (short, i + 1)
    l3 = [{k: r[k] for k in ("id", "skill", "seq", "title", "kind", "domain",
                             "start_line", "end_line", "text")} for r in l3]

    allrows = sorted(l3 + l4, key=lambda r: r["start_line"])
    rebuilt = "\n".join(r["text"] for r in allrows) + ("\n" if ends_nl else "")
    body = "\n".join(lines) + ("\n" if ends_nl else "")
    diag = dict(l3=len(l3), l4=len(l4),
                l4_bytes=sum(len(r["text"].encode()) for r in l4),
                rt=(rebuilt == body))
    return l3, l4, diag


def l2_ids(meta):
    """L2 里引用的所有 L3 id（用于可达性检查）。"""
    out = set()
    for q in (meta.get("l2_questions") or []):
        for i in (q.get("ids") or []):
            out.add(i)
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="SKILL.md → L3(怎么做) + L4(重型知识)，逐字节可往返")
    ap.add_argument("--skills", required=True, type=Path)
    ap.add_argument("--repo", required=True, type=Path)
    ap.add_argument("--only", default=None)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args(argv)
    if args.check == args.apply:
        ap.error("二选一：--check 或 --apply")

    want = {x.strip() for x in args.only.split(",")} if args.only else None
    rc = 0
    done = tot = 0
    for d in sorted(args.skills.iterdir()):
        if not d.is_dir() or not (d / "SKILL.md").exists():
            continue
        if want and d.name not in want:
            continue
        if (d / "references").is_dir():
            print("SKIP %-28s 有 references/（走 build-l4.py，另论）" % d.name, file=sys.stderr)
            continue
        rs = args.repo / d.name
        mp = rs / ".meta.json"
        if not mp.exists():
            print("SKIP %-28s 未注册（无 .meta.json）" % d.name, file=sys.stderr)
            continue
        tot += 1
        meta = json.loads(mp.read_text(encoding="utf-8"))
        try:
            l3, l4, diag = split_skill(d, meta)
        except SystemExit as e:
            print("RED  %-28s %s" % (d.name, e), file=sys.stderr); rc = 1; continue
        if not diag["rt"]:
            print("RED  %-28s 往返不一致" % d.name, file=sys.stderr); rc = 1; continue
        # G4 L2 可达性：L2 引用的 id 必须仍在 L3 ∪ L4 里
        have = {r["id"] for r in l3} | {r["id"] for r in l4}
        missing = sorted(l2_ids(meta) - have)
        if missing:
            print("WARN %-28s L2 引用了不存在的 id：%s" % (d.name, ",".join(missing[:4])), file=sys.stderr)
        if diag["l4"]:
            done += 1
        print("OK  %-28s L3 %3d 条 · L4 %2d 条（%6d B）· 往返 ✓%s"
              % (d.name, diag["l3"], diag["l4"], diag["l4_bytes"],
                 "  ← L2 缺 %d" % len(missing) if missing else ""))
        if args.check:
            continue
        (rs / "L4.jsonl").write_bytes(render(l4)) if l4 else ((rs / "L4.jsonl").unlink()
                                                              if (rs / "L4.jsonl").exists() else None)
        (rs / "L3.jsonl").write_bytes(render(l3))
        meta.setdefault("artifacts", {})["L3.jsonl"] = sha_b(render(l3))
        meta["artifacts"]["L4.jsonl"] = sha_b(render(l4)) if l4 else None
        meta["layer_split"] = {"by": EXTRACTOR + "/" + EXTRACTOR_VERSION,
                               "rule": "section-title-heavy", "l3": len(l3), "l4": len(l4)}
        mp.write_text(json.dumps(meta, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    if not args.check:
        print("[split] 已分家：%d/%d 个技能有 L4" % (done, tot), file=sys.stderr)
    return rc


if __name__ == "__main__":
    raise SystemExit(main())

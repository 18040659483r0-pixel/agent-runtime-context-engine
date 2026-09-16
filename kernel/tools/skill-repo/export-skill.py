#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""export-skill.py —— 技能仓库 → SKILL.md（保留 frontmatter）+ round-trip 逐字节比对。

用法：
  python3 tools/skill-repo/export-skill.py --repo skill-repo-pilot --skills ~/.openclaw/workspace/skills
  python3 tools/skill-repo/export-skill.py --repo skill-repo-pilot --only image-compress --out /tmp/x

判据（DESIGN-SKILL-LAYERS §4.2 / S2 / S10）：
  frontmatter_raw + L3 各条按 seq 拼接 == 源 SKILL.md 逐字节相等。
  差异必须**逐条列出**写进 roundtrip.md（不许"大致相同"）。
本脚本不接触密钥（离线，不发请求）。
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import json
import sys
from pathlib import Path


def sha_b(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def first_diff(a: bytes, b: bytes):
    n = min(len(a), len(b))
    for i in range(n):
        if a[i] != b[i]:
            return i
    return n if len(a) != len(b) else -1


def reconstruct(repo_skill_dir: Path):
    """仓库 → (bytes, meta, 副本明细)。"""
    meta = json.loads((repo_skill_dir / ".meta.json").read_text(encoding="utf-8"))
    l3_path = repo_skill_dir / "L3.jsonl"
    recs = [json.loads(ln) for ln in l3_path.read_text(encoding="utf-8").splitlines() if ln.strip()]
    recs.sort(key=lambda r: r["seq"])
    ends_nl = meta["source"]["body_ends_with_newline"]
    body = "\n".join(r["text"] for r in recs) + ("\n" if ends_nl else "")
    data = (meta["frontmatter_raw"] + body).encode("utf-8")
    return data, meta, recs


def diff_lines(a: str, b: str):
    return list(difflib.unified_diff(a.splitlines(True), b.splitlines(True),
                                     fromfile="源 SKILL.md", tofile="导出 SKILL.md", n=1))


def main():
    ap = argparse.ArgumentParser(description="技能仓库 → SKILL.md + round-trip 逐字节比对")
    ap.add_argument("--repo", required=True, help="仓库根目录（build-skill-repo.py 的 --out）")
    ap.add_argument("--skills", default=None, help="技能源目录（缺省用账本里记录的源路径）")
    ap.add_argument("--only", default=None, help="只导出这些技能（逗号分隔）")
    ap.add_argument("--out", default=None, help="导出目录（缺省 <repo>/export）")
    args = ap.parse_args()

    repo = Path(args.repo).expanduser().resolve()
    out = Path(args.out).expanduser().resolve() if args.out else (repo / "export")
    dirs = sorted(p for p in repo.iterdir() if p.is_dir() and (p / ".meta.json").exists()
                  and not p.name.startswith("."))
    if args.only:
        want = {n.strip() for n in args.only.split(",") if n.strip()}
        dirs = [d for d in dirs if d.name in want]
    if not dirs:
        print("[FAIL] 仓库里没有可导出的技能（%s）" % repo, file=sys.stderr)
        sys.exit(2)

    rows, failed = [], 0
    for d in dirs:
        data, meta, recs = reconstruct(d)
        src = Path(meta["source"]["path"]) if not args.skills else (Path(args.skills).expanduser() / d.name / "SKILL.md")
        sk_out = out / d.name
        sk_out.mkdir(parents=True, exist_ok=True)
        (sk_out / "SKILL.md").write_bytes(data)

        if not src.exists():
            rows.append((d.name, "SKIP", "源不存在：%s" % src))
            (sk_out / "roundtrip.md").write_text("# round-trip %s\n\n源不存在：%s\n" % (d.name, src),
                                                encoding="utf-8")
            failed += 1
            continue

        raw = src.read_bytes()
        equal = (raw == data)
        diff_at = first_diff(raw, data)
        lines = []
        lines.append("# round-trip 逐字节比对：%s\n" % d.name)
        lines.append("- 源：`%s`（%d 字节，sha256 `%s`）" % (src, len(raw), sha_b(raw)[:16]))
        lines.append("- 导出：`%s`（%d 字节，sha256 `%s`）" % (sk_out / "SKILL.md", len(data), sha_b(data)[:16]))
        lines.append("- 条数：%d（seq 1..%d）；frontmatter 保留：%s" % (len(recs), recs[-1]["seq"] if recs else 0,
                                                                      bool(meta.get("frontmatter_raw"))))
        lines.append("- **结论：%s**" % ("逐字节相等 ✅" if equal else "存在差异 ❌"))
        if not equal:
            lines.append("\n- 首个差异字节偏移：%d（源 %d 字节 / 导出 %d 字节）" % (diff_at, len(raw), len(data)))
            lines.append("\n```diff")
            dl = diff_lines(raw.decode("utf-8", "replace"), data.decode("utf-8", "replace"))
            for ln in dl[:200]:
                lines.append(ln.rstrip("\n"))
            if len(dl) > 200:
                lines.append("...（差异行超过 200，已截断）")
            lines.append("```")
        (sk_out / "roundtrip.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
        rows.append((d.name, "EQUAL" if equal else "DIFF", "%d/%d 字节，%d 条" % (len(data), len(raw), len(recs))))
        if not equal:
            failed += 1

    # 汇总
    out.mkdir(parents=True, exist_ok=True)
    summary = ["# round-trip 汇总（仓库 → SKILL.md）\n",
               "| 技能 | 结果 | 详情 |", "|---|---|---|"]
    for name, status, detail in rows:
        summary.append("| %s | %s | %s |" % (name, status, detail))
    ok_n = sum(1 for _, s, _ in rows if s == "EQUAL")
    summary.append("\n**逐字节相等：%d/%d**" % (ok_n, len(rows)))
    (out / "roundtrip.md").write_text("\n".join(summary) + "\n", encoding="utf-8")

    for name, status, detail in rows:
        print("  %-8s %-24s %s" % ("[EQUAL]" if status == "EQUAL" else "[DIFF]", name, detail))
    print("\n[ROUND-TRIP] 逐字节相等 %d/%d，详情见 %s" % (ok_n, len(rows), out / "roundtrip.md"))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()

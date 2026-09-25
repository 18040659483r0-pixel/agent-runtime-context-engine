#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""build-l4.py —— 技能的 references 分册 → L4 条（确定性 / 离线 / 不调模型）。

设计依据：docs/DESIGN-L4-LAYER.md
L4 = 技能内部**重型知识**层（证据 / 完整清单 / 被否掉的尝试 / 实测细节）。
与 L3 的分工：L3 回答「怎么做」，L4 回答「为什么这么做、试过什么错的、完整的翻车面」。

纪律（与 build-skill-repo.py 同规，只是本阶段**不需要 AI**）：
  S1 逐字切 —— 条正文 = 源文件逐字切片（`## ` 二级标题 = 一条）；
     文件头（H1 + 到第一条 `## ` 之前）= 一条 kind:`Header`（seq 随序）
     ⇒ header + Σtext == 源文件（逐字节往返，由构造保证）。
  S2 确定性 —— 无时间戳、字段序固定、LF、无 BOM；同输入 ⇒ 同字节。
  S3 只读源 —— `--check` 重算比对既有产物，不一致即报红（非零退出）。
  S4 离线 —— 不调用任何远端、不读密钥。

用法：
  python3 tools/skill-repo/build-l4.py --skills <skills_root> --repo <skill-repo>            # 生成
  python3 tools/skill-repo/build-l4.py --skills <skills_root> --repo <skill-repo> --check    # 校验
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

EXTRACTOR = "build-l4.py"
EXTRACTOR_VERSION = "l4/1.0.0"
ARTIFACT = "L4.jsonl"
SOURCE_DIR = "references"
HEAD_RE = re.compile(r"^## (.+?)\s*$")
KIND = "Heavy"
HEADER_KIND = "Header"
ID_TMPL = "L4-%s-%03d"


def short_name_of(repo: Path, skill: str) -> str:
    """短名取自账本（与 L3 的 id 前缀同源）；没有就按技能名派生。"""
    meta = repo / skill / ".meta.json"
    if meta.exists():
        try:
            got = json.loads(meta.read_text(encoding="utf-8")).get("short_name")
            if got:
                return str(got)
        except Exception:
            pass
    return "".join(ch for ch in skill.lower() if ch.isalnum())[:6] or "skill"


def split_file(raw: str):
    """→ (header, [(title, block), ...])；header + Σblock == raw（逐字节）。"""
    lines = raw.splitlines(keepends=True)
    starts = [i for i, ln in enumerate(lines) if HEAD_RE.match(ln.rstrip("\n"))]
    if not starts:
        return raw, []
    header = "".join(lines[: starts[0]])
    blocks = []
    for n, i in enumerate(starts):
        end = starts[n + 1] if n + 1 < len(starts) else len(lines)
        block = "".join(lines[i:end])
        blocks.append((HEAD_RE.match(lines[i].rstrip("\n")).group(1), block))
    return header, blocks


def sources_of(skills_root: Path, skill: str):
    d = skills_root / skill / SOURCE_DIR
    if not d.is_dir():
        return []
    return sorted(p for p in d.rglob("*.md") if p.is_file())


def records_for(skills_root: Path, skill: str, short: str):
    rows = []
    for src in sources_of(skills_root, skill):
        raw = src.read_text(encoding="utf-8")
        header, blocks = split_file(raw)
        rel = str(src.relative_to(skills_root))
        if header != "":
            rows.append({"title": "（文件头 / 分册说明）", "kind": HEADER_KIND,
                         "source": rel, "text": header})
        for title, block in blocks:
            rows.append({"title": title, "kind": KIND, "source": rel, "text": block})
    out = []
    for i, r in enumerate(rows):
        rec = {"id": ID_TMPL % (short, i), "skill": skill, "seq": i}
        rec.update(r)
        out.append(rec)
    return out


def render(records) -> bytes:
    if not records:
        return b""
    body = "\n".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in records)
    return (body + "\n").encode("utf-8")


def deps(skills_root: Path, repo: Path):
    """要处理的技能 = 源侧有 references/ 且产物侧已建目录（注册过）的技能。"""
    out = []
    if not skills_root.is_dir():
        return out
    for d in sorted(skills_root.iterdir()):
        if not d.is_dir():
            continue
        if not (repo / d.name).is_dir():
            continue
        if sources_of(skills_root, d.name):
            out.append(d.name)
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="references/*.md → skill-repo/<skill>/L4.jsonl")
    ap.add_argument("--skills", required=True, type=Path)
    ap.add_argument("--repo", required=True, type=Path)
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)

    skills_root: Path = args.skills
    repo: Path = args.repo
    skills = deps(skills_root, repo)
    rc = 0
    for skill in skills:
        short = short_name_of(repo, skill)
        records = records_for(skills_root, skill, short)
        want = render(records)
        target = repo / skill / ARTIFACT
        if args.check:
            got = target.read_bytes() if target.exists() else None
            if got != want:
                print("RED  %-28s 产物与重算不一致（%s）" % (skill, target), file=sys.stderr)
                rc = 1
            else:
                print("OK   %-28s %2d 条 / %d B" % (skill, len(records), len(want)))
            continue
        target.write_bytes(want)
        print("WROTE %-28s %2d 条 / %d B" % (skill, len(records), len(want)), file=sys.stderr)

    # 往返自检：header + Σtext == 源文件（逐字节）
    for skill in skills:
        short = short_name_of(repo, skill)
        for src in sources_of(skills_root, skill):
            raw = src.read_text(encoding="utf-8")
            recs = [r for r in records_for(skills_root, skill, short)
                    if r["source"] == str(src.relative_to(skills_root))]
            rebuilt = "".join(r["text"] for r in recs)
            if rebuilt != raw:
                print("RED  往返不一致：%s" % src, file=sys.stderr)
                rc = 1
            elif not args.check:
                print("ROUNDTRIP OK %s" % src, file=sys.stderr)
    return rc


if __name__ == "__main__":
    raise SystemExit(main())

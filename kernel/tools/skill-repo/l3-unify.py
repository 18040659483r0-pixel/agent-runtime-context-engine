#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""l3-unify.py —— 把「坑集」与「交接」派生成可逐条定位的 JSONL 索引（与 skill L3 同构）。

坑集   ：projects/AgentRuntime/docs/PITFALLS.md  →  id = P-<proj>-<NNN>  （如 P-rt-030）
交接   ：workspace/handoff/*.md                   →  id = H-<nn>-<NNN>   （如 H-13-002）

一条 = 一个「遇到什么问题 → 怎么处理」的最小完整单元。

边界规则（确定性，可逐字节往返）：
  · `## `（二级标题）= 一条；文件前言（H1 + 开头引言 / `---`）= 不入条，仅作往返用的头部。
  · 无 `## ` 的文件（如 12-共享md换行符）→ 整份一条（title 取 H1，startLine=1）。
  · `### ` 及更深标题 = 条内正文，本次**不下切**（列为待裁决）。
  · 行号即地址：JSONL 第 N 行 = 第 N 条；startLine/endLine 定位源文件，sha256 校验内容未变。

id 纪律（与 skill `S-<short>-###` 同构）：
  · 稳定、不回收：改内容不改 id；删条 = 墓碑（kind:"Tombstone"，本次无删除，仅记约定）。
  · `<NNN>` 是**文档序**（1-based 序号），不是源文件里的「人类编号」（如坑集 1. / 1.5 / 2. …只进 title）。
  · 交接 `<nn>` = 文件名前导数字（2 位零填充）；同号冲突（现有两个 "04"）按**文件名排序**给后续文件加小写字母后缀
    （第一个 "04" 保持裸号，第二个 "04" → "04a"），保证唯一、确定、不静默。

纪律（与语料迁移器 / knowledge-repo 同一套）：先 `--check`（应报错）→ `--bump` → 重跑 → 再 `--check`。

确定性：产物 LF + 无 BOM + 无时间戳 + 排序固定（时间戳只进账本）。纯 Python3 标准库，不联网。

用法：
    python3 l3-unify.py --source <md|dir> --kind pitfalls|handoff [--proj rt] --out <file.jsonl>       # 派生 + 首次建账本
    python3 l3-unify.py --source <md|dir> --kind ... --out <file.jsonl> --check                        # 校验（4 道闸门）
    python3 l3-unify.py --source <md|dir> --kind ... --out <file.jsonl> --derive-only                  # 只重建（证明可重建，不动账本）
    python3 l3-unify.py --source <md|dir> --kind ... --out <file.jsonl> --bump                         # 显式接受 id 变化（重写账本）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from collections import OrderedDict
from pathlib import Path

LEDGER_SCHEMA = "l3-unify/ledger/1"
KINDS = ("pitfalls", "handoff")

# 账本默认放在 --out 的「上两级」目录（即 workspace/knowledge/.derived/*.jsonl → workspace/knowledge/.unify-ledger.json），
# 与 knowledge-repo 的 .ledger.json 并列、但 schema 不同，互不覆盖。
DEFAULT_LEDGER_NAME = ".unify-ledger.json"
DEFAULT_REF = "workspace/knowledge/knowledge.md"


def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def find_repo_root(path: Path) -> Path:
    """向上找同时含 workspace/ 与 projects/ 的根（software-company）。找不到回退 CWD。"""
    p = path.resolve()
    for parent in [p] + list(p.parents):
        if (parent / "workspace").is_dir() and (parent / "projects").is_dir():
            return parent
    return Path.cwd()


def read_text(path: Path) -> str:
    # 用 utf-8（保留可能存在的 BOM → \ufeff），往返按字节重建时原样保留。
    return path.read_bytes().decode("utf-8")


def entry_text(lines: list, start: int, end: int) -> str:
    """start/end 为 1-based 闭区间；返回该条目文本（含标题行）。"""
    return "\n".join(lines[start - 1:end])


def clean_title(s: str) -> str:
    s = s.strip()
    # 仅去掉行首可能混入的 BOM（外观修正，不动内容/往返）
    return s.lstrip("\ufeff")


def parse_sections(lines: list) -> tuple:
    """返回 (preamble_line_count, sections)。

    sections: [{title, startLine, endLine}]，startLine/endLine 1-based 闭区间，按文档序。
    preamble_line_count = 首条之前的行数（0-based 计数）。
    无 `## ` 时：整份一条（title 取 H1）。
    """
    heading_idx = [i for i, ln in enumerate(lines) if ln.startswith("## ")]
    sections = []
    for k, hi in enumerate(heading_idx):
        end = heading_idx[k + 1] if k + 1 < len(heading_idx) else len(lines)
        title = clean_title(lines[hi][3:])
        sections.append({"title": title, "startLine": hi + 1, "endLine": end})
    if not sections:
        title = ""
        for ln in lines:
            if ln.startswith("# "):
                title = clean_title(ln[2:])
                break
        if not title:
            title = "(整份)"
        sections.append({"title": title, "startLine": 1, "endLine": len(lines)})
    preamble = sections[0]["startLine"] - 1
    return preamble, sections


def relpath_to_root(path: Path, root: Path) -> str:
    try:
        return os.path.relpath(str(path.resolve()), str(root))
    except ValueError:
        return str(path)


def derive(kind: str, source_arg: str, proj: str | None) -> tuple:
    """派生 → (records, files)。files = {相对路径: lines}（供往返校验，避免重复读）。

    records 字段固定序：id / kind / file / title / startLine / endLine / sha256。
    """
    src = Path(source_arg)
    root = find_repo_root(src)
    records: list = []
    files: dict = {}

    if kind == "pitfalls":
        if not src.is_file():
            sys.exit("[FAIL] --kind pitfalls 需要 --source 指向单个 .md 文件：%s" % src)
        if not proj:
            sys.exit("[FAIL] --kind pitfalls 必须给 --proj <项目短名>（如 rt）")
        lines = read_text(src).split("\n")
        files[relpath_to_root(src, root)] = lines
        _, sections = parse_sections(lines)
        for seq, s in enumerate(sections, 1):
            rid = "P-%s-%03d" % (proj, seq)
            records.append(_record(rid, kind, relpath_to_root(src, root), s, lines))

    elif kind == "handoff":
        if not src.is_dir():
            sys.exit("[FAIL] --kind handoff 需要 --source 指向目录：%s" % src)
        seen: dict = {}
        for p in sorted([q for q in src.iterdir() if q.suffix == ".md"]):
            m = re.match(r"^(\d+)", p.name)
            num = int(m.group(1)) if m else 0
            seen[num] = seen.get(num, 0) + 1
            cnt = seen[num]
            nn = "%02d" % num if cnt == 1 else "%02d%s" % (num, chr(ord("a") + cnt - 2))
            lines = read_text(p).split("\n")
            frel = relpath_to_root(p, root)
            files[frel] = lines
            _, sections = parse_sections(lines)
            for seq, s in enumerate(sections, 1):
                rid = "H-%s-%03d" % (nn, seq)
                records.append(_record(rid, kind, frel, s, lines))
    else:
        sys.exit("[FAIL] 未知 --kind：%s（可选 %s）" % (kind, "/".join(KINDS)))

    return records, files


def _record(rid: str, kind: str, frel: str, s: dict, lines: list) -> OrderedDict:
    t = entry_text(lines, s["startLine"], s["endLine"])
    rec = OrderedDict()
    rec["id"] = rid
    rec["kind"] = kind
    rec["file"] = frel
    rec["title"] = s["title"]
    rec["startLine"] = s["startLine"]
    rec["endLine"] = s["endLine"]
    rec["sha256"] = sha256_bytes(t.encode("utf-8"))
    return rec


def records_to_bytes(records: list) -> bytes:
    return "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in records).encode("utf-8")


# ------------------------------------------------------------------ 账本

def load_ledger(path: Path) -> dict:
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return {"schema": LEDGER_SCHEMA}


def save_ledger(path: Path, led: dict) -> None:
    path.write_bytes((json.dumps(led, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def id_list(records: list) -> list:
    return [r["id"] for r in records]


# ------------------------------------------------------------------ 校验

def verify_roundtrip(kind: str, records: list, files: dict, source_arg: str):
    """①往返逐字节 + ②覆盖完整：按 file 分组，用 startLine/endLine 逐字节重建源，与原文比对。"""
    by_file: dict = OrderedDict()
    for r in records:
        by_file.setdefault(r["file"], []).append(r)

    all_ok = True
    detail = []
    for frel, recs in by_file.items():
        lines = files[frel]
        recs = sorted(recs, key=lambda r: r["startLine"])
        # 覆盖/无重叠：首条起点 == 前言行数+1、相邻连续、末条到文件尾
        contiguous = True
        for i in range(len(recs)):
            if recs[i]["startLine"] > recs[i]["endLine"]:
                contiguous = False
            if i > 0 and recs[i - 1]["endLine"] + 1 != recs[i]["startLine"]:
                contiguous = False
        if recs[-1]["endLine"] != len(lines):
            contiguous = False
        # 往返重建：按行拼接、只 join 一次（空前言不会凭空多出一行换行）
        all_lines = list(lines[0:recs[0]["startLine"] - 1])
        for r in recs:
            all_lines.extend(lines[r["startLine"] - 1:r["endLine"]])
        rebuilt = "\n".join(all_lines)
        ok = (rebuilt == "\n".join(lines)) and contiguous
        all_ok = all_ok and ok
        detail.append({"file": frel, "entries": len(recs), "equal": bool(rebuilt == "\n".join(lines)),
                       "contiguous": contiguous})
    return all_ok, detail


def verify_ids(records: list) -> tuple:
    """③ id 唯一：无重复 id。"""
    ids = id_list(records)
    dup = sorted({i for i in ids if ids.count(i) > 1})
    return (len(dup) == 0), dup


def verify_ledger(led: dict, kind: str, proj: str | None, source_arg: str, records: list) -> str:
    """③ id 稳定：与账本比对。

    返回：
      ok            —— id 序列完全一致
      append        —— 仅尾部新增（现有前缀不变）⇒ 安全，自动扩账本，不需 bump
      changed       —— 现有 id 位移/重排/删除 ⇒ 需显式 --bump（不许静默改）
      warn-missing  —— 账本无该 kind 记录（首次派生）
    """
    entry = led.get(kind)
    if not entry:
        return "warn-missing"
    prev = entry.get("ids") or []
    cur = id_list(records)
    if prev == cur:
        return "ok"
    if cur[:len(prev)] == prev:
        return "append"
    return "changed"


def dangling_pointers(kind: str, records: list, ref_files: list) -> list:
    """④ 悬空指针：引用文件里出现不存在的 id ⇒ 告警（不阻断）。"""
    known = {r["id"] for r in records}
    if kind == "pitfalls":
        pat = re.compile(r"\bP-[A-Za-z0-9]+-\d{3}\b")
    else:
        pat = re.compile(r"\bH-\d{2}[a-z]?-\d{3}\b")
    dangling = []
    for rf in ref_files:
        if not Path(rf).exists():
            continue
        text = read_text(Path(rf))
        for tok in sorted(set(pat.findall(text))):
            if tok not in known:
                dangling.append("%s（%s）" % (tok, rf))
    return dangling


# ------------------------------------------------------------------ 动作

def main() -> None:
    ap = argparse.ArgumentParser(description="坑集/交接 → 逐条 JSONL 索引（与 skill L3 同构）")
    ap.add_argument("--source", required=True, help="坑集：单个 .md；交接：目录")
    ap.add_argument("--kind", required=True, choices=KINDS)
    ap.add_argument("--proj", default=None, help="坑集必填：项目短名（如 rt）")
    ap.add_argument("--out", required=True, help="输出 JSONL 路径")
    ap.add_argument("--check", action="store_true", help="校验：往返 + 覆盖 + id 唯一/稳定 + 悬空指针")
    ap.add_argument("--derive-only", action="store_true", help="只重建 JSONL（证明可重建，不动账本）")
    ap.add_argument("--bump", action="store_true", help="显式接受 id 变化，重写账本")
    ap.add_argument("--ledger", default=None, help="账本路径（默认 --out 上两级/.unify-ledger.json）")
    ap.add_argument("--ref", default=None, help="悬空指针扫描的引用文件（默认 workspace/knowledge/knowledge.md）")
    args = ap.parse_args()

    out = Path(args.out)
    ledger_path = Path(args.ledger) if args.ledger else out.resolve().parent.parent / DEFAULT_LEDGER_NAME
    root = find_repo_root(Path(args.source))
    ref_default = (root / DEFAULT_REF) if (root / DEFAULT_REF).exists() else None
    ref_files = [args.ref] if args.ref else ([str(ref_default)] if ref_default else [])

    # 所有动作都先派生（确定性）
    records, files = derive(args.kind, args.source, args.proj)
    new_bytes = records_to_bytes(records)
    led = load_ledger(ledger_path)

    if args.check:
        failed = 0

        # ① 派生产物与磁盘一致（派生缺失 / 与源不一致 ⇒ 报错）
        if not out.exists():
            print("  [FAIL] 派生缺失         %s 不存在（请先派生）" % out, file=sys.stderr)
            failed += 1
        else:
            on_disk = out.read_bytes()
            same = on_disk == new_bytes
            print("  [%s] 派生与源一致       %s（%d 字节，重派生 %d 字节）%s"
                  % ("PASS" if same else "FAIL", out.name, len(on_disk), len(new_bytes),
                     "" if same else " ← 与源不一致"))
            failed += 0 if same else 1

        # ①② 往返 + 覆盖
        ok, detail = verify_roundtrip(args.kind, records, files, args.source)
        eq = sum(1 for d in detail if d["equal"])
        cov = all(d["contiguous"] for d in detail)
        print("  [%s] 往返逐字节         %d/%d 个文件逐字节重建等于原文"
              % ("PASS" if ok else "FAIL", eq, len(detail)))
        print("  [%s] 覆盖完整           每个源条目恰一条、无空档重叠（%d 文件）%s"
              % ("PASS" if cov else "FAIL", len(detail), "" if cov else " ← 漏条/重叠"))
        failed += 0 if ok else 1
        failed += 0 if cov else 1

        # ③ id 唯一
        uniq, dup = verify_ids(records)
        print("  [%s] id 唯一             共 %d 条，重复 id %s"
              % ("PASS" if uniq else "FAIL", len(records), dup if dup else "无"))
        failed += 0 if uniq else 1

        # ③ id 稳定（账本比对）
        st = verify_ledger(led, args.kind, args.proj, args.source, records)
        if st == "ok":
            print("  [PASS] id 稳定             账本 id 序列与本次派生一致")
        elif st == "append":
            added = len(records) - len(led[args.kind].get("ids", []))
            print("  [PASS] id 稳定             尾部新增 %d 条（现有 id 未位移，安全）" % added)
        elif st == "warn-missing":
            print("  [WARN] id 稳定             账本无 %s 记录（首次派生，无历史可比对）" % args.kind)
        else:
            print("  [FAIL] id 稳定             账本 id 序列已变化 ⇒ 需显式 `--bump` 接受（不许静默改）")
            failed += 1

        # ④ 悬空指针（告警，不阻断）
        dang = dangling_pointers(args.kind, records, ref_files)
        print("  [%s] 悬空指针             %s"
              % ("WARN" if dang else "PASS", "；".join(dang) if dang else "无（引用文件未引用不存在的 id）"))

        print("\n[CHECK] %s" % ("全部通过" if failed == 0 else "存在失败项（%d 项）" % failed))
        sys.exit(1 if failed else 0)

    # ---- 写产物 + 账本
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(new_bytes)

    if args.derive_only:
        print("[DERIVE] 已从源重建 %s（%d 条 / %d 字节；**未动账本**）" % (out.name, len(records), len(new_bytes)))
        sys.exit(0)

    entry = led.setdefault(args.kind, {})
    if args.kind == "pitfalls":
        entry["proj"] = args.proj
    entry["source"] = args.source
    prev_ids = entry.get("ids")

    if args.bump:
        entry["ids"] = id_list(records)
        save_ledger(ledger_path, led)
        print("[BUMP] 已接受 id 变化并重写账本 %s（%s：%d 条）" % (ledger_path.name, args.kind, len(records)))
    elif prev_ids is None:
        entry["ids"] = id_list(records)
        save_ledger(ledger_path, led)
        print("[DERIVE] 已派生 %s（%d 条 / %d 字节）；账本首次建立 %s" % (out.name, len(records), len(new_bytes), ledger_path.name))
    elif id_list(records) == prev_ids:
        print("[DERIVE] 已派生 %s（%d 条 / %d 字节）；id 序列与账本一致（无变化）" % (out.name, len(records), len(new_bytes)))
    elif id_list(records)[:len(prev_ids)] == prev_ids:
        # 尾部追加：安全，自动扩账本，不需 bump
        entry["ids"] = id_list(records)
        save_ledger(ledger_path, led)
        print("[DERIVE] 已派生 %s（%d 条 / %d 字节）；尾部新增 %d 条，账本已自动扩展（现有 id 未位移）"
              % (out.name, len(records), len(new_bytes), len(records) - len(prev_ids)))
    else:
        print("[DERIVE] 已写 %s（%d 条 / %d 字节）" % (out.name, len(records), len(new_bytes)))
        print("[WARN]  id 序列与账本不一致（非尾部追加），但**未更新账本** ⇒ 先 `--check` 确认，再 `--bump` 接受（不许静默改）")

    # 顺手打印文件分布（供报表）
    by_file: dict = OrderedDict()
    for r in records:
        by_file.setdefault(r["file"], 0)
        by_file[r["file"]] += 1
    print("[STAT] 文件 %d 个：%s" % (len(by_file), ", ".join("%s=%d" % (f, c) for f, c in by_file.items())))


if __name__ == "__main__":
    main()

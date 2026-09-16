#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""knowledge-repo.py —— 把技能仓库再发成「用户可见」的形态（md + 版本 + 可重建派生 + 账本）。

形态（主人 2026-09-16 01:0x 定）：
    knowledge/
      knowledge.md                    ← **当前版本**（人类可读；L1+L2+L3 全在里面，可直接手改）
      versions/knowledge-<时间戳>.md   ← 每次收尾一份快照（只追加，不删不覆盖）
      .derived/                       ← **可重建**的派生（ids / L3 逐条 / L1 / L2 / 域索引 / 往返）
      .ledger.json                    ← 账本：版本号 / md 指纹 / 写入来源 / 收尾记录（不进 prompt）

纪律（K1~K6）：
  K1 绝不静默    —— 每次写入都记账（emit/promote/rollback 各留一条）；closeout **只判定不写**
  K2 版本只增不删 —— versions/ 只追加；rollback 也**追加新版本**，历史不动
  K3 派生可重建   —— .derived/ 能从 knowledge.md 重建（`--derive-only`）；反之不成立
  K4 一条命令可回滚 —— `--rollback <ver>`
  K5 指纹重算不信旧值 —— 所有判定都**重算** md / 版本文件的 sha256，不读账本里的旧值当真值
  K6 晋级只发生在收尾 —— 只有 `--promote`（收尾流程调用）会写 versions/

用法：
    python3 tools/skill-repo/knowledge-repo.py --repo skill-repo-pilot --out knowledge            # emit
    python3 tools/skill-repo/knowledge-repo.py --out knowledge --check                            # 校验
    python3 tools/skill-repo/knowledge-repo.py --out knowledge --derive-only                      # 只重建派生
    python3 tools/skill-repo/knowledge-repo.py --out knowledge --closeout                         # 三态判定（只读）
    python3 tools/skill-repo/knowledge-repo.py --out knowledge --promote                          # 晋级为版本快照
    python3 tools/skill-repo/knowledge-repo.py --out knowledge --rollback <ver>                   # 回滚（= 把旧版重新晋级）
本脚本**不调用任何远端**（纯 re-emit + 本地派生）。
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime
from pathlib import Path

BYTES_PER_TOKEN = 3.36
MD_NAME = "knowledge.md"
LEDGER_NAME = ".ledger.json"
DERIVED_DIR = ".derived"
VERSIONS_DIR = "versions"
K_GATES = ["K1 绝不静默", "K2 版本只增不删", "K3 派生可重建而 md 不可反向生成",
           "K4 一条命令可回滚", "K5 指纹重算不信旧值", "K6 晋级只发生在收尾"]
ORIGIN = {"emit-initial": "初始基线（首次 emit）", "promote": "记账写入 A（收尾晋级）",
          "promote-external": "外部手改后确认 B（用户裁决后晋级）", "rollback": "回滚（旧版重新晋级）"}

H_SKILL = re.compile(r"^## (\S+)\s*$")
H_STRIP = re.compile(r"^### (S-[A-Za-z0-9]+-\d{3})\s+(.*)$")
MARK = re.compile(r"^<!--\s*skill:\s*(?P<name>[^|]+?)\s*\|\s*src:\s*(?P<src>[^|]+?)\s*"
                  r"\|\s*ends_with_newline:\s*(?P<en>true|false)\s*\|\s*mode:\s*(?P<mode>\S+)\s*-->\s*$")
L2ROW = re.compile(r"^- \[([a-z]+)\] (.+?) → 见 L3 ((?:S-[A-Za-z0-9]+-\d{3})(?:\s+S-[A-Za-z0-9]+-\d{3})*)\s*$")
L1LINE = re.compile(r"^\*\*L1 法则\*\*：(.+)$")


def sha_b(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def jdump(obj) -> bytes:
    return (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def now_stamp() -> str:
    return datetime.now().strftime("%Y%m%d-%H%M%S")


def rt_state_counts(rt):
    """往返三态计数：equal（逐字节相等）/ source_missing（源已归档/卸载）/ mismatch（字节不等）。"""
    return {"equal": sum(1 for r in rt if r.get("state") == "equal"),
            "source_missing": sum(1 for r in rt if r.get("state") == "source_missing"),
            "mismatch": sum(1 for r in rt if r.get("state") == "mismatch")}


# ------------------------------------------------------------------ emit

def emit_md(repo: Path, skills_root: Path):
    """技能仓库 → knowledge.md 字节 + 每技能元数据。"""
    metas = []
    for p in sorted(repo.iterdir()):
        if p.is_dir() and (p / ".meta.json").exists():
            metas.append(json.loads((p / ".meta.json").read_text(encoding="utf-8")))
    metas.sort(key=lambda m: -m["source"]["bytes"])

    buf = "".join(["# Skill Knowledge · 技能知识库（当前版本）\n\n",
                    "> 本文件是**人类可读的唯一活版**：L1 法则 / L2 问题地图 / L3 正文全在这里，可直接手改。\n",
                    "> L3 锚点格式 = `### <id> <标题>`；`.derived/` 从本文件重建（反之不成立）；收尾时 `--promote` 晋级为 `versions/` 快照。\n\n"])
    info = {}
    for m in metas:
        name = m["skill"]
        fo = Path(m["source"]["path"])
        strip_recs = [json.loads(l) for l in
                      (repo / name / "L3.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
        l2rows = [ln for ln in (repo / name / "L2.md").read_text(encoding="utf-8").splitlines()
                  if ln.startswith("- ")]
        buf += "## %s\n" % name
        buf += ("<!-- skill: %s | src: %s | ends_with_newline: %s | mode: %s -->\n"
                % (name, fo, "true" if m["source"]["body_ends_with_newline"] else "false", m["mode"]))
        buf += "```yaml\n" + (m.get("frontmatter_raw") or "").rstrip("\n") + "\n```\n"
        buf += "**L1 法则**：%s\n\n" % m["l1"]
        buf += "**L2 问题地图**\n" + "".join(r + "\n" for r in l2rows) + "\n"
        for rec in strip_recs:
            t = rec["text"]
            lead = len(t) - len(t.lstrip("\n"))
            trail = len(t) - len(t.rstrip("\n"))
            core = t.strip("\n")
            buf += "### %s %s\n" % (rec["id"], rec["title"])
            buf += "<!--wrap: %d %d-->\n\n" % (lead, trail)   # 前后换行数（正文本身的空行不丢）
            buf += core + "\n\n"
        info[name] = {"src": str(fo), "ends_with_newline": m["source"]["body_ends_with_newline"],
                      "frontmatter": m.get("frontmatter_raw") or "", "l1": m["l1"], "mode": m["mode"],
                      "ratio_indexed": m.get("ratio_indexed"), "benefit": m.get("benefit"),
                      "strips": [{"id": r["id"], "seq": r["seq"], "title": r["title"],
                                  "kind": r["kind"], "domain": r["domain"]} for r in strip_recs]}
    return buf.encode("utf-8"), info


def is_skill_header(lines, i):
    """只有「下一行是 skill 标记注释」的 `## X` 才算技能头 ——
    否则它是**源正文里的** markdown 标题（L3 逐字文本里本来就有）。"""
    return bool(H_SKILL.match(lines[i])) and i + 1 < len(lines) and bool(MARK.match(lines[i + 1]))


def parse_md(md_bytes: bytes):
    """knowledge.md → 结构化（供派生 + 往返）。

    源正文里本就有 `## ` / `### ` 标题 ⇒ 只有「下一行是 skill 标记注释」的 `## X` 才算技能头；
    L3 正文 = 标题行到下一个标题行之间的块，对称剥掉写者固定加的 1 前 / 2 后换行。
    """
    text = md_bytes.decode("utf-8")
    lines = text.split("\n")
    heads = [i for i in range(len(lines)) if is_skill_header(lines, i)]
    skills = {}
    for k, i in enumerate(heads):
        end = heads[k + 1] if k + 1 < len(heads) else len(lines)
        block = "\n".join(lines[i:end])
        name = H_SKILL.match(lines[i]).group(1)
        mk = MARK.match(lines[i + 1])
        mfm = re.search(r"```yaml\n(.*?)\n```\n", block, re.S)
        ent = {"skill": name, "src": mk.group("src").strip(),
               "ends_with_newline": (mk.group("en") == "true"), "mode": mk.group("mode"),
               "frontmatter": (mfm.group(1) + "\n") if mfm else "",
               "l1": "", "l2": [], "strips": [], "md_line": i + 1}
        for ln in block.split("\n"):
            ml1 = L1LINE.match(ln)
            if ml1:
                ent["l1"] = ml1.group(1)
            m2 = L2ROW.match(ln)
            if m2:
                ent["l2"].append({"domain": m2.group(1), "q": m2.group(2), "ids": m2.group(3).split()})
        hits = list(re.finditer(r"^### (S-[A-Za-z0-9]+-\d{3}) (.+)$", block, re.M))
        for n, h in enumerate(hits):
            seg_end = hits[n + 1].start() if n + 1 < len(hits) else len(block)
            seg = block[h.end():seg_end]
            mw = re.match(r"\n<!--wrap: (\d+) (\d+)-->\n\n", seg)
            if mw:
                core = seg[mw.end():]
                if core.endswith("\n\n"):
                    core = core[:-2]
                elif core.endswith("\n"):
                    core = core[:-1]
                text_ = "\n" * int(mw.group(1)) + core + "\n" * int(mw.group(2))
            else:
                if seg.startswith("\n"):
                    seg = seg[1:]
                if seg.endswith("\n\n"):
                    seg = seg[:-2]
                elif seg.endswith("\n"):
                    seg = seg[:-1]
                text_ = seg
            ent["strips"].append({"id": h.group(1), "title": h.group(2).strip(),
                                  "seq": n + 1, "text": text_,
                                  "md_line": i + 1 + block[:h.start()].count("\n") + 1})
        skills[name] = ent
    return skills


def derive(skills: dict, sources: dict):
    """从 md 结构派生出全部 .derived/ 文件（确定性的）。"""
    ids, l3, l1, l2, dom, rt = [], [], [], [], {}, []
    for name, sk in skills.items():
        l1.append({"skill": name, "l1": sk["l1"], "mode": (sources.get(name) or {}).get("mode")})
        for s in sk["strips"]:
            kind = next((x["kind"] for x in (sources.get(name) or {}).get("strips", [])
                         if x["id"] == s["id"]), None)
            ids.append({"id": s["id"], "skill": name, "seq": s["seq"], "title": s["title"],
                        "kind": kind, "md_line": s["md_line"]})
            l3.append({"id": s["id"], "skill": name, "seq": s["seq"], "title": s["title"],
                       "kind": kind, "text": s["text"]})
        for row in sk["l2"]:
            l2.append({"skill": name, "domain": row["domain"], "q": row["q"], "ids": row["ids"]})
            for i_ in row["ids"]:
                d = dom.setdefault(row["domain"], {"domain": row["domain"], "skills": [], "questions": 0, "ids": []})
                if name not in d["skills"]:
                    d["skills"].append(name)
                d["questions"] += 1
                if i_ not in d["ids"]:
                    d["ids"].append(i_)
        src = sk["src"]
        rebuilt = (sk["frontmatter"] + "\n".join(s["text"] for s in sk["strips"])
                   + ("\n" if sk["ends_with_newline"] else "")).encode("utf-8")
        rebuilt_sha = sha_b(rebuilt)
        if not Path(src).exists():
            state, src_sha, equal = "source_missing", None, None
        else:
            cur = Path(src).read_bytes()
            if cur == rebuilt:
                state, src_sha, equal = "equal", rebuilt_sha, True
            else:
                state, src_sha, equal = "mismatch", sha_b(cur), False
        rt.append({"skill": name, "source": src, "state": state,
                   "source_sha256": src_sha, "rebuilt_sha256": rebuilt_sha, "equal": equal})
    files = {
        "ids.jsonl": "".join(json.dumps(x, ensure_ascii=False) + "\n" for x in ids).encode("utf-8"),
        "l3.jsonl": "".join(json.dumps(x, ensure_ascii=False) + "\n" for x in l3).encode("utf-8"),
        "l1.jsonl": "".join(json.dumps(x, ensure_ascii=False) + "\n" for x in l1).encode("utf-8"),
        "l2.jsonl": "".join(json.dumps(x, ensure_ascii=False) + "\n" for x in l2).encode("utf-8"),
        "domains.jsonl": "".join(json.dumps(dom[k], ensure_ascii=False) + "\n" for k in sorted(dom)).encode("utf-8"),
        "roundtrip.json": jdump({"equal": rt_state_counts(rt)["equal"],
                                    "source_missing": rt_state_counts(rt)["source_missing"],
                                    "mismatch": rt_state_counts(rt)["mismatch"],
                                    "total": len(rt), "skills": rt}),
        "summary.json": jdump({
            "skills": len(skills), "l3_entries": len(l3), "l2_questions": len(l2),
            "l1_bytes": sum(len((r["l1"] + "\n").encode("utf-8")) for r in l1),
            "l2_bytes": sum(len(("- [%s] %s → 见 L3 %s\n" % (r["domain"], r["q"], " ".join(r["ids"]))).encode("utf-8")) for r in l2),
            "bytes_per_token": BYTES_PER_TOKEN,
        }),
    }
    return files, rt


def write_derived(out: Path, files: dict):
    d = out / DERIVED_DIR
    d.mkdir(parents=True, exist_ok=True)
    for name, data in files.items():
        (d / name).write_bytes(data)


def load_ledger(out: Path):
    p = out / LEDGER_NAME
    if p.exists():
        return json.loads(p.read_text(encoding="utf-8"))
    return {"schema": "knowledge-repo/ledger/1", "gates": K_GATES, "current": None,
            "versions": [], "closeouts": [], "sources": {}}


def save_ledger(out: Path, led: dict):
    (out / LEDGER_NAME).write_bytes(jdump(led))


def snapshot(out: Path, md: bytes, led: dict, source: str, note: str = ""):
    """K6：只有 promote / rollback / 首次 emit 会写版本快照（只追加）。

    命名**只用版本号**（`knowledge-v0001.md`，四位零填充、整数递增）——
    时间戳不进命名、不进 prompt，只进 `.ledger.json`（保证同输入同产物、可逐字节复算）。
    """
    vdir = out / VERSIONS_DIR
    vdir.mkdir(parents=True, exist_ok=True)
    ver = (led["versions"][-1]["version"] + 1) if led["versions"] else 1
    fn = "knowledge-v%04d.md" % ver
    (vdir / fn).write_bytes(md)
    rec = {"version": ver, "file": "%s/%s" % (VERSIONS_DIR, fn), "sha256": sha_b(md),
           "bytes": len(md), "at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
           "source": source, "origin": ORIGIN.get(source, source), "note": note}
    led["versions"].append(rec)
    led["current"] = dict(rec)
    return rec


# ------------------------------------------------------------------ 判定

def judge(out: Path, led: dict):
    """三态判定（K5：一切指纹重算，不信账本旧值）。"""
    md_path = out / MD_NAME
    md = md_path.read_bytes()
    md_sha = sha_b(md)
    last = None
    if led["versions"]:
        f = out / led["versions"][-1]["file"]
        if f.exists():
            last = {"version": led["versions"][-1]["version"], "file": led["versions"][-1]["file"],
                    "sha256": sha_b(f.read_bytes())}
    cur = led.get("current") or {}
    if last and md_sha == last["sha256"]:
        state, why = "C", "md 与最新版本快照一致 ⇒ 无变化，不产生空版本"
    elif cur.get("sha256") == md_sha:
        state, why = "A", "md 与账本记录的最近一次写入一致，但尚未晋级 ⇒ 可晋级"
    else:
        state, why = "B", "md 指纹与账本记录不一致 ⇒ 疑似**外部手改**（未记账的写入）"
    diff = ""
    if state == "B" and last:
        old = (out / last["file"]).read_text(encoding="utf-8").splitlines(True)
        new = md.decode("utf-8").splitlines(True)
        dl = list(difflib.unified_diff(old, new, fromfile="versions/%s" % Path(last["file"]).name,
                                       tofile="knowledge.md", n=2))
        diff = "".join(dl[:400])
    return {"state": state, "why": why, "md_sha256": md_sha, "md_bytes": len(md),
            "last_version": last, "ledger_current_sha256": cur.get("sha256"), "diff": diff}


def check_unify(out: Path, pitfalls_src: str, handoff_src: str, proj: str) -> int:
    """把「坑集/交接」派生一致性校验（l3-unify.py --check）接进来；返回失败项数。

    派生缺失 / 与源不一致 ⇒ 报错（不静默跳过）。
    """
    tool = Path(__file__).resolve().parent / "l3-unify.py"
    failed = 0
    specs = []
    if pitfalls_src and Path(pitfalls_src).exists():
        specs.append(("pitfalls", pitfalls_src, proj))
    if handoff_src and Path(handoff_src).exists():
        specs.append(("handoff", handoff_src, None))
    for kind, src, prj in specs:
        out_jsonl = out / DERIVED_DIR / ("pitfalls.jsonl" if kind == "pitfalls" else "handoff.jsonl")
        cmd = [sys.executable, str(tool), "--source", src, "--kind", kind,
               "--out", str(out_jsonl), "--check"]
        if prj:
            cmd += ["--proj", prj]
        print("  -- %s 派生一致性（l3-unify.py --check）--" % kind)
        r = subprocess.run(cmd, capture_output=True, text=True)
        sys.stdout.write(r.stdout)
        if r.returncode != 0:
            sys.stdout.write(r.stderr)
            failed += 1
        else:
            print("  [PASS] %s 派生           %s 与源一致、id 稳定" % (kind, out_jsonl.name))
    return failed


def main():
    ap = argparse.ArgumentParser(description="技能仓库 → 用户可见形态（md + 版本 + 派生 + 账本）")
    ap.add_argument("--repo", default=None, help="技能仓库根（emit 需要；--check/--derive-only/--closeout 不需要）")
    ap.add_argument("--out", required=True, help="knowledge/ 输出根")
    ap.add_argument("--check", action="store_true", help="校验：派生一致 + 往返逐字节 + 版本完整性")
    ap.add_argument("--derive-only", action="store_true", help="只从 knowledge.md 重建 .derived/（证明可重建）")
    ap.add_argument("--closeout", "--knowledge-closeout", dest="closeout", action="store_true",
                    help="三态判定（只读、不写、不交互）")
    ap.add_argument("--promote", "--knowledge-promote", dest="promote", action="store_true",
                    help="晋级为新的版本快照（收尾时调用；版本号 +1）")
    ap.add_argument("--accept-external", action="store_true",
                    help="配合 --promote：接受用户的外部手改（态 B）并晋级，来源记为 B")
    ap.add_argument("--rollback", "--knowledge-rollback", dest="rollback", default=None,
                    help="回滚到指定版本号（把旧版重新晋级为新版）")
    ap.add_argument("--pitfalls-src", default="projects/AgentRuntime/docs/PITFALLS.md",
                    help="坑集源 .md（--check 接 l3-unify 校验；相对仓库根）")
    ap.add_argument("--handoff-src", default="workspace/handoff",
                    help="交接目录（--check 接 l3-unify 校验；相对仓库根）")
    ap.add_argument("--pitfalls-proj", default="rt",
                    help="坑集 id 项目短名（P-<proj>-NNN）")
    args = ap.parse_args()

    out = Path(args.out).expanduser().resolve()
    out.mkdir(parents=True, exist_ok=True)
    led = load_ledger(out)

    if args.rollback is not None:
        want = int(args.rollback)
        rec = next((v for v in led["versions"] if v["version"] == want), None)
        if not rec:
            print("[FAIL] 版本不存在：v%04d" % want, file=sys.stderr)
            sys.exit(2)
        md = (out / rec["file"]).read_bytes()
        (out / MD_NAME).write_bytes(md)
        files, rt = derive(parse_md(md), led.get("sources") or {})
        write_derived(out, files)
        new = snapshot(out, md, led, source="rollback", note="回滚：旧版 v%04d 重新晋级" % want)
        led["closeouts"].append({"at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"), "state": "rollback",
                                 "action": "rollback -> v%04d，重新晋级为 v%04d" % (want, new["version"])})
        save_ledger(out, led)
        print("[ROLLBACK] 已回滚到 v%04d 的内容，并**追加**为新版本 v%04d（历史未删）：%s"
              % (want, new["version"], new["file"]))
        print("[ROLLBACK] 派生已随之重建；往返 equal %d / missing %d / mismatch %d"
              % (rt_state_counts(rt)["equal"], rt_state_counts(rt)["source_missing"], rt_state_counts(rt)["mismatch"]))
        sys.exit(0)

    if args.derive_only:
        md = (out / MD_NAME).read_bytes()
        files, rt = derive(parse_md(md), led.get("sources") or {})
        write_derived(out, files)
        print("[DERIVE] 已从 %s 重建 %s/（%d 个文件；往返 equal %d / missing %d / mismatch %d）"
              % (MD_NAME, DERIVED_DIR, len(files), rt_state_counts(rt)["equal"],
                 rt_state_counts(rt)["source_missing"], rt_state_counts(rt)["mismatch"]))
        sys.exit(0)

    if args.closeout:
        j = judge(out, led)
        print("[CLOSEOUT] 态 %s：%s" % (j["state"], j["why"]))
        print("[CLOSEOUT] md sha256 = %s（%d 字节，**重算**）" % (j["md_sha256"], j["md_bytes"]))
        print("[CLOSEOUT] 上版 = %s（sha %s）" % (j["last_version"]["file"] if j["last_version"] else "（无）",
                                                (j["last_version"] or {}).get("sha256", "-")))
        print("[CLOSEOUT] 账本 current.sha256 = %s" % j["ledger_current_sha256"])
        if j["state"] == "A":
            print("[CLOSEOUT] 建议：`--promote` 晋级为 v%04d"
                  % ((led["versions"][-1]["version"] + 1) if led["versions"] else 1))
        elif j["state"] == "B":
            print("[CLOSEOUT] 建议：**由 TUI 询问用户**（工具自身不交互）—— ① 接受本次手改 ⇒ `--promote --accept-external`；"
                  "② 丢弃手改 ⇒ `--rollback %04d`；③ 先看下面的 diff 再定。"
                  % ((j["last_version"] or {}).get("version") or 0))
            print("[CLOSEOUT] --- unified diff（上版 → 当前）---")
            print(j["diff"] or "（无 diff）")
        else:
            print("[CLOSEOUT] 建议：无需动作（不产生空版本）")
        led["closeouts"].append({"at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"), "state": j["state"],
                                 "md_sha256": j["md_sha256"],
                                 "prev_version": (j["last_version"] or {}).get("version"),
                                 "prev_sha256": (j["last_version"] or {}).get("sha256"),
                                 "action": "reported-only"})
        save_ledger(out, led)          # K1：连"只判定"也留痕（不改 md、不产生版本）
        sys.exit(0)

    if args.promote:
        j = judge(out, led)
        if j["state"] == "C":
            print("[PROMOTE] 拒绝：无变化（md == 最新版本）⇒ **不产生空版本**")
            sys.exit(0)
        if j["state"] == "B" and not args.accept_external:
            print("[PROMOTE] 拒绝：当前是**外部手改（态 B）**，未经用户裁决 ⇒ 先 `--closeout` 交给 TUI 询问；"
                  "用户确认接受后加 `--accept-external` 晋级，或 `--rollback <ver>` 丢弃")
            sys.exit(1)
        md = (out / MD_NAME).read_bytes()
        src = "promote-external" if j["state"] == "B" else "promote"
        new = snapshot(out, md, led, source=src,
                       note="收尾晋级（态 %s）" % j["state"])
        save_ledger(out, led)
        print("[PROMOTE] 已晋级：v%04d → %s（md sha %s；来源：%s）"
              % (new["version"], new["file"], new["sha256"][:16], new["origin"]))
        sys.exit(0)

    if args.check:
        failed = 0
        md_path = out / MD_NAME
        if not md_path.exists():
            print("[FAIL] %s 不存在" % md_path, file=sys.stderr)
            sys.exit(2)
        md = md_path.read_bytes()
        skills = parse_md(md)
        files, rt = derive(skills, led.get("sources") or {})

        same = all((out / DERIVED_DIR / n).exists() and (out / DERIVED_DIR / n).read_bytes() == d
                   for n, d in files.items())
        print("  [%s] 派生一致           %d 个派生文件与从 md 重算的结果%s"
              % ("PASS" if same else "FAIL", len(files), "逐字节相同" if same else "不一致"))
        failed += 0 if same else 1

        rt_c = rt_state_counts(rt)
        missing = [r["skill"] for r in rt if r["state"] == "source_missing"]
        mismatch = [r["skill"] for r in rt if r["state"] == "mismatch"]
        if mismatch:
            print("  [FAIL] 往返（md→派生→SKILL.md）%d/%d 逐字节相等（字节不等：%s）"
                  % (rt_c["equal"], rt_c["equal"] + rt_c["mismatch"], mismatch[:5]))
            failed += 1
        else:
            print("  [PASS] 往返（md→派生→SKILL.md）%d/%d 逐字节相等（mismatch 0）"
                  % (rt_c["equal"], rt_c["equal"] + rt_c["mismatch"]))
        if missing:
            print("  [WARN] 源已归档/卸载       %d 个技能 src 不存在 ⇒ 跳过字节比对：%s"
                  % (len(missing), missing[:8]))

        bad = []
        for v in led["versions"]:
            f = out / v["file"]
            if not f.exists():
                bad.append("%s 缺失" % v["file"])
            elif sha_b(f.read_bytes()) != v["sha256"]:
                bad.append("%s 指纹不符（重算）" % v["file"])
        print("  [%s] 版本完整性         %d 个版本快照%s（只增不删：%s）"
              % ("PASS" if not bad else "FAIL", len(led["versions"]),
                 "全部指纹重算通过" if not bad else "异常：%s" % bad, "是"))
        failed += 0 if not bad else 1

        j = judge(out, led)
        print("  [%s] 账本 ↔ md          %s（md 重算 sha %s / 账本 %s）"
              % ("PASS" if j["state"] in ("A", "C") else "WARN", j["state"], j["md_sha256"][:16],
                 (j["ledger_current_sha256"] or "-")[:16]))
        s = json.loads((out / DERIVED_DIR / "summary.json").read_text(encoding="utf-8"))
        print("  [PASS] 常驻汇总         L1 %d B + L2 %d B = %d B = %.0f token（口径 %.2f）"
              % (s["l1_bytes"], s["l2_bytes"], s["l1_bytes"] + s["l2_bytes"],
                 (s["l1_bytes"] + s["l2_bytes"]) / BYTES_PER_TOKEN, BYTES_PER_TOKEN))
        failed += check_unify(out, args.pitfalls_src, args.handoff_src, args.pitfalls_proj)
        print("\n[CHECK] %s（失败 %d 项）" % ("全部通过" if failed == 0 else "存在失败项", failed))
        sys.exit(1 if failed else 0)

    # ---- 默认动作：emit
    if not args.repo:
        print("[FAIL] emit 需要 --repo <技能仓库>", file=sys.stderr)
        sys.exit(2)
    repo = Path(args.repo).expanduser().resolve()
    md, info = emit_md(repo, None)
    (out / MD_NAME).write_bytes(md)
    led["sources"] = info
    if led["versions"]:
        files, rt = derive(parse_md(md), led.get("sources") or {})
        write_derived(out, files)
        rec = {"version": None, "file": None, "sha256": sha_b(md), "bytes": len(md),
               "at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"), "source": "emit", "note": "K1 记账写入"}
        led["current"] = rec
        print("[EMIT] 已重写 %s（%d 字节）与 %s/；**未**晋级（晋级只在收尾 `--promote`）" % (MD_NAME, len(md), DERIVED_DIR))
    else:
        rec = snapshot(out, md, led, source="emit-initial", note="首次基线")
        files, rt = derive(parse_md(md), led.get("sources") or {})
        write_derived(out, files)
        print("[EMIT] 首次发版：%s（%d 字节）+ 基线版本 v%04d（%s）" % (MD_NAME, len(md), rec["version"], rec["file"]))
    save_ledger(out, led)
    print("[EMIT] 技能 %d 个；往返 equal %d / missing %d / mismatch %d"
          % (len(info), rt_state_counts(rt)["equal"], rt_state_counts(rt)["source_missing"], rt_state_counts(rt)["mismatch"]))


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""G6 · 双跑对照 —— **采集器 + 报告生成器 + 闸门**（范式同 `tools/shadow-compose.py`）

门序（`docs/REPORT-HOST-SWITCH-PREMORTEM.md`）：G5 影子模式 ✅ → **G6 双跑对照** → G7 有限切换。
判据（`docs/DESIGN-V4.4-TUI.md` §六·阶段 1）：**正确率不降；规模与命中可解释（不要求相同）**。

三条子命令：

    python3 tools/double-run.py run-wb                 # [WB] 侧跑任务集（真模型 + PTY 真审批），写记录
    python3 tools/double-run.py render                 # 由记录重算对照块，写进结果文档（散文不动）
    python3 tools/double-run.py --check                # 只校验：重算 == 文档块？记录字段是否非空？两侧是否齐？

**纪律（血泪换来的，逐条都有代价）**：
  1. **数字不手抄**：文档块一律由本工具从**记录文件**重算（同 G5）。`--check` 让漂移跑一次就现形。
  2. **闸门必须断言「有数」而非「在不在」**（PITFALLS #62）：只验文件存在 ⇒ 截断/空表都会静默通过。
     ⇒ 这里断言关键字段（答复、prompt、cached）**非空**，以及**两侧都到齐**。
  3. **任务文本两侧逐字相同**，唯一例外是 `{SCRATCH}`（各侧解析为自己的临时目录）。
  4. **尺子独立**：本工具不属 Runtime，Runtime 永不引用本目录。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                                     # projects/AgentRuntime
TASK_FILE = os.path.join(HERE, "double-run-tasks.json")
WB_RUNNER = os.path.join(ROOT, "src/AgentRuntime.Cli/e2e-shadow-stage1.py")
WB_STREAM = os.path.join(ROOT, ".stage1/stream.jsonl")
WB_TRANSCRIPT = "/tmp/stage1-wb-transcript.txt"
RUNS = os.path.join(ROOT, "benchmark/runs")
DOC = os.path.join(ROOT, "docs/REHEARSAL-STAGE1-DOUBLE-RUN.md")

BEGIN = "<!-- g6:double-run begin"
END = "<!-- g6:double-run end -->"

SCRATCH_WB = "/tmp/g6-scratch/wb"
SCRATCH_OC = "/tmp/g6-scratch/oc"

DIAG = re.compile(r"^\s*(?:\[turn (\d+)\]\s*)?(tokens\.\w+|latency\.\w+|finish_reason)\s*:?\s*(\S+)")

# 审批口径（坑 #69）：「**项**」= 人可见的**逐项**提示（多面动作逐项问 —— 一次 `exec` =
# `[1/2] proc.exec` + `[2/2] net.request` = **2 项**）；「**批**」= **一次工具运行**的授权面。
# 两者**不是同一把尺子** ⇒ 记录里**分开存**、表里**分开列**，绝不合成一个含糊的「次数」。
APPROVALS_RE = re.compile(r"审批次数:\s*(\d+)\s*项\s*/\s*(\d+)\s*批")
LEGACY_APPROVALS_RE = re.compile(r"审批次数:\s*(\d+)")          # 旧驱动只报一个数（= 项数）

# 夹具：**逐字**抄自驱动的收尾打印（`e2e-shadow-stage1.py` 那条 `print(" 审批次数:", …)`）。
FIXTURE_NEW = (" 审批次数: 2 项 / 1 批（一批 = 一次工具运行的授权面；"
               "多面动作逐项点头，如 exec = proc.exec + net.request）· 续跑行数（由宿主打）: 3")
FIXTURE_LEGACY = " 审批次数: 7 · 续跑行数（由宿主打）: 1"


def dash(v) -> str:
    """None → 「—」。**0 是合法的数**，不许显示成「—」。"""
    return "—" if v is None else str(v)


def stamp_of(rec: dict | None) -> str:
    """记录的跑次指纹（`%Y%m%d-%H%M%S`）—— 直接拄记录，不手写。"""
    return (rec or {}).get("stamp") or "—"


def run_of(side: str) -> str:
    """该侧记录所属的 **run 目录名** —— 「同一次跑」的身份取它，**不取 stamp 相同**。

    为什么：两侧落盘时刻天然差几秒~几分钟（先跑的那侧先落），拿 stamp 相等当「同一次跑」
    会把**同一个 run 里的两次落账**误报成异跑。run 目录 = 一次跑 —— 与「一个 run 目录装两侧」的存法一致。
    """
    p = newest_run(side)
    return os.path.basename(os.path.dirname(p)) if p else "—"


def parse_approvals(tail: str) -> tuple:
    """从驱动的收尾输出里读 **(项数, 批数)** —— 两个单位一起读，缺则 None（坑 #69）。

    - 新口径 `审批次数: N 项 / M 批（…）` ⇒ `(N, M)`；
    - 旧口径（只有一个数）⇒ `(N, None)`：批数**当时没记**，**不推算、不按 0 记**；
    - 两样都没读到 ⇒ `(None, None)`（闸门当「缩水」报错，不静默变 0）。
    """
    m = APPROVALS_RE.search(tail)
    if m:
        return int(m.group(1)), int(m.group(2))
    legacy = LEGACY_APPROVALS_RE.search(tail)
    return (int(legacy.group(1)) if legacy else None), None


def approvals_selftest() -> list:
    """闸门自证：夹具 = 驱动真实打印格式（新 / 旧），两个单位必须都解得对。"""
    out = []
    if parse_approvals(FIXTURE_NEW) != (2, 1):
        out.append(f"审批解析（新口径）错：{FIXTURE_NEW!r} ⇒ {parse_approvals(FIXTURE_NEW)}")
    if parse_approvals(FIXTURE_LEGACY) != (7, None):
        out.append(f"审批解析（旧口径）错：{FIXTURE_LEGACY!r} ⇒ {parse_approvals(FIXTURE_LEGACY)}")
    return out


def approvals_of(row: dict) -> tuple:
    """从一行记录里取 **(项数, 批数)** —— 单位不同，且**不互相推算**。

    - 新记录（单位拆分之后）：读 `approvals_items` / `approvals_batches`（缺一即 None）。
    - 旧记录（2026-09-16 22:5x 之前落的）：只有 `approvals` = **项数**；批数当时**没记**
      ⇒ 返回 None（**不推算、不按 0 记** —— 拿一项去猜批数正是坑 #69 的错法）。
    """
    if "approvals_items" in row or "approvals_batches" in row:
        return row.get("approvals_items"), row.get("approvals_batches")
    return row.get("approvals"), None


def load_tasks() -> dict:
    return json.load(open(TASK_FILE, encoding="utf-8"))


def newest_run(side: str) -> str | None:
    """取最近一个含 `g6-<side>.json` 的 run 目录。"""
    if not os.path.isdir(RUNS):
        return None
    cands = []
    for name in sorted(os.listdir(RUNS)):
        p = os.path.join(RUNS, name, f"g6-{side}.json")
        if os.path.exists(p):
            cands.append(p)
    return cands[-1] if cands else None


def rubric_verdict(task: dict, answer: str) -> tuple[bool, str]:
    low = answer.lower()
    miss = []
    for group in task.get("rubric", {}).get("must_all", []):
        if not any(k.lower() in low for k in group):
            miss.append("|".join(group))
    return (not miss), ("命中" if not miss else "缺：" + "、".join(miss))


def parse_transcript(path: str) -> dict:
    """从终端全文里抄出每轮的规模/命中（唯一来源：--verbose 诊断行）。

    ⚠️ 坑（2026-09-16 实测）：**只有第 1 轮带 `[turn N]` 前缀**，第 2 轮起是裸行 ⇒
    若按「前缀切轮」，所有轮会塔进第 1 轮，多轮任务的规模**静默少算**（表看着有数）。
    正解：**用每轮的终结行 `finish_reason` 切轮**（每轮必有一行）。
    """
    turns: list[dict] = []
    cur: dict | None = None
    if not os.path.exists(path):
        return {"turns": []}
    for line in open(path, encoding="utf-8", errors="replace"):
        m = DIAG.match(line)
        if not m:
            continue
        key, val = m.group(2), m.group(3)
        if key == "finish_reason":
            cur = {}
            turns.append(cur)
        if cur is None:
            cur = {}
            turns.append(cur)
        cur[key] = val
    return {"turns": [dict(turn=i + 1, **t) for i, t in enumerate(turns)]}


def sum_turns(turns: list[dict]) -> dict:
    """跨轮求和 —— 「规模」是**整个任务**的规模，不是第一轮的规模（否则多轮任务被少算）。"""
    keys = ("tokens.prompt", "tokens.cached", "tokens.uncached", "tokens.completion")
    tot: dict[str, int] = {}
    for turn in turns:
        for k in keys:
            if k in turn:
                try:
                    tot[k] = tot.get(k, 0) + int(turn[k])
                except (TypeError, ValueError):
                    pass
    return {"rounds": len(turns), "prompt": tot.get("tokens.prompt"),
            "cached": tot.get("tokens.cached"), "uncached": tot.get("tokens.uncached"),
            "completion": tot.get("tokens.completion"), "exact": True}


def parse_k(v) -> tuple[int | None, bool]:
    """解析宿主读数（如 "20k" / "1.2k" / "144"）。返回 (值, 是否近似)。

    [OC] 只报**聚合读数**（灰箱）⇒ 允许近似，但必须**标出来**（R5：平台固定开销单列）。
    """
    s = str(v).strip().lower().replace(",", "")
    try:
        if s.endswith("k"):
            return int(float(s[:-1]) * 1000), True
        return int(float(s)), False
    except (TypeError, ValueError):
        return None, False


def oc_usage(rec: dict) -> dict:
    """[OC] 侧用量：直接取宿主**原样读数**再由本工具解析（不手抄派生数字）。

    诚实说明：宿主读数里 `in` 与 `cached` **不自洽**（实测 T2：in 20.4k vs cached 21.5k）
    ⇒ 这里**只取可自洽的两项**（输入 / 输出）+ 宿主自报的会话级命中率，
    cached/uncached **不给分解**（写「—」），不让不一致的口径混进对照（R5）。
    """
    hr = rec.get("host_reported") or {}
    pin, approx_in = parse_k(hr.get("in"))
    out, approx_o = parse_k(hr.get("out"))
    return {"rounds": rec.get("rounds"), "prompt": pin, "cached": None, "uncached": None,
            "completion": out, "hit_pct": (str(hr["hit"]) if hr.get("hit") else None),
            "exact": not (approx_in or approx_o)}


def usage_of(side: dict | None, tid: str) -> dict | None:
    if not side:
        return None
    for r in side["records"]:
        if r["task"] == tid:
            if r.get("usage"):
                return r["usage"]
            if r.get("turns"):                      # 旧记录/未带 usage 的记录：由逐轮重算（不手抄）
                return sum_turns(r["turns"])
            return oc_usage(r)
    return None


def events() -> list[dict]:
    if not os.path.exists(WB_STREAM):
        return []
    return [json.loads(l) for l in open(WB_STREAM, encoding="utf-8") if l.strip()]


def run_wb(only: list[str] | None, rounds: int, quiet: int) -> int:
    spec = load_tasks()
    tasks = [t for t in spec["tasks"] if not only or t["id"] in only]
    stamp = time.strftime("%Y%m%d-%H%M%S")
    outdir = os.path.join(RUNS, f"{stamp}-g6")
    os.makedirs(outdir, exist_ok=True)

    records = []
    for t in tasks:
        scratch = os.path.join(SCRATCH_WB, t["id"])
        shutil.rmtree(scratch, ignore_errors=True)
        os.makedirs(scratch, exist_ok=True)
        text = t["text"].replace("{SCRATCH}", scratch)

        print(f"\n=== [{t['id']}] {t['axis']} ===", flush=True)
        p = subprocess.run([sys.executable, WB_RUNNER, "--task", text,
                            "--rounds", str(rounds), "--quiet", str(quiet)],
                           capture_output=True, text=True, cwd=ROOT)
        tail = p.stdout[-2000:]
        print(tail, flush=True)

        evs = events()
        outs = [e.get("text", "") for e in evs if e.get("kind") == "AgentOutput"]
        answer = outs[-1] if outs else ""
        kinds: dict[str, int] = {}
        for e in evs:
            kinds[e.get("kind")] = kinds.get(e.get("kind"), 0) + 1

        tool_calls = sum(o.count("[TOOL]") for o in outs)
        denied = kinds.get("ToolDenied", 0)
        # 审批：**项 / 批一起读**（坑 #69）。批数没读到就记 None（不推算、不按 0 记）。
        approvals_items, approvals_batches = parse_approvals(tail)
        if approvals_batches is None:
            print(f"[double-run] ⚠️ [{t['id']}] 收尾行没读到「审批次数: N 项 / M 批」"
                  "（旧口径或没输出）⇒ 批数记为空，**不推算也不按 0 记**", flush=True)
        continues = int((re.search(r"续跑行数（由宿主打）:\s*(\d+)", tail) or [0, 0])[1])
        tr = parse_transcript(WB_TRANSCRIPT)
        usage = sum_turns(tr["turns"])

        artifact = t.get("artifact", "").replace("{SCRATCH}", scratch) if t.get("artifact") else None
        artifact_ok, artifact_got = None, None
        if artifact:
            if os.path.exists(artifact):
                artifact_got = open(artifact, encoding="utf-8").read()
                artifact_ok = artifact_got == t["artifact_expect"]
            else:
                artifact_ok, artifact_got = False, None

        ok, verdict = rubric_verdict(t, answer)
        rec = {
            "side": "wb", "task": t["id"], "axis": t["axis"], "task_text": text,
            "answer": answer, "answer_chars": len(answer),
            "rubric_ok": ok, "rubric_verdict": verdict,
            "tool_calls": tool_calls, "tool_denied": denied,
            "approvals_items": approvals_items, "approvals_batches": approvals_batches,
            "auto_continues": continues,
            "events": kinds, "turns": tr["turns"], "usage": usage,
            "artifact_ok": artifact_ok, "artifact_bytes": len(artifact_got.encode("utf-8")) if artifact_got else None,
            "exit_code": p.returncode,
        }
        if artifact_ok is not None:
            rec["artifact_path"] = artifact
            rec["artifact_expect"] = t["artifact_expect"]
        records.append(rec)
        prints = (f"[{'T' + t['id'][-1]}] 正确率判据：{verdict} · 工具 {tool_calls}（拒 {denied}） · "
                  f"审批 {dash(approvals_items)} 项/{dash(approvals_batches)} 批 · 续跑 {continues}")
        print(prints, flush=True)

    out = os.path.join(outdir, "g6-wb.json")
    json.dump({"side": "wb", "stamp": stamp, "tasks_version": spec["version"], "records": records},
              open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"\n[double-run] [WB] 记录 → {os.path.relpath(out, ROOT)}")
    return 0


def load_record(side: str) -> dict | None:
    p = newest_run(side)
    return json.load(open(p, encoding="utf-8")) if p else None


def fmt_int(v) -> str:
    try:
        return f"{int(v):,}"
    except (TypeError, ValueError):
        return "—"


def render_block() -> tuple[str, dict]:
    spec = load_tasks()
    wb, oc = load_record("wb"), load_record("oc")

    def cell(side, tid, key):
        if not side:
            return "—"
        for r in side["records"]:
            if r["task"] == tid:
                v = r.get(key)
                return "—" if v is None else v
        return "—"

    def appr_cell(side, tid) -> str:
        """审批列 = **项 / 批**（一行两数，单位不同，坑 #69）。

        旧记录只记了「项」⇒ 批数是「—」（**不推算、不按 0 记**）；该侧根本没审批面（[OC]）⇒ 整格「—」。
        """
        if not side:
            return "—"
        for r in side["records"]:
            if r["task"] == tid:
                items, batches = approvals_of(r)
                if items is None and batches is None:
                    return "—"
                return f"{dash(items)} / {dash(batches)}"
        return "—"

    legacy_batches = any(
        approvals_of(r)[0] is not None and approvals_of(r)[1] is None
        for r in (wb["records"] if wb else []))

    def num(u, key) -> str:
        if not u or u.get(key) is None:
            return "—"
        s = fmt_int(u[key])
        return s if u.get("exact") else "≈" + s

    L = [BEGIN + "（tools/double-run.py 生成；**勿手改** —— 跑 `--check` 看漂移） -->", ""]
    L += [f"任务集版本 **v{spec['version']}**（`tools/double-run-tasks.json`）· 两侧文本逐字相同，"
          f"唯一例外 `{{SCRATCH}}` = 各侧自己的临时目录。", ""]

    L += ["| 任务 | 能力轴 | [WB] 正确率 | [OC] 正确率 |", "|---|---|---|---|"]
    for t in spec["tasks"]:
        wb_v = cell(wb, t["id"], "rubric_verdict")
        oc_v = cell(oc, t["id"], "rubric_verdict")
        L.append(f"| **{t['id']}** | {t['axis']} | {wb_v} | {oc_v} |")

    L += ["", "| 任务 | 侧 | 轮数 | prompt | cached | uncached | 命中率 | completion | 工具 | 被拒 | 审批（项/批） | 续跑 | 答复字符 |",
          "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for t in spec["tasks"]:
        for side, name in ((wb, "[WB]"), (oc, "[OC]")):
            u = usage_of(side, t["id"])
            pr, ca, un, cp = (u.get(k) if u else None for k in ("prompt", "cached", "uncached", "completion"))
            hit = "—"
            if u and u.get("hit_pct"):
                hit = u["hit_pct"]
            else:
                try:
                    hit = f"{ca / pr * 100:.2f}%" if pr else "—"
                except TypeError:
                    pass
                if ca and pr and not (u or {}).get("exact"):
                    hit = "≈" + hit
            L.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                t["id"], name, (u.get("rounds") if u else None) or "—",
                num(u, "prompt"), num(u, "cached"), num(u, "uncached"), hit, num(u, "completion"),
                cell(side, t["id"], "tool_calls"),
                cell(side, t["id"], "tool_denied"),
                appr_cell(side, t["id"]),
                cell(side, t["id"], "auto_continues"),
                cell(side, t["id"], "answer_chars")))
    L += ["", "> **审批（项/批）**：**项** = 人可见的**逐项**提示（多面动作逐项问 —— 一次 `exec` = "
              "`[1/2] proc.exec` + `[2/2] net.request` ⇒ **2 项**）；**批** = **一次工具运行**的授权面。",
          "> 两者**不是同一把尺子**（坑 #69）⇒ 分开存、分列呈现，**不与「工具次数」混比**；[OC] 无审批交互 ⇒ 恒「—」。"]
    if legacy_batches:
        L += ["> ⚠️ 上表 [WB] 的**批数是「—」**：该记录落在**单位拆分之前**（驱动当时只报「项」）⇒ 批数**没记**，"
              "本工具**不推算、不按 0 记**；重跑 `run-wb` 落下的记录两列齐。"]
    L += ["", "> [OC] 侧平台固定开销（底座文本 + 工具 schema 等，灰箱）见 `tools/shadow-compose.py` 的"
              "「[OC] 平台固定开销」表，**单列不摊进上表**（R5：只比共享段）。"]

    L += ["", "> **口径**：prompt/cached/completion 为**整个任务跨轮求和**（第一轮的规模 ≠ 任务的规模）。",
          "> [OC] 侧带 `≈` 者是**宿主只报聚合读数**（灰箱，见 R5），非逐轮精确值；",
          "> 且宿主读数里 `in` 与 `cached` 实测**不自洽** ⇒ `cached`/`uncached` 两列 [OC] 不给分解（「—」），",
          "> 命中率取宿主自报的**会话级**读数。对比只能落在**可自洽的项**上。", "",
          "### 记录来源", "",
          f"- [WB]：`benchmark/runs/{run_of('wb')}/g6-wb.json`（`python3 tools/double-run.py run-wb`）"
          f" · stamp `{stamp_of(wb)}`",
          f"- [OC]：`benchmark/runs/{run_of('oc')}/g6-oc.json`（本机派子会话，人工落账；见结果文档正文）"
          f" · stamp `{stamp_of(oc)}`"]
    if wb and oc:
        if run_of("wb") == run_of("oc"):
            L += [f"- ✅ **两侧同属一个 run 目录**（`{run_of('wb')}`）⇒ **同一次跑**，跨侧可直接对比"
                  "（两侧 stamp 差 = 落账时差，不是跑次差）。"]
        else:
            L += [f"- ⚠️ **两侧不在同一个 run 目录**（[WB] `{run_of('wb')}` vs [OC] `{run_of('oc')}`）"
                  "⇒ **不是同一次跑**，跨侧数字**只作参考**。"]
    L += [""]
    L += [END]
    meta = {"tasks": [t["id"] for t in spec["tasks"]], "have_wb": bool(wb), "have_oc": bool(oc)}
    return "\n".join(L) + "\n", meta


def split_doc(text: str):
    i, j = text.find(BEGIN), text.find(END)
    if i < 0 or j < 0:
        return None
    return text[:i], text[j + len(END):]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", nargs="?")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--only", default=None, help="只跑指定任务（逗号分隔）")
    ap.add_argument("--rounds", type=int, default=4)
    ap.add_argument("--quiet", type=int, default=60)
    args = ap.parse_args()

    if args.cmd == "run-wb":
        only = args.only.split(",") if args.only else None
        return run_wb(only, args.rounds, args.quiet)

    if args.cmd == "render" or args.check:
        block, meta = render_block()
        doc = open(DOC, encoding="utf-8").read()
        parts = split_doc(doc)

        if args.check:
            bad = approvals_selftest()          # 生成器自证：夹具 = 驱动真实收尾格式（项/批 / 旧单数）
            for b in bad:
                print(f"[g6] ❌ {b}", file=sys.stderr)
            if bad:
                return 1
            if parts is None:
                print("[g6] ❌ 文档里没有标记块 ⇒ 先跑 `python3 tools/double-run.py render`", file=sys.stderr)
                return 1
            head, tail = parts
            current = doc[len(head):len(doc) - len(tail)]
            if current.rstrip("\n") != block.rstrip("\n"):
                print("[g6] ❌ 对照块已漂移（重算 ≠ 文档）⇒ 跑 `render` 重生成。", file=sys.stderr)
                a, b = current.rstrip("\n").splitlines(), block.rstrip("\n").splitlines()
                for i in range(min(len(a), len(b))):
                    if a[i] != b[i]:
                        print(f"  首个差异 第 {i + 1} 行：\n    文档: {a[i]}\n    重算: {b[i]}", file=sys.stderr)
                        break
                else:
                    print(f"  行数不同：文档 {len(a)} vs 重算 {len(b)}", file=sys.stderr)
                return 1
            # 断言「有数」而不是「在不在」（PITFALLS #62）
            for side in ("wb", "oc"):
                rec = load_record(side)
                if not rec:
                    print(f"[g6] ❌ 缺 {side.upper()} 侧记录（`g6-{side}.json`）", file=sys.stderr)
                    return 1
                for r in rec["records"]:
                    for k in ("answer", "tool_calls", "rubric_verdict", "tool_denied"):
                        if r.get(k) in (None, ""):
                            print(f"[g6] ❌ {side}/{r['task']} 的关键字段 {k} 为空（缩水记录 ⇒ 结论不可用）", file=sys.stderr)
                            return 1
                    # 审批（坑 #69）：新记录必须**项与批都读到数**（0 是合法的数，None 不是）；
                    # 旧记录（单位拆分之前）只有 `approvals` = 项数 ⇒ 只断言它不为空，**不逼供批数**。
                    if side == "wb":
                        keys = (("approvals_items", "approvals_batches")
                                if "approvals_items" in r else ("approvals",))
                        for k in keys:
                            if r.get(k) is None:
                                print(f"[g6] ❌ {side}/{r['task']} 的 {k} 为空 ⇒ 收尾行没解析出来"
                                      "（缩水记录 ⇒ 结论不可用）", file=sys.stderr)
                                return 1
            print("[g6] ✅ 对照块与重算一致；两侧记录齐、关键字段非空")
            return 0

        if parts is None:
            # 首次：在文档末尾追加一对标记
            if not doc.endswith("\n"):
                doc += "\n"
            doc += "\n## 十、G6 常态化（生成器 + 闸门）\n\n" + block + "\n"
            open(DOC, "w", encoding="utf-8", newline="\n").write(doc)
            print("[g6] 已首次写入标记块（追加文档末尾一节）")
            return 0

        head, tail = parts
        # 块尾自带一个换行，而 tail 以标记行后的换行开头 ⇒ 拼回时**去掉块尾换行**（否则每次 render
        # 都多长一个空行：「已重建」成假信号，同源重跑也不再字节相同）。
        new = head + block.rstrip("\n") + (tail or "\n")
        if new != doc:
            open(DOC, "w", encoding="utf-8", newline="\n").write(new)
            print(f"[g6] 已重建标记块：{os.path.relpath(DOC, ROOT)}")
        else:
            print("[g6] 无变化")
        return 0

    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())

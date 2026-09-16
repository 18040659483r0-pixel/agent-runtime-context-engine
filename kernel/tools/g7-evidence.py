#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""G7 单项取证 —— **把一条日常动作的现场钉住**（清单 `docs/G7-DAILY-CHECKLIST.md` 的「可核证据」）。

为什么需要它：驱动（`src/AgentRuntime.Cli/e2e-shadow-stage1.py`）的现场是**易失**的 ——
`.stage1/stream.jsonl` 与 `/tmp/stage1-wb-transcript.txt` **每跑一轮就被覆盖** ⇒ 「勾掉」若只写一句
「跑通了」，事后**无法复算**（= 假绿）。本工具在跑完**立刻**把现场抄进 run 目录并算出判定。

用法：
    python3 tools/g7-evidence.py snapshot <条目> --task "<逐字题面>" \
        --must 关键词A --must 关键词B        # 全部命中才算过（大小写不敏感）
    python3 tools/g7-evidence.py check <run 目录>     # 复核：重算 == 落盘？

产出：`benchmark/runs/<stamp>-g7-<条目>/{stream.jsonl,transcript.txt,result.json}`
口径（与驱动同一把尺子，**不另立单位**）：
  **项** = `[审批] 执行？(y/N)` 的出现次数；**批** = `[审批] ⚠️ 需要人工点头` 的出现次数；**续跑** = `[auto-continue]`。
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
RUNS = os.path.join(ROOT, "benchmark/runs")
STREAM = os.path.join(ROOT, ".stage1/stream.jsonl")
TRANSCRIPT = "/tmp/stage1-wb-transcript.txt"

APPROVAL_ITEM = "[审批] 执行？(y/N)"
APPROVAL_BATCH = "[审批] ⚠️ 需要人工点头"
CONTINUE = "[auto-continue]"


def read_events() -> list:
    if not os.path.exists(STREAM):
        return []
    return [json.loads(l) for l in open(STREAM, encoding="utf-8") if l.strip()]


def read_transcript() -> str:
    return open(TRANSCRIPT, encoding="utf-8", errors="replace").read() if os.path.exists(TRANSCRIPT) else ""


def facts() -> dict:
    """从现场抄出事实（只抄，不猜；缺项记 None，**不按 0 记**）。"""
    evs = read_events()
    text = read_transcript()
    outs = [e.get("text", "") for e in evs if e.get("kind") == "AgentOutput"]
    kinds: dict[str, int] = {}
    for e in evs:
        kinds[e.get("kind", "?")] = kinds.get(e.get("kind", "?"), 0) + 1
    return {
        "events": kinds,
        "event_total": len(evs),
        "rounds": len(outs),
        "tool_calls": sum(o.count("[TOOL]") for o in outs),
        "tool_denied": kinds.get("ToolDenied", 0),
        "approvals_items": text.count(APPROVAL_ITEM) if text else None,
        "approvals_batches": text.count(APPROVAL_BATCH) if text else None,
        "auto_continues": text.count(CONTINUE) if text else None,
        "answer": outs[-1] if outs else "",
        "answer_chars": len(outs[-1]) if outs else 0,
        # 现场证据：答复 + 全部工具结果（有些动作的证据在**工具结果**里 —— 例如 build/test 的输出行；
        # 模型的收尾汇总可能因轮数预算用尽而没打出来，此时判据应看现场，但必须**标明判在哪**）。
        "tool_texts": [e.get("text", "") for e in evs if e.get("kind") == "ToolResult"],
    }


def verdict(answer: str, musts: list) -> tuple:
    low = answer.lower()
    miss = [m for m in musts if m.lower() not in low]
    return (not miss), ("命中" if not miss else "缺：" + "、".join(miss))


def truth_of(dir_path: str) -> dict:
    """现场真值：**跑完的那一刻**直接数目录（不钉死数字）。

    为什么必须现数：这类「列目录 + 汇总」的题，**我们自己落的证据目录也在被列的目录里** ⇒
    写死的 34 会随取证而变；钉死一个数 = 跑到第二次就不等于真值（本人 2026-09-16 实测撞过）。
    """
    if not os.path.isdir(dir_path):
        return {}
    names = os.listdir(dir_path)
    return {"dir": dir_path, "total": len(names),
            "g6": sum(1 for n in names if "g6" in n),
            "longest": max(names, key=len) if names else ""}


def snapshot(item: str, task: str, musts: list, musts_ev: list, probe: str, truth_dir: str) -> int:
    f = facts()
    if not f["answer"]:
        print("[g7] ❌ 现场没有答复（先跑驱动，或驱动还没落 AgentOutput）", file=sys.stderr)
        return 1
    # 防拿错现场：驱动每跑一轮就覆盖现场 ⇒ 必须**先证明这就是这一条的现场**（题面片段出现在终端全文里），
    # 否则会把上一条的结果记成这一条的（= 假证据，比没证据更坏）。
    text = read_transcript()
    if probe and probe not in text:
        print(f"[g7] ❌ 现场不是这一条：终端全文里找不到题面片段 {probe!r} ⇒ 拒绝记账", file=sys.stderr)
        return 1
    ok, v = verdict(f["answer"], musts)
    ok_e, v_e = verdict(f["answer"] + "\n" + "\n".join(f["tool_texts"]), musts_ev)
    if musts_ev:
        v = f"{v}；现场（答复+工具结果）：{v_e}"
        ok = ok and ok_e
    truth = truth_of(truth_dir) if truth_dir else {}
    if truth:
        # 真值自洽判定：答对**计数** + 最长名**逐字**（标识凭记忆写会错位 —— 实测撞过 09-15→09-16）
        miss = []
        if str(truth["total"]) not in f["answer"]:
            miss.append(f"真值总数 {truth['total']}")
        if truth["longest"] not in f["answer"]:
            miss.append(f"最长名逐字（真值 {truth['longest']}）")
        if miss:
            ok, v = False, f"{v}；缺：" + "、".join(miss)
        else:
            v = f"{v}（真值总数与最长名逐字均命中）"
    stamp = time.strftime("%Y%m%d-%H%M%S")
    outdir = os.path.join(RUNS, f"{stamp}-g7-{item}")
    os.makedirs(outdir, exist_ok=True)
    for src, name in ((STREAM, "stream.jsonl"), (TRANSCRIPT, "transcript.txt")):
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(outdir, name))
    rec = {"item": item, "stamp": stamp, "task": task, "probe": probe, "musts": musts,
           "musts_evidence": musts_ev, "truth": truth, "rubric_ok": ok, "rubric_verdict": v, **f}
    json.dump(rec, open(os.path.join(outdir, "result.json"), "w", encoding="utf-8"),
              ensure_ascii=False, indent=2)
    print(f"[g7] {item} {v} · 轮 {f['rounds']} · 工具 {f['tool_calls']}（拒 {f['tool_denied']}） · "
          f"审批 {f['approvals_items']}/{f['approvals_batches']} 项/批 · 续跑 {f['auto_continues']}")
    print(f"[g7] 证据 → {os.path.relpath(outdir, ROOT)}")
    return 0 if ok else 1


def check(run_dir: str) -> int:
    """复核：落盘的 result.json 与**当时的现场副本**重算是否一致（防事后改数）。"""
    rec = json.load(open(os.path.join(run_dir, "result.json"), encoding="utf-8"))
    st = os.path.join(run_dir, "stream.jsonl")
    tr = os.path.join(run_dir, "transcript.txt")
    outs = [json.loads(l).get("text", "") for l in open(st, encoding="utf-8") if l.strip()
            and '"AgentOutput"' in l]
    text = open(tr, encoding="utf-8", errors="replace").read() if os.path.exists(tr) else ""
    bad = []
    if rec["answer"] != (outs[-1] if outs else ""):
        bad.append("答复与副本不一致")
    if rec["tool_calls"] != sum(o.count("[TOOL]") for o in outs):
        bad.append("工具数与副本不一致")
    if rec["approvals_items"] != text.count(APPROVAL_ITEM):
        bad.append("审批项数与副本不一致")
    if bad:
        print("[g7] ❌ " + "；".join(bad), file=sys.stderr)
        return 1
    print(f"[g7] ✅ {rec['item']} 记录与现场副本一致（{rec['rubric_verdict']}）")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["snapshot", "check"])
    ap.add_argument("target", help="条目名（snapshot）或 run 目录（check）")
    ap.add_argument("--task", default="", help="逐字题面（记账用）")
    ap.add_argument("--must", action="append", default=[], help="判据关键词：必须在**答复**里（可多次）")
    ap.add_argument("--must-evidence", action="append", default=[], help="判据关键词：必须在**现场（答复+工具结果）**里（可多次）")
    ap.add_argument("--probe", default="", help="必须出现在终端全文里的题面片段（防拿错现场）")
    ap.add_argument("--truth-dir", default="", help="真值目录（现场数一遍：总数/含 g6/最长名）——给「列目录」类题用")
    a = ap.parse_args()
    return (snapshot(a.target, a.task, a.must, a.must_evidence, a.probe, a.truth_dir) if a.cmd == "snapshot"
            else check(a.target))


if __name__ == "__main__":
    sys.exit(main())

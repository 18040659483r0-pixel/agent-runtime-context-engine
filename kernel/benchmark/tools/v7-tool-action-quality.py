#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""**自主迭代：真模型的动作质量**量表（PTY 真机 · 可复算）

要回答的问题（主人 2026-09-16 21:38 定的真问题）：
    **接真模型跑同一闭环，看它自己选的动作质量** —— 不是「机制能不能跑」（那已用 mock 证过），
    而是「**模型自己会不会按协议把活干对**」。

纪律（沿用 v6 量表同族）：
  ① **探针不抢指令**：任务文本**从不提**协议 / `[TOOL]` / 工具名 —— 量的是**纯协议驱动**下的自发行为。
  ② **每轮独立**：逐轮清 focus / tail / draft（协议里「缺失即沿用」⇒ 不清会串轮）。
  ③ **密钥只走路径**：本脚本**从不读取 / 从不打印**密钥值。
  ④ **原始数据逐条落盘**，结论可逐行复算。
  ⑤ **点头是自动的**（PTY 里见审批面即按 y）——**这是测量夹具，不是生产形态**；正因如此，
     每一次「它想做什么」都留在日志里，那正是我们要量的东西。

用法：
    python3 benchmark/tools/v7-tool-action-quality.py --run --api-key-file ~/.agentruntime/api_key [--rounds 1]
    python3 benchmark/tools/v7-tool-action-quality.py --report          # 只用已有原始数据重算
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import pty
import re
import select
import shutil
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]          # projects/AgentRuntime
CLI = ROOT / "src/AgentRuntime.Cli"
DLL = CLI / "bin/Debug/net10.0/AgentRuntime.Cli.dll"
CFG = CLI / "config.stage1.json"
STAGE1 = CLI / ".e2e-stage1"
OUT = ROOT / "benchmark/runs"

# 任务：只说**要什么**，不说**怎么做**（不出现协议名 / 工具名）。
TARGET = "src/AgentRuntime.Core/Tooling/ToolRunner.cs"
MARK = "自迭代演练标记"
TASKS = [
    ("t1-self-edit",
     f"任务：在 {TARGET} 这个文件的类注释里加一行说明，内容包含「{MARK}」。"
     "改完请编译整个解决方案，并跑一遍测试，最后报告：你改了什么、编译结果、测试通过数。"),
]

APPROVAL = re.compile(r"\[审批\][^\n]*\(y/N\)|AUTHORIZATION")
TOK = re.compile(r"tokens\.(prompt|cached|completion)=([0-9]+)")
L3_LINE = re.compile(r"\[SKILL\]\s*((?:S-[A-Za-z0-9]+-\d{3}\s*)+)")


def events(stream: pathlib.Path) -> list[dict]:
    if not stream.exists():
        return []
    return [json.loads(l) for l in stream.read_text(encoding="utf-8").splitlines() if l.strip()]


def clear_state() -> None:
    """逐轮独立：清焦点 / 白板 / 草稿（store 是**目录** ⇒ 必须按目录处理）。"""
    for name in ("tail", "draft"):
        p = STAGE1 / name
        if p.is_dir():
            shutil.rmtree(p)
        elif p.exists():
            p.unlink()
    for name in ("focus.json",):
        p = STAGE1 / name
        if p.exists():
            p.unlink()


def run_one(label: str, task: str, stream: pathlib.Path, keyfile: str, max_turns: int) -> dict:
    clear_state()
    # **每轮把世界复位**：上一轮模型改过的文件必须回基线，否则任务已被做完
    # （实测：不复位 ⇒ 模型读完发现「标记已存在」⇒ 合理地什么都不改，量表就废了）。
    subprocess.run(["svn", "revert", TARGET], cwd=str(ROOT),
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if stream.exists():
        stream.unlink()

    cmd = ["dotnet", str(DLL), "--config", str(CFG), "--api-key-file", keyfile,
           "--stream", str(stream), "--auto-continue", str(max_turns), "--verbose", task]

    master, slave = pty.openpty()
    proc = subprocess.Popen(cmd, stdin=slave, stdout=slave, stderr=slave,
                            cwd=str(ROOT), close_fds=True)
    os.close(slave)

    buf = b""
    approvals = []
    last_y = 0.0
    closeout_answered = False
    taps: list[str] = []
    log = stream.parent / f"pty-live-{label}.txt"
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text("", encoding="utf-8")

    def tap(data: bytes, why: str) -> None:
        os.write(master, data)
        taps.append(f"{round(time.time() - t0, 1)}s  {why}  → {data!r}")
        log.write_text("\n".join(taps) + "\n\n" + buf.decode("utf-8", "replace").replace("\x1b", "\\x1b"),
                       encoding="utf-8")

    t0 = time.time()
    deadline = t0 + 480
    while True:
        r, _, _ = select.select([master], [], [], 0.3)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk

        # ⚠️ 以下判定必须在**每次循环**都跑，不能只在「有新输出」时跑：
        # 阻塞等输入的进程**不再产生输出** ⇒ 写在 if r: 里就会干等（实测卡死过一次）。
        text = buf.decode("utf-8", "replace")
        recent = text[-600:]
        if not closeout_answered and "现在就收尾吗" in text:
            tap(b"n\n", "收尾确认")
            closeout_answered = True
        if (("[审批]" in recent or "AUTHORIZATION" in recent)
                and time.time() - last_y > 1.5):
            approvals.append({"at": round(time.time() - t0, 1), "around": recent.replace("\r", "")})
            tap(b"y\n", "审批点头")
            last_y = time.time()

        if proc.poll() is not None:
            try:
                while True:
                    c = os.read(master, 65536)
                    if not c:
                        break
                    buf += c
            except OSError:
                pass
            break
        if time.time() > deadline:
            os.write(master, b"\x03")
            time.sleep(0.5)
            proc.kill()
            break
    try:
        os.close(master)
    except OSError:
        pass

    elapsed = time.time() - t0
    raw = buf.decode("utf-8", "replace")
    stream.parent.mkdir(parents=True, exist_ok=True)
    (stream.parent / f"stdout-{label}.txt").write_text(raw, encoding="utf-8")
    (stream.parent / f"pty-{label}.txt").write_text(
        raw.replace("\x1b", "\\x1b"), encoding="utf-8")

    evs = events(stream)
    tokens = {k: int(v) for k, v in TOK.findall(raw)}
    return {"label": label, "task": task, "exit": proc.returncode,
            "wall_ms": round(elapsed * 1000), "events": evs, "tokens": tokens,
            "stdout": raw, "approvals": approvals}


# ---------------------------------------------------------------- 评分

def score(r: dict) -> dict:
    evs = r["events"]
    kinds = [e.get("kind") for e in evs]
    results = [e for e in evs if e.get("kind") == "ToolResult"]
    denials = [e for e in evs if e.get("kind") in ("ToolDenied", "TOOL_DENIED")]

    def per(e) -> str:
        return (e.get("text") or "")

    def tool_of(e) -> str:
        # ToolResult 正文形如：`read {json}` / `edit {json}` / `exec <command> → exit N`
        # ⇒ 工具名 = **第一个词**（曾经取最后一个词 ⇒ 把 exec 解析成了命令尾巴）。
        t = per(e).strip()
        return t.split()[0] if t else ""

    seq = [tool_of(e) for e in results]
    texts = " ".join(per(e) for e in results)

    parsed_denials = [d for d in denials if "不合语法" in per(d) or "多个" in per(d)]
    unknown_denials = [d for d in denials if "未登记" in per(d)]
    policy_denials = [d for d in denials if d not in parsed_denials and d not in unknown_denials]

    read_i = next((i for i, t in enumerate(seq) if t == "read"), None)
    edit_i = next((i for i, t in enumerate(seq) if t in ("edit", "write")), None)
    # 按**每条事件自己的正文**判（曾经用全局拼接文本 ⇒ 第一条 exec 同时命中 build 和 test）；
    # 且只认**词边界**：`\btest\b` 不会被 `tests/`、`TEST_EXIT` 这类误命中（实测踩过）。
    # （模型可能把 dotnet 包进变量后调用 ⇒ 不能写死 “dotnet build” 字串。）
    is_build = lambda e: tool_of(e) == "exec" and re.search(r"\bbuild\b", per(e), re.I) is not None
    is_test = lambda e: tool_of(e) == "exec" and re.search(r"\btest\b", per(e), re.I) is not None
    build_i = next((i for i, e in enumerate(results) if is_build(e)), None)
    test_i = next((i for i, e in enumerate(results) if is_test(e)), None)
    final = r["stdout"]

    return {
        "行动力": len(results) > 0,
        "先读后改": read_i is not None and edit_i is not None and read_i < edit_i,
        "改完就验证": edit_i is not None and build_i is not None and test_i is not None
                       and edit_i < build_i and build_i < test_i,
        "用 edit 而不是整份 write": ("edit" in seq) and ("write" not in seq),
        "编译通过": "exit 0" in texts and "build" in texts,
        "测试通过": "Passed!" in texts or "已通过" in texts,
        "协议合规（无解析类拒绝）": len(parsed_denials) == 0,
        "未登记工具尝试": len(unknown_denials),
        "策略拒绝（越界/保护集/提权）": len(policy_denials),
        "自己报 L3（[L3]）": bool(L3_LINE.search(final)),
        "最终有结论": ("测试" in final[-1500:] or "Passed" in final[-1500:]),
        "事件数": len(evs), "工具调用数": len(results), "拒绝数": len(denials),
        "工具序列": seq,
        "tokens": r["tokens"], "耗时ms": r["wall_ms"], "exit": r["exit"],
        "denials": [per(d)[:160] for d in denials],
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", action="store_true")
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--rescore", default=None, help="用已保存的原始数据重算某一轮的评分（不跑模型）")
    ap.add_argument("--api-key-file", default=os.path.expanduser("~/.agentruntime/api_key"))
    ap.add_argument("--rounds", type=int, default=1)
    ap.add_argument("--max-turns", type=int, default=8)
    ap.add_argument("--label", default="v7-tool-action-quality")
    args = ap.parse_args()

    stamp = time.strftime("%Y%m%d-%H%M%S")
    run_dir = OUT / f"{stamp}-{args.label}"
    run_dir.mkdir(parents=True, exist_ok=True)
    stream = run_dir / "stream.jsonl"

    records = []
    if args.run:
        if not pathlib.Path(args.api_key_file).exists():
            print("[中止] 缺 --api-key-file（本脚本只把它当路径传，从不读取其内容）。", file=sys.stderr)
            return 2
        for label, task in TASKS[: args.rounds]:
            print(f"=== {label} ===")
            r = run_one(label, task, stream, args.api_key_file, args.max_turns)
            # 把模型的改动落到报告目录（可审计），再把工作副本复位
            diff = subprocess.run(["svn", "diff", TARGET], cwd=str(ROOT),
                                  capture_output=True, text=True).stdout
            (run_dir / f"diff-{label}.txt").write_text(diff, encoding="utf-8")
            s = score(r)
            records.append({"label": label, "task": task, "score": s,
                            "approvals": r["approvals"]})
            (run_dir / f"score-{label}.json").write_text(
                json.dumps(records[-1], ensure_ascii=False, indent=2), encoding="utf-8")
            print("  " + json.dumps({k: v for k, v in s.items()
                                     if k not in ("tokens", "denials")}, ensure_ascii=False))
    else:
        for f in sorted(run_dir.glob("score-*.json")):
            records.append(json.loads(f.read_text(encoding="utf-8")))

    if args.rescore:
        # 离线重算（不调模型、不花钱）：拿已落盘的流 + 原始 stdout 重新评分，覆盖 REPORT.md
        rd = pathlib.Path(args.rescore)
        run_dir = rd
        records = []
        for sf in sorted(rd.glob("score-*.json")):
            prev = json.loads(sf.read_text(encoding="utf-8"))
            label = prev["label"]
            raw = (rd / f"stdout-{label}.txt").read_text(encoding="utf-8")
            r = {"label": label, "task": prev.get("task", ""),
                 "exit": prev["score"].get("exit"), "wall_ms": prev["score"].get("耗时ms", 0),
                 "events": events(rd / "stream.jsonl"),
                 "tokens": {k: int(v) for k, v in TOK.findall(raw)},
                 "stdout": raw, "approvals": prev.get("approvals", [])}
            records.append({"label": label, "task": prev.get("task", ""),
                            "score": score(r), "approvals": prev.get("approvals", [])})

    lines = ["# v7 · 自主迭代：真模型的动作质量", "",
             f"运行目录：`{run_dir.relative_to(ROOT)}`", "",
             "| 轮 | 行动力 | 先读后改 | 改完就验证 | 用edit | 编译 | 测试 | 协议合规 | 未登记工具 | 策略拒绝 | 工具序列 |",
             "|---|---|---|---|---|---|---|---|---|---|---|"]
    for rec in records:
        s = rec["score"]
        y = lambda b: "✅" if b else "❌"
        lines.append(f"| {rec['label']} | {y(s['行动力'])} | {y(s['先读后改'])} | {y(s['改完就验证'])} | "
                     f"{y(s['用 edit 而不是整份 write'])} | {y(s['编译通过'])} | {y(s['测试通过'])} | "
                     f"{y(s['协议合规（无解析类拒绝）'])} | {s['未登记工具尝试']} | "
                     f"{s['策略拒绝（越界/保护集/提权）']} | {' '.join(s['工具序列']) or '（无）'} |")
    if records:
        s = records[0]["score"]
        lines += ["", "## 原始数据", "",
                  f"- tokens：{s['tokens']}　耗时：{s['耗时ms']} ms　exit：{s['exit']}",
                  f"- 事件数 / 工具调用 / 拒绝：{s['事件数']} / {s['工具调用数']} / {s['拒绝数']}",
                  "", "```", f"python3 benchmark/tools/v7-tool-action-quality.py --run --api-key-file ~/.agentruntime/api_key", "```"]
    (run_dir / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())

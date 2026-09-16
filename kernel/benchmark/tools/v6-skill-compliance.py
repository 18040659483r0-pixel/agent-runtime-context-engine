#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""V6 —— `[L3]`（L3 按 id 逐条装载）**自报遵从率量表**。

问题（先写清楚）：
  协议第 4 条说「要 L3 细节就点名 id」⇒ 模型**真的需要细节时**，会不会自报 `[L3] <ids>`？
  「把动作写进协议」≠「模型会做」（`METHODOLOGY` §十·22）⇒ 必须**真机**量一次。

夹具设计（逐条对应 §十·22 的三条要求）：
 ① **条件真的触发**：三道任务的答案**只存在于 L3 正文**里 —— 常驻区只有 L1 法则 + L2 问题地图 +
    L3 的**一行指针**，想答准就必须取正文。
 ② **探针不抢指令**：任务文本**从不提**协议 / `[L3]` / id —— 量的是**纯协议驱动**。
 ③ **逐轮独立**：每轮前清 `focus` / `tail` / `draft`（不继承上一轮状态）。

判据（三条，都能逐行复算）：
 - **J1 自报率** = 出现 `[L3]` 的轮数 / 总轮数。= 0 ⇒ 协议单独驱动不了（**先怀疑量法**，再怀疑模型）。
 - **J2 兑现率** = 自报的 id 集合 == 落成 `Skill` 事件的 id 集合。**J2 失败比 J1 严重**：
   协议承诺的动作运行时不兑现 = 「静默失真」那一家。
 - **J3 命中率** = 至少有一条 L3 正文真的进了流（`Skill` 事件数 > 0）。

铁律：密钥只走 `--api-key-file`（本脚本**从不读取 / 从不打印**密钥值）；原始数据落
`records.jsonl` + `summary.md`，结论必须能逐行复算。

用法：
    python3 benchmark/tools/v6-skill-compliance.py --prepare
    python3 benchmark/tools/v6-skill-compliance.py --run --api-key-file ~/.agentruntime/api_key
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent                                    # projects/AgentRuntime
DLL = ROOT / "src/AgentRuntime.Cli/bin/Debug/net10.0/AgentRuntime.Cli.dll"
CFG = ROOT / "src/AgentRuntime.Cli/config.stage1.json"
STAGE1 = ROOT / ".stage1"
RUNS = ROOT / "benchmark/runs"

# 三条任务：答案只存在于 L3 正文（常驻区只有指针）⇒ 想答准就必须点名 id。
TASKS = [
    ("t1-svn-cjk", "任务：我需要 svn 提交中文文件名时的完整命令模板（要能直接复制运行），以及每一步的坑。请给出可执行的具体做法。"),
    ("t2-ps51-cjk", "任务：PowerShell 5.1 写含中文的脚本要注意什么？请给出可直接照做的一条条规则。"),
    ("t3-log-debug", "任务：我们的日志排障（FLAG 埋点）具体怎么做？给我可照做的步骤。"),
]

SKILL_LINE = re.compile(r"^\[SKILL\]\s*(.+)$", re.M)
TOK = re.compile(r"^(tokens\.\w+|latency\.total_ms)\s*:\s*(\S+)\s*$", re.M)


def events(path: pathlib.Path) -> list[dict]:
    if not path.exists():
        return []
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            out.append(json.loads(line))
    return out


def clear_state() -> None:
    """逐轮独立：清焦点 / 白板 / 草稿（协议里它们「缺失即沿用」⇒ 不清就会串轮）。

    注：白板 / 草稿的 store 是**目录**（不是文件）⇒ `unlink` 会 EPERM，必须按目录处理。
    """
    for name in ("tail", "draft"):
        p = STAGE1 / name
        if p.is_dir():
            shutil.rmtree(p)
        elif p.exists():
            p.unlink()
    p = STAGE1 / "focus.json"
    if p.exists():
        p.unlink()


def run_one(label: str, task: str, stream: pathlib.Path, keyfile: str) -> dict:
    clear_state()
    cmd = ["dotnet", str(DLL), "--config", str(CFG),
           "--api-key-file", keyfile, "--stream", str(stream), "--verbose", task]
    t0 = time.time()
    proc = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True)
    elapsed = time.time() - t0
    raw = proc.stdout + proc.stderr
    (stream.parent / f"stdout-{label}.txt").write_text(raw, encoding="utf-8")   # 原始数据逐条落盘（可逐行复算）

    reported: list[str] = []
    for m in SKILL_LINE.finditer(proc.stdout):
        reported += m.group(1).split()
    reported = [s.strip() for s in reported if s.strip()]

    evs = events(stream)
    loaded = [e.get("source") for e in evs if e.get("kind") == "Skill"]
    tokens = {k: v for k, v in TOK.findall(raw)}

    return {
        "round": label,
        "task": task,
        "exit": proc.returncode,
        "wall_ms": round(elapsed * 1000),
        "reported_ids": reported,
        "loaded_ids": loaded,
        "stream_kinds": [e.get("kind") for e in evs],
        "j1_reported": len(reported) > 0,
        "j2_fulfilled": sorted(set(reported)) == sorted(set(loaded)),
        "j3_loaded": len(loaded) > 0,
        "tokens": tokens,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--prepare", action="store_true")
    ap.add_argument("--run", action="store_true")
    ap.add_argument("--api-key-file", default=None)
    ap.add_argument("--out", default=None, help="产物目录（默认 benchmark/runs/<stamp>-v6-skill-compliance）")
    args = ap.parse_args()

    stamp = time.strftime("%Y%m%d-%H%M%S")
    outdir = pathlib.Path(args.out) if args.out else RUNS / f"{stamp}-v6-skill-compliance"

    if args.prepare or not args.run:
        print(f"[量表] {len(TASKS)} 轮 · 产物目录：{outdir}")
        for label, task in TASKS:
            print(f"  - {label}: {task[:48]}…")
        print(f"[前置] 二进制：{DLL}（不存在则先 dotnet build src/AgentRuntime.Cli）")
        print(f"[前置] 配置：{CFG}（真模型 + 真语料 + skill.repo 地址表）")
        return 0

    if not DLL.exists():
        print(f"[中止] 找不到 {DLL} —— 先 `dotnet build src/AgentRuntime.Cli/AgentRuntime.Cli.csproj`。", file=sys.stderr)
        return 2
    keyfile = args.api_key_file or os.path.expanduser("~/.agentruntime/api_key")
    if not os.path.exists(keyfile):
        print("[中止] 缺 --api-key-file（本脚本从不读取密钥值，只把它作为路径传给 CLI）。", file=sys.stderr)
        return 2

    outdir.mkdir(parents=True, exist_ok=True)
    records = []
    for label, task in TASKS:
        rec = run_one(label, task, outdir / f"stream-{label}.jsonl", keyfile)
        records.append(rec)
        print(f"[{label}] J1自报={'✅' if rec['j1_reported'] else '❌'} "
              f"J2兑现={'✅' if rec['j2_fulfilled'] else '❌'} "
              f"J3命中={'✅' if rec['j3_loaded'] else '❌'} "
              f"· 自报 {len(rec['reported_ids'])} / 装载 {len(rec['loaded_ids'])} "
              f"· {rec['wall_ms']} ms")

    with (outdir / "records.jsonl").open("w", encoding="utf-8") as fh:
        for rec in records:
            fh.write(json.dumps(rec, ensure_ascii=False) + "\n")

    n = len(records)
    j1 = sum(1 for r in records if r["j1_reported"])
    j2 = sum(1 for r in records if r["j2_fulfilled"])
    j3 = sum(1 for r in records if r["j3_loaded"])
    lines = [
        f"# v6-skill-compliance —— `[L3]` 自报遵从率（{stamp}）",
        "",
        f"- 轮数：**{n}** · 模型：`deepseek-v4-flash` · 配置：`src/AgentRuntime.Cli/config.stage1.json`",
        f"- **J1 自报率**：{j1}/{n} = {100 * j1 // max(n, 1)}%",
        f"- **J2 兑现率**（自报 id == 落成 Skill 事件）：{j2}/{n}",
        f"- **J3 命中率**（至少一条正文进流）：{j3}/{n}",
        "",
        "| 轮 | 自报 ids | 装载 ids | 事件流 | prompt / cached | 耗时 |",
        "|---|---|---|---|---|---|",
    ]
    for r in records:
        tk = r["tokens"]
        lines.append(
            f"| `{r['round']}` | {len(r['reported_ids'])} 条 | {len(r['loaded_ids'])} 条 | "
            f"{'/'.join(r['stream_kinds'])[:60]} | {tk.get('tokens.prompt', '?')} / {tk.get('tokens.cached', '?')} | "
            f"{r['wall_ms']} ms |")
    lines += ["", "```", "python3 benchmark/tools/v6-skill-compliance.py --run --api-key-file ~/.agentruntime/api_key",
              "```", ""]
    (outdir / "summary.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"[产物] {outdir}/records.jsonl · summary.md")
    return 0 if (j1 == n and j2 == n) else 1


if __name__ == "__main__":
    sys.exit(main())

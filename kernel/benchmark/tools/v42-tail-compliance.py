#!/usr/bin/env python3
"""V4.2 R4 —— **自报遵从率量表**（只量一件事：模型会不会按协议维护白板）。

问题（先写清楚）：
  协议第 4 条说「任务/待办变化时，回复末尾加一段 [TAIL]」。**任务真的变了**的时候，
  模型到底报不报？—— 上一版夹具（探针题写「只回代号」、任务从不变）**量不出**这个问题
  （见 `docs/PITFALLS.md` #29）。

本量表的设计（与上一版的差别，逐条对应 #29 的两个根因）：
 ① **每轮真的发生任务变化**：用户消息明说「本轮起改做任务X」⇒ 按协议第 4 条**必须**自报，
    第 5 条「无变化可省略」**不再适用**（省略 = 违规）。
 ② **探针不抢指令**：不再写「只回代号」，改问「你现在在做什么 / 下一步做什么」——与自报段同向。

两组（同一份流骨架，**组盐隔离**；组间只差一句话）：

| 组 | 用户消息 | 量什么 |
|---|---|---|
| b | 只说明任务变化 + 问状态（**不提**协议、不提 `[TAIL]`） | **纯协议遵从率** |
| x | 同上 + 末句「（请在回复末尾按协议更新 `[TAIL]`）」 | 显式要求下的遵从率 |

判据：
 J1：b 组 `[TAIL]` 出现率 > 0 ⇒ 协议能驱动模型自报；= 0 ⇒ 协议单独驱动不了，需要改投递点。
 J2：x 组 ≥ b 组（显式要求只应提升、不应降低）—— 用来分辨「不遵守协议」与「不知道要报」。

铁律（与其它尺子同规）：密钥只走 `AGENTRUNTIME_API_KEY` 或 `--api-key-file <路径>`（本脚本**从不读取/打印**密钥）；
夹具确定性（同 SEED ⇒ 逐字节相同）；原始数据落 `records.jsonl` + `summary.md`（结论必须能逐行复算）。

用法：
    python3 benchmark/tools/v42-tail-compliance.py --prepare
    python3 benchmark/tools/v42-tail-compliance.py --run --api-key-file ~/.agentruntime/api_key
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import time

SEED = 20260918
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
CLI_PROJECT = ROOT / "src" / "AgentRuntime.Cli"
CLI_DLL = CLI_PROJECT / "bin" / "Debug" / "net10.0" / "AgentRuntime.Cli.dll"
GROUPS = ("b", "x")

# 每轮：**任务真的变化** + 不抢指令的状态问句。
TURNS = [
    ("任务三先放着。本轮起改做任务五：对账 9 月流水。你现在在做什么？下一步做什么？",
     "任务五"),
    ("任务五告一段落，本轮起改做任务六：核对回滚窗口。你现在在做什么？还欠什么？",
     "任务六"),
    ("任务六暂停，本轮起改做任务七：整理验收阈值。你现在在做什么？",
     "任务七"),
    ("任务七完成，本轮起改做任务八：归档本季度材料。当前任务与待办分别是什么？",
     "任务八"),
]

EXPLICIT_HINT = "（请在回复末尾按协议更新 [TAIL]）"

TOKEN_RE = re.compile(r"tokens\.(?P<key>prompt|completion|cached|uncached)\s*:\s*(?P<value>\d+)")
OVERHEAD_RE = re.compile(r"latency\.overhead_ms:\s*(?P<value>[\d.]+)")


def build_stream(group: str, seed: int = SEED) -> list[dict]:
    """确定性流骨架：两个已完成任务 + 当前任务三在进行（够让模型有上下文，但不喧宾夺主）。"""
    events: list[dict] = []

    def add(kind: str, text: str) -> None:
        events.append({"seq": len(events) + 1, "tag": f"E{len(events) + 1:03d}",
                       "kind": kind, "source": None, "text": text})

    add("Memory", f"实验变体盐：{group}-{seed}（组间隔离用；同组逐字节相同）")

    for index, (code, window) in enumerate((("K2-1188", "2026-08-11"), ("K4-3307", "2026-09-02")), start=1):
        add("UserInput", f"任务{index}现在到哪一步了？（代号 {code}）")
        add("AgentOutput", f"任务{index}已完成，代号 {code}，回滚窗口 {window}。")

    add("UserInput", "任务三开工：整理 8 月流水。")
    add("AgentOutput", "任务三进行中；已整理 8 月流水的前半段。")

    return events


def write_fixtures(run_dir: pathlib.Path, api_key_file: str | None, seed: int, model: str) -> dict:
    run_dir.mkdir(parents=True, exist_ok=True)
    sizes = {}

    for group in GROUPS:
        text = "".join(json.dumps(e, ensure_ascii=False) + "\n" for e in build_stream(group, seed))
        (run_dir / f"stream-{group}.jsonl").write_text(text, encoding="utf-8")
        sizes[group] = len(text.encode("utf-8"))

    def config(group: str, provider: str) -> dict:
        credentials = {"apiKeyEnv": "AGENTRUNTIME_API_KEY"}
        if api_key_file:
            credentials["apiKeyFile"] = api_key_file
        return {
            "provider": "openai-compatible",
            "baseUrl": "http://127.0.0.1:8899/v1" if provider == "mock" else "https://api.deepseek.com",
            "model": "mock-model" if provider == "mock" else model,
            **credentials,
            "timeoutSeconds": 120,
            "modules": ["append-stream", "current-tail"],
            "stream": {"path": f"stream-{group}.jsonl"},
            "snapshot": {"path": f"snapshot-{group}.json", "enabled": True},
            "currentTail": {"storePath": f"tail-{group}"},
        }

    for provider in ("real", "mock"):
        for group in GROUPS:
            (run_dir / f"config-{group}-{provider}.json").write_text(
                json.dumps(config(group, provider), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    return {"events": len(build_stream(GROUPS[0], seed)), "stream_bytes": sizes, "seed": seed, "model": model}


def cli_command(config: pathlib.Path) -> list[str]:
    if CLI_DLL.exists():
        return ["dotnet", str(CLI_DLL), "--config", str(config)]
    return ["dotnet", "run", "--project", str(CLI_PROJECT), "--", "--config", str(config)]


def whiteboard(run_dir: pathlib.Path, group: str) -> str:
    """照读存储区（**唯一真相源**）—— 白板有没有被模型写进去，看文件最实在。"""
    path = run_dir / f"tail-{group}" / "default.json"
    if not path.exists():
        return "（无存储文件）"
    try:
        entry = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return "（存储文件坏）"
    lines = entry.get("tail") or []
    return " / ".join(lines) if lines else "（空，零注入）"


def run_group(run_dir: pathlib.Path, group: str, provider: str) -> list[dict]:
    config = run_dir / f"config-{group}-{provider}.json"
    records: list[dict] = []

    for index, (question, expected_task) in enumerate(TURNS, start=1):
        user_message = question if group == "b" else f"{question}{EXPLICIT_HINT}"
        started = time.time()
        env = os.environ.copy()
        if provider == "mock":
            env["AGENTRUNTIME_API_KEY"] = "mock-provider-ignores-this"

        completed = subprocess.run(cli_command(config) + ["--verbose", user_message],
                                   capture_output=True, text=True, cwd=ROOT, env=env)
        elapsed_ms = (time.time() - started) * 1000

        answer = completed.stdout.strip()
        diagnostics = completed.stderr
        tokens = {m.group("key"): int(m.group("value")) for m in TOKEN_RE.finditer(diagnostics)}
        overhead = OVERHEAD_RE.search(diagnostics)
        prompt = tokens.get("prompt", 0)
        cached = tokens.get("cached", 0)
        board = whiteboard(run_dir, group)

        records.append({
            "group": group,
            "provider": provider,
            "turn": index,
            "user_message": user_message,
            "expected_task": expected_task,
            "answer": answer.replace("\n", " ")[:160],
            "reported_tail": "[TAIL]" in answer.upper(),
            "mentions_task": expected_task in answer,
            "board_after_turn": board,
            "board_has_content": board not in ("（空，零注入）", "（无存储文件）", "（存储文件坏）"),
            "prompt_tokens": prompt,
            "completion_tokens": tokens.get("completion", 0),
            "cached_tokens": cached,
            "cached_rate": round(cached / prompt, 4) if prompt else 0.0,
            "runtime_overhead_ms": float(overhead.group("value")) if overhead else None,
            "exit_code": completed.returncode,
            "wall_ms": round(elapsed_ms, 1),
        })

        if completed.returncode != 0:
            last = diagnostics.strip().splitlines()[-1] if diagnostics.strip() else "(无 stderr)"
            print(f"[量表] ⚠️ {group.upper()} turn {index} 退出码 {completed.returncode}：{last}", file=sys.stderr)

    return records


def summarize(run_dir: pathlib.Path, records: list[dict], meta: dict) -> None:
    groups: dict[str, list[dict]] = {}
    for record in records:
        groups.setdefault(record["group"], []).append(record)

    lines = [
        "# V4.2 R4 自报遵从率量表（协议 v3 · 任务每轮真的变化）", "",
        f"- 夹具种子：{meta['seed']}（确定性；组间用盐隔离）",
        f"- 流规模：{meta['events']} 条事件 / {meta['stream_bytes']}",
        f"- provider：{meta.get('provider', 'real')}；模型：{meta['model']}",
        "- 判据：**b 组（纯协议驱动）`[TAIL]` 出现率 > 0 ⇒ 协议能驱动自报**；x 组（显式要求）≥ b 组。",
        "",
        "| 组 | 自报轮数 | 总轮数 | 遵从率 | 白板被写入轮数 | 正确提到新任务 |",
        "|---|---|---|---|---|---|",
    ]

    for group in GROUPS:
        rows = groups.get(group, [])
        if not rows:
            continue
        reported = sum(1 for r in rows if r["reported_tail"])
        board = sum(1 for r in rows if r["board_has_content"])
        mentions = sum(1 for r in rows if r["mentions_task"])
        lines.append(f"| {group.upper()} | {reported} | {len(rows)} | {reported / len(rows):.0%} | "
                     f"{board}/{len(rows)} | {mentions}/{len(rows)} |")

    lines += ["", "## 逐 turn 明细", "",
              "| 组 | turn | 期望任务 | 答里提到 | 有 [TAIL] | 白板（存储区） | prompt | cached | 开销ms |",
              "|---|---|---|---|---|---|---|---|---|"]
    for record in records:
        lines.append(
            f"| {record['group'].upper()} | {record['turn']} | {record['expected_task']} | "
            f"{'✓' if record['mentions_task'] else '✗'} | {'✓' if record['reported_tail'] else '✗'} | "
            f"{record['board_after_turn'][:60]} | {record['prompt_tokens']} | {record['cached_tokens']} | "
            f"{record['runtime_overhead_ms']} |")

    lines += ["", "## 回答原文（截断 160 字）", ""]
    for record in records:
        lines.append(f"- [{record['group'].upper()}#{record['turn']}] {record['answer']}")

    lines += ["", "## 复算", "", "```bash",
              "python3 benchmark/tools/v42-tail-compliance.py --prepare",
              f"python3 benchmark/tools/v42-tail-compliance.py --run --api-key-file ~/.agentruntime/api_key "
              f"--dir {run_dir.name}",
              "```", "",
              "原始数据：`records.jsonl`（一条 = 一个 turn，含回答原文 / 是否自报 / 存储区白板 / token）。",
              "注：`--api-key-file` 只把**路径**写进配置；本脚本不接触密钥内容。"]

    (run_dir / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepare", action="store_true", help="只生成夹具与配置")
    parser.add_argument("--run", action="store_true", help="真机跑两组（需要密钥）")
    parser.add_argument("--provider", choices=("real", "mock"), default="real")
    parser.add_argument("--dir", default=None, help="实验目录名（默认按时间戳新建）")
    parser.add_argument("--seed", type=int, default=SEED, help="夹具种子（换种子 = 逐字节全变 = 真冷启动）")
    parser.add_argument("--model", default="deepseek-flash", help="真机模型 id（默认 deepseek-flash）")
    parser.add_argument("--api-key-file", default=None,
                        help="从本地文件取密钥（只传路径；CLI 自己读，脚本不接触密钥内容）")
    args = parser.parse_args()

    stamp = time.strftime("%Y%m%d-%H%M%S")
    run_dir = ROOT / "benchmark" / "runs" / (args.dir or f"{stamp}-v42-tail-compliance")

    meta = write_fixtures(run_dir, args.api_key_file, args.seed, args.model)
    meta["provider"] = args.provider
    print(f"[夹具] {run_dir}：{meta['events']} 条事件 / {meta['stream_bytes']}")

    if args.prepare or not args.run:
        print("[夹具] 已就绪（未跑）。真机运行：--run --api-key-file <路径>")
        return 0

    if args.provider == "real" and not os.environ.get("AGENTRUNTIME_API_KEY") and not args.api_key_file:
        print("[量表] 未做：既无 AGENTRUNTIME_API_KEY，也未给 --api-key-file。", file=sys.stderr)
        return 2

    records: list[dict] = []
    for group in GROUPS:
        print(f"[量表] 跑 {group.upper()} 组…", flush=True)
        records.extend(run_group(run_dir, group, args.provider))

    (run_dir / "records.jsonl").write_text(
        "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in records), encoding="utf-8")
    summarize(run_dir, records, meta)
    print(f"[量表] 完成：{run_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

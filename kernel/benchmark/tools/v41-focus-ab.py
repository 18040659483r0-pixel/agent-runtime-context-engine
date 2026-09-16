#!/usr/bin/env python3
"""V4.1 Semantic Focus —— A/B 实验尺子（规格书 §十）。

A 组：只挂 append-stream（一条 ≥200 事件的流：3 个已结束任务 + 1 个当前任务）
B 组：同流 + focus（自报权重策略）

指标：① 任务准确完成率 ② 是否还被逼着收尾重拉（用「深度＝turn 数」时的准确率走势代理）
      ③ 单 turn token 增量 ④ cached 命中率 ⑤ Runtime 开销

铁律：
- **密钥只从环境变量 AGENTRUNTIME_API_KEY 取**：本脚本只检查「有没有」，**从不读取、打印、写入**它；
  API 调用由 CLI 自己从同一个环境变量取（本脚本只透传已有环境）。
- 夹具**确定性**：同一个 SEED 生成同一份流（逐字节），A/B 用**逐字节相同**的流副本。
- 原始数据落盘：records.jsonl（一条 = 一个 turn）+ summary.md（人看的汇总）。

用法：
    python3 benchmark/tools/v41-focus-ab.py --prepare                 # 只生成夹具（不需密钥）
    AGENTRUNTIME_API_KEY=... python3 benchmark/tools/v41-focus-ab.py --run        # 真机 A/B（环境变量）
    python3 benchmark/tools/v41-focus-ab.py --run --api-key-file ~/.agentruntime/api_key   # 真机 A/B（本机密钥文件）
    python3 benchmark/tools/v41-focus-ab.py --run --provider mock     # 管线演练（不需要密钥，需 mock provider）

`--api-key-file` 只向配置写入**路径**（`apiKeyFile`）：密钥由 CLI 自己从文件读，
本脚本**从不读取 / 打印 / 写入密钥内容**（与 `AGENTRUNTIME_API_KEY` 同一条纪律）。
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

SEED = 20260915  # 夹具生成种子（写进 summary，保证可复算）
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
CLI_PROJECT = ROOT / "src" / "AgentRuntime.Cli"

HINT = "回复末尾用一行 [FOCUS] E### E### 标出本轮依据的事件标签"

# 当前任务（任务四）的**判据题**：答案逐字存在于流里的某条事件中；
# 三个已结束任务里出现的是**同形状的干扰代号**（考验注意力）。
PROBES = [
    ("当前任务（任务四）的部署代号是什么？只回代号。", "K9-7421"),
    ("当前任务（任务四）的回滚窗口是哪天？只回日期。", "2026-10-07"),
    ("当前任务（任务四）的验收阈值是多少？只回数字。", "0.87"),
    ("当前任务（任务四）的负责人代号是什么？只回代号。", "OPS-31"),
    ("当前任务（任务四）的灰度比例是多少？只回数字。", "15"),
    ("当前任务（任务四）的截止日期是哪天？只回日期。", "2026-10-20"),
]

CURRENT_TASK_FACTS = [
    ("当前任务（任务四）的部署代号是 K9-7421。", "K9-7421"),
    ("当前任务（任务四）的回滚窗口是 2026-10-07。", "2026-10-07"),
    ("当前任务（任务四）的验收阈值是 0.87。", "0.87"),
    ("当前任务（任务四）的负责人代号是 OPS-31。", "OPS-31"),
    ("当前任务（任务四）的灰度比例是 15。", "15"),
    ("当前任务（任务四）的截止日期是 2026-10-20。", "2026-10-20"),
]

FINISHED_TASK_SALT = {
    1: ("K2-1188", "2026-08-11", "0.92"),
    2: ("K4-3307", "2026-09-02", "0.79"),
    3: ("K7-6650", "2026-09-19", "0.95"),
}


def build_stream() -> list[dict]:
    """确定性生成一条 ≥200 事件的流（3 个已结束任务 + 1 个当前任务）。"""
    events: list[dict] = []

    def add(kind: str, text: str, source: str | None = None) -> None:
        events.append({"seq": len(events) + 1, "tag": f"E{len(events) + 1:03d}",
                       "kind": kind, "source": source, "text": text})

    add("Rule", HINT)

    # ---- 三个已结束任务：每个任务 3 轮，每轮 15 条工作记录 + 问 + 答 + 自报 ----
    for task in (1, 2, 3):
        code, date, ratio = FINISHED_TASK_SALT[task]
        add("Knowledge", f"任务{task}（已结束）的部署代号是 {code}。")
        for round_index in (1, 2, 3):
            citations: list[str] = []
            for item in range(15):
                add("ToolResult", f"任务{task} 第 {round_index} 轮工作记录 {item}：步骤 {task}{round_index}{item} 完成，"
                                  f"观测值 {item * 7 % 13}，所属代号 {code}。")
                if item in (2, 9):
                    citations.append(f"E{len(events):03d}")
            add("UserInput", f"任务{task} 第 {round_index} 轮：继续推进。")
            add("AgentOutput", f"任务{task} 第 {round_index} 轮完成，依据 {code}、{date}、阈值 {ratio}。")
            add("FocusReport", " ".join(citations))

    # ---- 当前任务（任务四）：关键事实散在 3 轮工作记录里 ----
    add("Knowledge", "任务四（当前）开工：目标是把发布流程收敛到一条命令。")
    for round_index in (1, 2, 3):
        facts = CURRENT_TASK_FACTS[(round_index - 1) * 2: round_index * 2]
        add("UserInput", f"任务四 第 {round_index} 轮：开始。")
        citations = []
        for index in range(15):
            fact_text = facts[0][0] if index == 3 else (
                facts[1][0] if index == 11 else f"任务四 第 {round_index} 轮工作记录 {index}：步骤 4{round_index}{index} 完成。")
            add("Knowledge" if index in (3, 11) else "ToolResult", fact_text)
            if index in (3, 11):
                citations.append(f"E{len(events):03d}")
        add("AgentOutput", f"任务四 第 {round_index} 轮完成。")
        add("FocusReport", " ".join(citations))

    return events


def write_fixtures(run_dir: pathlib.Path, api_key_file: str | None = None) -> dict:
    run_dir.mkdir(parents=True, exist_ok=True)
    events = build_stream()
    stream_text = "".join(json.dumps(e, ensure_ascii=False) + "\n" for e in events)

    for group in ("a", "b"):
        (run_dir / f"stream-{group}.jsonl").write_text(stream_text, encoding="utf-8")

    def config(group: str, provider: str) -> dict:
        modules = ["append-stream"] + (["focus"] if group == "b" else [])
        base_url = "http://127.0.0.1:8899/v1" if provider == "mock" else "https://api.deepseek.com"
        model = "mock-model" if provider == "mock" else "deepseek-flash"   # 提供商 /models 清单里的 id（'deepseek-v4-flash' 不在清单里，靠服务端兼容才没报错）
        credentials = {"apiKeyEnv": "AGENTRUNTIME_API_KEY"}
        if api_key_file:
            # 只传**路径**：密钥由 CLI 自己从文件读（脚本不接触密钥内容）。
            credentials["apiKeyFile"] = api_key_file
        return {
            "provider": "openai-compatible",
            "baseUrl": base_url,
            "model": model,
            **credentials,
            "timeoutSeconds": 120,
            "modules": modules,
            "stream": {"path": f"stream-{group}.jsonl"},
            "snapshot": {"path": f"snapshot-{group}.json", "enabled": True},
            # 注：reportHint 已从配置项删除（提示词改由协议区声明，用户无权改）—— 写了会被 CLI 显式拒绝。
            "focus": {"path": f"focus-{group}.json", "policy": "report",
                      "topK": 16, "minWeight": 0.95, "halfLifeTurns": 40},   # 阈值 = FocusOptions.DefaultMinWeight（2026-09-15 由 1.0 下调）
        }

    for provider in ("real", "mock"):
        for group in ("a", "b"):
            (run_dir / f"config-{group}-{provider}.json").write_text(
                json.dumps(config(group, provider), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    (run_dir / "probes.json").write_text(
        json.dumps([{"question": q, "expected": a} for q, a in PROBES], ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8")

    return {"events": len(events), "stream_bytes": len(stream_text.encode("utf-8"))}


TOKEN_RE = re.compile(r"tokens\.(?P<key>prompt|completion|cached|uncached)\s*:\s*(?P<value>\d+)")
OVERHEAD_RE = re.compile(r"latency\.overhead_ms:\s*(?P<value>[\d.]+)")


def run_group(run_dir: pathlib.Path, group: str, provider: str) -> list[dict]:
    config = run_dir / f"config-{group}-{provider}.json"
    records: list[dict] = []

    for index, (question, expected) in enumerate(PROBES, start=1):
        # 每个 turn 一个进程：会话状态在**只追加流**里（进程间靠流文件续接）。
        started = time.time()
        env = os.environ.copy()
        if provider == "mock":
            # mock provider 不看密钥，但 CLI 仍然要求「有个密钥」——这里放一个占位串（不是真密钥）。
            env["AGENTRUNTIME_API_KEY"] = "mock-provider-ignores-this"

        completed = subprocess.run(
            ["dotnet", "run", "--project", str(CLI_PROJECT), "--",
             "--config", str(config), "--verbose", question],
            capture_output=True, text=True, cwd=ROOT, env=env)
        elapsed_ms = (time.time() - started) * 1000

        if completed.returncode != 0:
            print(f"[实验] ⚠️ {group.upper()} turn {index} 退出码 {completed.returncode}："
                  f"{completed.stderr.strip().splitlines()[-1] if completed.stderr.strip() else '(无 stderr)'}",
                  file=sys.stderr)

        answer = completed.stdout.strip()
        diagnostics = completed.stderr
        tokens = {m.group("key"): int(m.group("value")) for m in TOKEN_RE.finditer(diagnostics)}
        overhead = OVERHEAD_RE.search(diagnostics)

        prompt = tokens.get("prompt", 0)
        cached = tokens.get("cached", 0)
        records.append({
            "group": group,
            "provider": provider,
            "turn": index,
            "question": question,
            "expected": expected,
            "answer": answer,
            "correct": expected in answer,
            "prompt_tokens": prompt,
            "completion_tokens": tokens.get("completion", 0),
            "cached_tokens": cached,
            "uncached_tokens": tokens.get("uncached", 0),
            "cached_rate": round(cached / prompt, 4) if prompt else 0.0,
            "runtime_overhead_ms": float(overhead.group("value")) if overhead else None,
            "exit_code": completed.returncode,
            "wall_ms": round(elapsed_ms, 1),
        })

    return records


def summarize(run_dir: pathlib.Path, records: list[dict], meta: dict) -> None:
    groups = {}
    for record in records:
        groups.setdefault(record["group"], []).append(record)

    lines = [
        "# V4.1 Semantic Focus —— A/B 实验（规格书 §十）",
        "",
        f"- 夹具种子：{SEED}（确定性；A/B 用逐字节相同的流副本）",
        f"- 流规模：{meta['events']} 条事件 / {meta['stream_bytes']} 字节（3 个已结束任务 + 1 个当前任务）",
        f"- provider：{meta['provider']}；模型：{meta['model']}",
        f"- 判据（先写）：B 组在「准确完成率」上若有可复现提升 ⇒ 焦点成立；若无 ⇒ 降级为「账本-only」。",
        "",
        "| 组 | 准确率 | prompt 均值 | cached 命中率 | Runtime 开销均值(ms) |",
        "|---|---|---|---|---|",
    ]

    for group in sorted(groups):
        rows = groups[group]
        correct = sum(1 for r in rows if r["correct"])
        prompt = sum(r["prompt_tokens"] for r in rows) / max(1, len(rows))
        cached = sum(r["cached_tokens"] for r in rows) / max(1, sum(r["prompt_tokens"] for r in rows))
        overhead = [r["runtime_overhead_ms"] for r in rows if r["runtime_overhead_ms"] is not None]
        lines.append(
            f"| {group.upper()} | {correct}/{len(rows)} | {prompt:.0f} | {cached:.1%} | "
            f"{(sum(overhead) / len(overhead) if overhead else 0):.2f} |")

    lines += ["", "## 逐 turn 明细", "",
              "| 组 | turn | 期望 | 回答（截断） | 对? | prompt | cached | 命中率 | 开销ms |",
              "|---|---|---|---|---|---|---|---|---|"]
    for record in records:
        answer = record["answer"].replace("\n", " ")[:40]
        lines.append(
            f"| {record['group'].upper()} | {record['turn']} | {record['expected']} | {answer} | "
            f"{'✓' if record['correct'] else '✗'} | {record['prompt_tokens']} | {record['cached_tokens']} | "
            f"{record['cached_rate']:.1%} | {record['runtime_overhead_ms']} |")

    lines += ["", "## 复算", "",
              "```bash",
              "# 夹具（确定性，不需要密钥）",
              "python3 benchmark/tools/v41-focus-ab.py --prepare",
              "# 真机 A/B（密钥只在环境变量里；脚本不读不打印）",
              f"AGENTRUNTIME_API_KEY=... python3 benchmark/tools/v41-focus-ab.py --run --dir {run_dir.name}",
              "```",
              "",
              "原始数据：`records.jsonl`（一条 = 一个 turn，含 token / cached / 开销 / 回答原文）。"]

    (run_dir / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepare", action="store_true", help="只生成夹具与配置")
    parser.add_argument("--run", action="store_true", help="真机跑 A/B（需要 AGENTRUNTIME_API_KEY）")
    parser.add_argument("--provider", choices=("real", "mock"), default="real")
    parser.add_argument("--dir", default=None, help="实验目录名（默认按时间戳新建）")
    parser.add_argument("--api-key-file", default=None,
                        help="从本地文件取密钥（只传路径；CLI 自己读，脚本不接触密钥内容）")
    args = parser.parse_args()

    stamp = time.strftime("%Y%m%d-%H%M%S")
    name = args.dir or f"{stamp}-v41-focus-ab"
    run_dir = ROOT / "benchmark" / "runs" / name

    meta = write_fixtures(run_dir, args.api_key_file)
    print(f"[夹具] {run_dir}：{meta['events']} 条事件 / {meta['stream_bytes']} 字节")

    if args.prepare or not args.run:
        print("[夹具] 已就绪（未跑实验）。真机运行："
              "AGENTRUNTIME_API_KEY=... python3 benchmark/tools/v41-focus-ab.py --run")
        return 0

    if args.provider == "real" and not os.environ.get("AGENTRUNTIME_API_KEY") and not args.api_key_file:
        # 只判断「有没有」，绝不读取/回显密钥内容。
        print("[实验] 未做：既无环境变量 AGENTRUNTIME_API_KEY，也未给 --api-key-file（按规格书 §十：取不到密钥即跳过）。",
              file=sys.stderr)
        return 2

    records: list[dict] = []
    for group in ("a", "b"):
        print(f"[实验] 跑 {group.upper()} 组…")
        records.extend(run_group(run_dir, group, args.provider))

    (run_dir / "records.jsonl").write_text(
        "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in records), encoding="utf-8")

    model = json.loads((run_dir / "config-a-real.json").read_text(encoding="utf-8"))["model"] \
        if args.provider == "real" else "mock-model"
    summarize(run_dir, records, {**meta, "provider": args.provider, "model": model})
    print(f"[实验] 完成：{run_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

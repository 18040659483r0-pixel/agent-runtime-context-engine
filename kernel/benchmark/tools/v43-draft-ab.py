#!/usr/bin/env python3
"""V4.3 Dynamic Draft Region（R5）—— 真机对照尺子（规格书 §八·4）。

问题（先写清楚，再看数字）：
  **R5 每个 turn 都被「大删大改」（整块推翻重写）⇒ R1/R2/R4 的 cached 命中率不降吗？**

两组（同一份语料骨架，**组间用盐隔离**，组内只动一个变量）：

| 组 | 模块 | R5 |
|---|---|---|
| A | append-stream + focus + current-tail | 无（= V4.2 形态，参照） |
| D | 同上 + **dynamic-draft** | **每轮由宿主整块重写**（`--draft` 每轮全换 + `--draft-report off`） |

判据：
  J1（I9 · 组内）：D 组 turn≥2 的 cached **不低于** A 组同 turn（R5 变化只该作废它自己与更靠后的 R3）。
  J2（冷启动）：每组第 1 轮 cached 应尽量低（组间共享的只有协议区那几百 token，如实记录）。
  J3（正确率）：R5 不得让任务正确率下降（Task Equivalence）。

铁律（与 v41/v42 尺子同规）：
- 密钥只走环境变量 `AGENTRUNTIME_API_KEY` 或 `--api-key-file <路径>`；本脚本**从不读取/打印/写入**密钥内容。
- 夹具确定性（同一 SEED ⇒ 逐字节相同的流）；原始数据落 `records.jsonl`，结论必须能逐行复算。
- 每个 turn 一个进程（会话状态在只追加流里，跨进程靠流 + 存储区续接）。

用法：
    python3 benchmark/tools/v43-draft-ab.py --prepare
    python3 benchmark/tools/v43-draft-ab.py --run --api-key-file ~/.agentruntime/api_key
    python3 benchmark/tools/v43-draft-ab.py --run --provider mock     # 管线演练（不需要密钥）
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

SEED = 20260917   # 夹具种子（--seed 可改；写进 summary，保证可复算）
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
CLI_PROJECT = ROOT / "src" / "AgentRuntime.Cli"
CLI_DLL = ROOT / "src" / "AgentRuntime.Cli" / "bin" / "Debug" / "net10.0" / "AgentRuntime.Cli.dll"

HINT = "回复末尾用一行 [FOCUS] E### E### 标出本轮依据的事件标签"

QUESTIONS = [
    ("当前任务（任务四）的部署代号是什么？只回代号。", "K9-7421"),
    ("当前任务（任务四）的回滚窗口是哪天？只回日期。", "2026-10-07"),
    ("当前任务（任务四）的验收阈值是多少？只回数字。", "0.87"),
    ("当前任务（任务四）的负责人代号是什么？只回代号。", "OPS-31"),
]

CURRENT_TASK_FACTS = [
    "当前任务（任务四）的部署代号是 K9-7421。",
    "当前任务（任务四）的回滚窗口是 2026-10-07。",
    "当前任务（任务四）的验收阈值是 0.87。",
    "当前任务（任务四）的负责人代号是 OPS-31。",
]

FINISHED_TASKS = [
    ("K2-1188", "2026-08-11", "0.92"),
    ("K4-3307", "2026-09-02", "0.79"),
    ("K7-6650", "2026-09-19", "0.95"),
]

# D 组草稿**每轮整块推翻重写**（宿主侧 --draft；不依赖模型配合 ⇒ 确定性、可复算）。
# 关键：不是追加，是**全换内容**（这才是「唯一允许大删大改的区域」的正面对照）。
def turn_draft(n: int) -> str:
    return (
        f"讨论中的构想: 第 {n} 轮候选方案 {n}A / {n}B / {n}C（上一轮全部作废）\n"
        f"未定稿需求: 第 {n} 版取舍标准尚未定稿\n"
        f"未验证假设: 假设 H{n} —— 待第 {n} 轮验证\n"
        f"已废弃: 第 {n - 1} 轮草案（整块删除，不保留）"
    )


GROUPS = ("a", "d")

TOKEN_RE = re.compile(r"tokens\.(?P<key>prompt|completion|cached|uncached)\s*:\s*(?P<value>\d+)")
OVERHEAD_RE = re.compile(r"latency\.overhead_ms:\s*(?P<value>[\d.]+)")


def build_stream(group: str, seed: int = SEED) -> list[dict]:
    """确定性生成一条流；**组盐放在第一条**（变体隔离：组间不共享 R2 之后的缓存）。"""
    events: list[dict] = []

    def add(kind: str, text: str) -> None:
        events.append({"seq": len(events) + 1, "tag": f"E{len(events) + 1:03d}",
                       "kind": kind, "source": None, "text": text})

    add("Rule", HINT)
    add("Memory", f"实验变体盐：{group}-{seed}（组间隔离用；同组逐字节相同）")

    for index, (code, window, threshold) in enumerate(FINISHED_TASKS, start=1):
        for step in range(1, 16):
            add("ToolResult", f"任务{index} 步骤{step}：检查 {code} / {window} / {threshold} 的第 {step} 项，结论 ok。")
        add("UserInput", f"任务{index} 现在到哪一步了？（代号 {code}）")
        add("AgentOutput", f"任务{index} 已完成，代号 {code}，回滚窗口 {window}，阈值 {threshold}。")
        add("FocusReport", "E003")

    for fact in CURRENT_TASK_FACTS:
        add("Memory", fact)

    add("UserInput", "任务四（当前任务）开工：先确认它的部署代号。")
    add("AgentOutput", "任务四进行中；已记录部署代号 K9-7421。")
    add("FocusReport", f"E{len(events) + 1 - 4:03d}")

    return events


def write_fixtures(run_dir: pathlib.Path, api_key_file: str | None, seed: int = SEED,
                   real_model: str = "deepseek-flash") -> dict:
    run_dir.mkdir(parents=True, exist_ok=True)
    sizes = {}

    for group in GROUPS:
        events = build_stream(group, seed)
        text = "".join(json.dumps(e, ensure_ascii=False) + "\n" for e in events)
        (run_dir / f"stream-{group}.jsonl").write_text(text, encoding="utf-8")
        sizes[group] = len(text.encode("utf-8"))

    def config(group: str, provider: str) -> dict:
        modules = ["append-stream", "current-tail"]
        if group == "d":
            modules.append("dynamic-draft")
        modules.append("focus")   # R3 居末（规范序 R2 → R4 → R5 → R3；闸门会拦违序）
        base_url = "http://127.0.0.1:8899/v1" if provider == "mock" else "https://api.deepseek.com"
        model = "mock-model" if provider == "mock" else real_model
        credentials = {"apiKeyEnv": "AGENTRUNTIME_API_KEY"}
        if api_key_file:
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
            "focus": {"path": f"focus-{group}.json", "policy": "report",
                      "topK": 16, "minWeight": 0.95, "halfLifeTurns": 40},
            "currentTail": {"storePath": f"tail-{group}"},
            # dynamicDraft **只留 storePath**（上限/提示词在协议区；写上 maxLines 会被显式拒绝）。
            "dynamicDraft": {"storePath": f"draft-{group}"} if group == "d" else {},
        }

    for provider in ("real", "mock"):
        for group in GROUPS:
            (run_dir / f"config-{group}-{provider}.json").write_text(
                json.dumps(config(group, provider), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    return {"events": len(build_stream("a", seed)), "stream_bytes": sizes, "seed": seed}


def cli_command(config: pathlib.Path) -> list[str]:
    if CLI_DLL.exists():
        return ["dotnet", str(CLI_DLL), "--config", str(config)]
    return ["dotnet", "run", "--project", str(CLI_PROJECT), "--", "--config", str(config)]


def run_group(run_dir: pathlib.Path, group: str, provider: str) -> list[dict]:
    config = run_dir / f"config-{group}-{provider}.json"
    records: list[dict] = []

    for index, (question, expected) in enumerate(QUESTIONS, start=1):
        # D 组：本轮开跑前把草稿**整块推翻重写**（宿主侧 --draft；整块覆盖，非追加）。
        if group == "d":
            subprocess.run(
                cli_command(config) + ["--draft", turn_draft(index)],
                capture_output=True, text=True, cwd=ROOT, env=os.environ.copy())

        started = time.time()
        env = os.environ.copy()
        if provider == "mock":
            env["AGENTRUNTIME_API_KEY"] = "mock-provider-ignores-this"

        extra = ["--draft-report", "off", "--tail-report", "off"] if group == "d" else []
        completed = subprocess.run(
            cli_command(config) + ["--verbose"] + extra + [question],
            capture_output=True, text=True, cwd=ROOT, env=env)
        elapsed_ms = (time.time() - started) * 1000

        answer = completed.stdout.strip()
        diagnostics = completed.stderr
        tokens = {m.group("key"): int(m.group("value")) for m in TOKEN_RE.finditer(diagnostics)}
        overhead = OVERHEAD_RE.search(diagnostics)

        prompt = tokens.get("prompt", 0)
        cached = tokens.get("cached", 0)
        draft_line = next((l for l in diagnostics.splitlines() if l.startswith("[草稿]")), "")
        records.append({
            "group": group,
            "provider": provider,
            "turn": index,
            "question": question,
            "expected": expected,
            "answer": answer.replace("\n", " ")[:80],
            "correct": expected in answer,
            "prompt_tokens": prompt,
            "completion_tokens": tokens.get("completion", 0),
            "cached_tokens": cached,
            "uncached_tokens": tokens.get("uncached", 0),
            "cached_rate": round(cached / prompt, 4) if prompt else 0.0,
            "runtime_overhead_ms": float(overhead.group("value")) if overhead else None,
            "draft_signal": draft_line,
            "exit_code": completed.returncode,
            "wall_ms": round(elapsed_ms, 1),
        })

        if completed.returncode != 0:
            last = diagnostics.strip().splitlines()[-1] if diagnostics.strip() else "(无 stderr)"
            print(f"[实验] ⚠️ {group.upper()} turn {index} 退出码 {completed.returncode}：{last}", file=sys.stderr)

    return records


def summarize(run_dir: pathlib.Path, records: list[dict], meta: dict) -> None:
    groups: dict[str, list[dict]] = {}
    for record in records:
        groups.setdefault(record["group"], []).append(record)

    def by_turn(rows: list[dict]) -> dict[int, dict]:
        return {r["turn"]: r for r in rows}

    lines = [
        "# V4.3 Dynamic Draft Region（R5）—— 真机对照",
        "",
        f"- 夹具种子：{meta['seed']}（确定性；组间以流首盐隔离，组内逐字节相同）",
        f"- 流规模：{meta['events']} 条事件 / 最大 {max(meta['stream_bytes'].values())} 字节",
        f"- provider：{meta['provider']}；模型：{meta['model']}",
        "- 两组：A = V4.2 形态（无 R5）；D = R5 **每轮整块推翻重写**（`--draft` 全换 + `--draft-report off`）",
        "- 判据（先写）：J1 = D 组 turn≥2 的 cached 不低于 A 组同 turn（R5 变化只该作废它自己与 R3）；"
        "J2 = 每组第 1 轮尽量冷；J3 = R5 不降正确率。",
        "",
        "| 组 | 正确率 | prompt 均值 | cached 均值 | 命中率(cached/prompt 合计) | 开销均值(ms) |",
        "|---|---|---|---|---|---|",
    ]

    for group in sorted(groups):
        rows = groups[group]
        correct = sum(1 for r in rows if r["correct"])
        prompt = sum(r["prompt_tokens"] for r in rows) / max(1, len(rows))
        cached_rate = sum(r["cached_tokens"] for r in rows) / max(1, sum(r["prompt_tokens"] for r in rows))
        overhead = [r["runtime_overhead_ms"] for r in rows if r["runtime_overhead_ms"] is not None]
        lines.append(
            f"| {group.upper()} | {correct}/{len(rows)} | {prompt:.0f} | "
            f"{sum(r['cached_tokens'] for r in rows) / max(1, len(rows)):.0f} | "
            f"{cached_rate:.1%} | {(sum(overhead) / len(overhead) if overhead else 0):.2f} |")

    lines += ["", "## 逐 turn 明细", "",
              "| 组 | turn | 期望 | 对? | prompt | cached | uncached | 命中率 | 开销ms | 草稿信号 |",
              "|---|---|---|---|---|---|---|---|---|---|"]
    for record in records:
        lines.append(
            f"| {record['group'].upper()} | {record['turn']} | {record['expected']} | "
            f"{'✓' if record['correct'] else '✗'} | {record['prompt_tokens']} | {record['cached_tokens']} | "
            f"{record['uncached_tokens']} | {record['cached_rate']:.1%} | {record['runtime_overhead_ms']} | "
            f"{record['draft_signal'][:60] or '（无）'} |")

    lines += ["", "## J1 · R5 每轮大删大改，稳定前缀还命中吗（A vs D）", "",
              "| turn | A cached | D cached | 差(D−A) | A 命中率 | D 命中率 |", "|---|---|---|---|---|---|"]
    a_turns, d_turns = by_turn(groups.get("a", [])), by_turn(groups.get("d", []))
    verdict = "无数据"
    deltas = []
    for turn in sorted(set(a_turns) & set(d_turns)):
        a, d = a_turns[turn], d_turns[turn]
        deltas.append((turn, d["cached_tokens"] - a["cached_tokens"]))
        lines.append(f"| {turn} | {a['cached_tokens']} | {d['cached_tokens']} | "
                     f"{d['cached_tokens'] - a['cached_tokens']:+d} | {a['cached_rate']:.1%} | {d['cached_rate']:.1%} |")
    hot = [(t, delta) for t, delta in deltas if t >= 2]
    if hot:
        worst = min(delta for _, delta in hot)
        verdict = "✅ J1 成立（D ≥ A，或差值为 0 附近的块对齐噪声）" if worst >= -128 else "❌ J1 不成立（稳定前缀命中被 R5 拖低）"
        lines += ["", f"- turn≥2 差值（D−A）：" + "、".join(f"t{t}:{d:+d}" for t, d in hot),
                  f"- **{verdict}**（判据：turn≥2 的 D 不低于 A；允许 64-token 块对齐的 ±1 块噪声）"]

    lines += ["", "## J2/J3 · 冷启动与正确率", ""]
    for group in GROUPS:
        rows = groups.get(group, [])
        if rows:
            lines.append(f"- {group.upper()} 组 turn 1：prompt={rows[0]['prompt_tokens']} cached={rows[0]['cached_tokens']} "
                         f"（冷启动自检；组间只共享协议区）")
            lines.append(f"- {group.upper()} 组正确率：{sum(1 for r in rows if r['correct'])}/{len(rows)}")

    lines += ["", "## 复算", "",
              "```bash",
              "python3 benchmark/tools/v43-draft-ab.py --prepare",
              f"python3 benchmark/tools/v43-draft-ab.py --run --api-key-file ~/.agentruntime/api_key --dir {run_dir.name}",
              "```",
              "",
              "原始数据：`records.jsonl`（一条 = 一个 turn，含 token / cached / 开销 / 回答原文 / 草稿信号）。",
              "注：`--api-key-file` 只把**路径**写进配置，密钥由 CLI 自己读；本脚本不接触密钥内容。"]

    (run_dir / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepare", action="store_true", help="只生成夹具与配置")
    parser.add_argument("--run", action="store_true", help="真机跑两组（需要密钥）")
    parser.add_argument("--provider", choices=("real", "mock"), default="real")
    parser.add_argument("--dir", default=None, help="实验目录名（默认按时间戳新建）")
    parser.add_argument("--seed", type=int, default=SEED, help="夹具种子（换种子 = 语料逐字节全变）")
    parser.add_argument("--api-key-file", default=None, help="从本地文件取密钥（只传路径）")
    parser.add_argument("--model", default="deepseek-flash", help="真机模型 id（默认 deepseek-flash）")
    args = parser.parse_args()

    stamp = time.strftime("%Y%m%d-%H%M%S")
    run_dir = ROOT / "benchmark" / "runs" / (args.dir or f"{stamp}-v43-draft-ab")

    meta = write_fixtures(run_dir, args.api_key_file, args.seed, args.model)
    print(f"[夹具] {run_dir}：{meta['events']} 条事件 / {meta['stream_bytes']}")

    if args.prepare or not args.run:
        print("[夹具] 已就绪（未跑实验）。真机运行：--run --api-key-file <路径>")
        return 0

    if args.provider == "real" and not os.environ.get("AGENTRUNTIME_API_KEY") and not args.api_key_file:
        print("[实验] 未做：既无 AGENTRUNTIME_API_KEY，也未给 --api-key-file。", file=sys.stderr)
        return 2

    records: list[dict] = []
    for group in GROUPS:
        print(f"[实验] 跑 {group.upper()} 组…", flush=True)
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

#!/usr/bin/env bash
# Agent Runtime Benchmark 启动器（尺子）。
#
#   ./run-bench.sh --plan                       # 只列计划，不花钱
#   ./run-bench.sh --suite T01,T02 --label t01  # 只跑指定场景
#   ./run-bench.sh                              # 跑全部场景并落盘
#
# 用 Release 构建：计时更接近真实，且与 Runtime 的 Debug/Release 无关（两边同构）。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "找不到 dotnet：请确认 ~/.local/bin 在 PATH 里（或直接用 ~/.dotnet/dotnet）。" >&2
  exit 2
fi

exec dotnet run --project "$HERE/AgentRuntime.Benchmark" -c Release -- \
  --config "$HERE/AgentRuntime.Benchmark/benchmark.config.json" "$@"

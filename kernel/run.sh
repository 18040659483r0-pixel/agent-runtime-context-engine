#!/usr/bin/env bash
# AgentRuntime V0 启动器 —— 让「开个终端就能用」成立。
#
# 用法：
#   ./run.sh "你好"                     # 一句话进，一句话出
#   ./run.sh --verbose "你好"           # 追加 token / 耗时诊断
#   ./run.sh --config <path> "你好"     # 换配置（例如 config.mock.json）
#   echo "你好" | ./run.sh              # 从 stdin 读
#
# 说明：默认配置文件显式传在最前，用户自己传的 --config 在最后，后者覆盖前者。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "找不到 dotnet：请确认 ~/.local/bin 在 PATH 里（或直接用 ~/.dotnet/dotnet）。" >&2
  exit 2
fi

exec dotnet run --project "$HERE/src/AgentRuntime.Cli" -- \
  --config "$HERE/src/AgentRuntime.Cli/config.json" "$@"

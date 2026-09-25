#!/bin/sh
# AgentRuntime TUI —— 真机日常启动器（**cwd 无关**）
#
# 为什么需要它：TUI 启动命令里有两个**相对路径**（`bin/Debug/net10.0/…` 与
# `--config ../AgentRuntime.Cli/…`），它们都按**你敲命令时所在的目录**解析。
# 换个目录敲（仓库根 / 家目录 / 别的工作区）就会报「找不到指定的命令或文件」——
# 而 dll 其实好好地在。这不是编译问题，是**目录问题**。
#
# 本脚本先把当前目录切到**自己所在目录**（= 面板相对路径的唯一基准），再执行。
#
# 用法：
#   sh <仓库>/projects/AgentRuntime/src/AgentRuntime.Tui/run.sh              # 默认真机日常配置
#   sh …/run.sh --verbose                                                    # 追加开关（原样传给 TUI）
#   sh …/run.sh --config ../AgentRuntime.Cli/config.private.json             # 自带 --config 时不覆盖
set -eu

here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

dll=bin/Debug/net10.0/AgentRuntime.Tui.dll

if [ ! -f "$dll" ]; then
  echo "run.sh: 找不到 $dll" >&2
  echo "  先编译：dotnet build \"$here/AgentRuntime.Tui.csproj\"" >&2
  exit 2
fi

# 默认配置：真机日常（真模型 + 真语料 + 工具面 + 审批）。调用方自带 --config 时不覆盖。
cfg=../AgentRuntime.Cli/config.stage1.json
for a in "$@"; do
  if [ "$a" = "--config" ]; then
    cfg=""
  fi
done

if [ -n "$cfg" ]; then
  exec dotnet "$dll" --config "$cfg" "$@"
else
  exec dotnet "$dll" "$@"
fi

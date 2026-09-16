#!/bin/sh
# AgentRuntime TUI —— 零成本端到端演示（**不花钱、不要密钥**）
#
# 产出三样：
#   ① plain 模式逐字转录（REPL + 纯文本面板）        → docs/TUI-DEMO-SAMPLE.md §三
#   ② split 模式一帧 · 笔记本半屏目标 96×30          → docs/TUI-DEMO-SAMPLE.md §七
#   ③ split 模式一帧 · 下限 80×24（证明不垮）         → docs/TUI-DEMO-SAMPLE.md §八
#
# 做法：起本地 mock provider（tools/mock-provider）→ 把 demo-input.txt / demo-frame-input.txt 喂给 TUI。
# 全程不碰真机 API：没有密钥、不花钱。
#
# 用法：sh src/AgentRuntime.Tui/demo.sh [转录输出] [96x30 帧输出] [80x24 帧输出]
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
out=${1:-"$here/.demo/transcript.txt"}
frame96=${2:-"$here/.demo/frame-96x30.txt"}
frame80=${3:-"$here/.demo/frame-80x24.txt"}

# 固定工作目录：面板里的相对路径（如 /stack dump .demo/x.txt）才有唯一解释。
cd "$here"

# 本地 mock：明文密钥是占位符（不是真密钥；mock 不校验 Authorization）。
export AGENTRUNTIME_MOCK_API_KEY=***

mkdir -p "$here/.demo"

if ! curl -s -m 2 -o /dev/null -X POST http://127.0.0.1:8899/v1/chat/completions \
      -H 'Content-Type: application/json' -d '{"model":"m","messages":[{"role":"user","content":"ping"}]}'; then
  python3 "$root/tools/mock-provider/mock_server.py" > "$here/.demo/mock.log" 2>&1 &
  sleep 1
  echo "[demo] 已起 mock provider：127.0.0.1:8899（日志 .demo/mock.log）" >&2
fi

# 清干净演示状态：只删 .demo 下的产物（流 / 快照 / 白板 / 草稿 / 焦点 / 水位线 / 分叉产物）。
# 注意：**不动 stack-dump.txt** —— 它是 plain 阶段的产物，后面两帧重跑状态时不能把它一起删掉。
reset_state() {
  rm -rf "$here/.demo/stream.jsonl" "$here/.demo/snapshot.json" "$here/.demo/focus.json" \
         "$here/.demo/watermark.json" "$here/.demo/tail" "$here/.demo/draft" \
         "$here/.demo/forked-stream.jsonl"
}

tui=bin/Debug/net10.0/AgentRuntime.Tui.dll

# ① plain 转录（先把上一轮的 dump 清掉，避免陈旧文件冒充本次产物）
rm -f "$here/.demo/stack-dump.txt"
reset_state
dotnet "$tui" --config "$here/config.demo.json" --focus E002 \
  < "$here/demo-input.txt" > "$out" 2>&1

# ② split 一帧（笔记本半屏目标 96×30）
reset_state
dotnet "$tui" --config "$here/config.demo.json" --focus E002 \
  --ui split --snapshot "$frame96" --frame-size 96x30 \
  < "$here/demo-frame-input.txt" >> "$out" 2>&1

# ③ split 一帧（下限 80×24：状态块压缩、仍不错位）
reset_state
dotnet "$tui" --config "$here/config.demo.json" --focus E002 \
  --ui split --snapshot "$frame80" --cols 80 --rows 24 \
  < "$here/demo-frame-input.txt" >> "$out" 2>&1

echo "[demo] 转录：$out" >&2
echo "[demo] 双栏一帧 96×30：$frame96" >&2
echo "[demo] 双栏一帧 80×24：$frame80" >&2

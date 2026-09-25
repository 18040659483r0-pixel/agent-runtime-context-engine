#!/bin/sh
# verify-demo.sh —— 把「文档帧 == 产物帧」从散文声明变成可执行检查。
#
# 从 docs/TUI-DEMO-SAMPLE.md 抽出 ```text 帧块（以 ┌─ AgentRuntime TUI 开头的双栏帧），
# 与 src/AgentRuntime.Tui/.demo/frame-{96x30,80x24}.txt 逐行比对。
# 墙钟数值（[数字]ms）先归一化为 `ms`；其余任何差异 ⇒ exit 1 并打印 diff。
# 全对 ⇒ 打印 [VERIFY] 帧与文档一致（墙钟已归一化） + exit 0。
#
# 用法：sh src/AgentRuntime.Tui/verify-demo.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
doc="$root/docs/TUI-DEMO-SAMPLE.md"

python3 - "$doc" "$here/.demo/frame-96x30.txt" "$here/.demo/frame-80x24.txt" <<'PY'
import re
import sys

doc_path, f96_path, f80_path = sys.argv[1:4]

doc = open(doc_path, encoding="utf-8").read()
# 抽出所有 ```text 块，只留以 ┌─ AgentRuntime TUI 开头的（= 双栏帧，不含转录/输入）。
blocks = re.findall(r"```text\n(.*?)\n```", doc, flags=re.DOTALL)
frames = [b for b in blocks if b.startswith("┌─ AgentRuntime TUI")]

def norm(text):
    # 墙钟数值归一化：任意 [数字(.数字)]ms → ms（其余字节不动）。
    return re.sub(r"[0-9]+(?:\.[0-9]+)?\s*ms", "ms", text)

def file_text(path):
    return open(path, encoding="utf-8").read()

expected = [file_text(f96_path), file_text(f80_path)]
labels = ["96×30", "80×24"]

if len(frames) != len(expected):
    print(f"[VERIFY] 文档里找到 {len(frames)} 个帧块，期望 {len(expected)} 个", file=sys.stderr)
    sys.exit(1)

bad = False
for label, got_block, exp in zip(labels, frames, expected):
    got_lines = got_block.rstrip("\n").split("\n")
    exp_lines = exp.rstrip("\n").split("\n")
    if len(got_lines) != len(exp_lines):
        print(f"[VERIFY] {label} 行数不一致：文档 {len(got_lines)} vs 产物 {len(exp_lines)}", file=sys.stderr)
        bad = True
        continue
    for i, (g, e) in enumerate(zip(got_lines, exp_lines)):
        if norm(g) != norm(e):
            print(f"[VERIFY] {label} 第 {i+1} 行不一致：", file=sys.stderr)
            print(f"  文档: {g!r}", file=sys.stderr)
            print(f"  产物: {e!r}", file=sys.stderr)
            bad = True

if bad:
    sys.exit(1)

print("[VERIFY] 帧与文档一致（墙钟已归一化）")
sys.exit(0)
PY

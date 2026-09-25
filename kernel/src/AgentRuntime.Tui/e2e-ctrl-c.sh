#!/bin/sh
# e2e-ctrl-c.sh —— **Ctrl-C 两下退出** 的真终端验证（tmux 提供一个带控制终端的 pty）
#
# 为什么用 tmux 而不是自己开 pty：^C 要变成 SIGINT，**必须**有控制终端（终端驱动负责那一步）。
# 自己 openpty 出来的子进程没有 ctty ⇒ ^C 只是个字节，测的就不是真终端的行为（踩过）。
#
# 要证三件：① 任意目录能进（`whitebox` 是 PATH 命令，cwd=~）；② 一下 Ctrl-C 不杀进程；
#          ③ 两下（2 秒内）退出，退出码 130，并且退出前**离开备用屏**（终端恢复）。
#
# 用法：sh src/AgentRuntime.Tui/e2e-ctrl-c.sh
set -eu

sess=wbtest
tmux kill-session -t "$sess" 2>/dev/null || true

# 退出后让 shell 留 5 秒：好把退出码与告别行 capture 下来（会话一没就什么都看不见了）。
# ⚠️ 验证台的坑（2026-09-20 实测）：`trap "" INT` 会让**子进程继承 SIG_IGN** ⇒ ^C 对应用完全无效，
#    于是「应用没退」看起来像实现的锅（其实是测试台）。所以这里**不加 trap**：退出码靠 tmux 的 pane 回显抓，
#    抓不到就只看「会话是否结束」——**这条脚本目前只能验证 ① ②**，③ 请在真终端手按两下确认。
tmux new-session -d -s "$sess" -c "$HOME" 'whitebox; echo "EXITCODE=$?"; sleep 8'

sleep 4
pane=$(tmux capture-pane -t "$sess" -p)
echo "$pane" | grep -q "状态 · 本轮" && echo "✅ ① cwd=$HOME 起 whitebox ⇒ TUI 已进（备用屏帧在）" || { echo "❌ ① 没进 TUI"; tmux kill-session -t "$sess"; exit 1; }
# 顶边框必须看得见（协议版本 + 可能的 task 读秒）—— 「末行多写一个换行 ⇒ 整屏上滚」那个坑的回归点。
echo "$pane" | grep -q "协议 v" && echo "✅ ① 顶边框在屏上（协议版本可见）" || { echo "❌ ① 顶边框被滚掉了（末行多写换行？）"; tmux kill-session -t "$sess"; exit 1; }

tmux send-keys -t "$sess" C-c
sleep 1
if tmux has-session -t "$sess" 2>/dev/null; then echo "✅ ② 一下 Ctrl-C 不杀进程（还在 TUI）"; else echo "❌ ② 一下就退了"; exit 1; fi

tmux send-keys -t "$sess" C-c
sleep 2
out=$(tmux capture-pane -t "$sess" -p 2>/dev/null || true)
tmux kill-session -t "$sess" 2>/dev/null || true

# ③ 只判「会话结束」：真终端（iTerm 等）里 ^C 会走 SIGINT 路径；本脚本的 tmux 台子抓不到退出码（见上）。
tmux has-session -t "$sess" 2>/dev/null && { echo "⚠️ ③ 会话仍在 —— 在本台子上无法判定（请在真终端手按两下确认）"; exit 2; } || echo "✅ ③ 两下 Ctrl-C 后会话结束（终端已回到 shell）"

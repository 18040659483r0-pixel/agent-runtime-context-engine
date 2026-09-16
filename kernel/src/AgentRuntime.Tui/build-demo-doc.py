#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""**把演示产物嵌回文档**（`docs/TUI-DEMO-SAMPLE.md`）—— 让「文档 == 产物」可一键重建。

为什么需要它：文档里嵌的四段**就是产物文件**（逐字转录 / `/stack dump` / 两帧），
但文档是手维护的 ⇒ 协议或区栈一变，文档就**静默落后**（实测：协议升 v7 后，
文档帧还写着 `协议 v6`、R1-P `1,106` ⇒ `verify-demo.sh` exit 1，而没人生成过它）。

用法：
    python3 src/AgentRuntime.Tui/build-demo-doc.py            # 用 .demo/ 产物重建文档四段
    python3 src/AgentRuntime.Tui/build-demo-doc.py --check     # 只检查是否已一致（不写）

纪律：**只动这四段围栏内的正文**；围栏外的散文一律原样保留（散文里的数字要人自己看，
`--check` 会把「散文里还留着旧数字」一并报出来当提示）。
"""
from __future__ import annotations

import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
DOC = ROOT / "docs/TUI-DEMO-SAMPLE.md"
DEMO = HERE / ".demo"

# 段 → (标题锚点, 下个标题, 产物文件, 预期行数说明)
# 前两段在**同一节**（§二）里，用 idx 选第几个 ```text 围栏；其余各占一节。
SEGMENTS = [
    ("## 二、", "## 三、", "../demo-input.txt", 0, None),
    ("## 二、", "## 三、", "../demo-frame-input.txt", 1, None),
    ("## 三、", "## 四、", "transcript.txt", 0, None),
    ("## 六、", "## 七、", "stack-dump.txt", 0, "23"),
    ("## 七、", "## 八、", "frame-96x30.txt", 0, None),
    ("## 八、", "## 九、", "frame-80x24.txt", 0, None),
]

# 散文里不该再出现的旧值（`--check` 用它提示；替换清单也用它）
STALE = ["协议 v6", "1,106", "1106", "2,269", "567 token", "0186f2"]


def slice_block(doc: str, start_mark: str, end_mark: str, idx: int = 0) -> tuple[int, int, str]:
    """返回 (正文起点, 正文终点, 正文)：某节里**第 idx 个（0 基）```text 围栏**的内容。"""
    s = doc.index(start_mark)
    e = doc.index(end_mark, s)
    section = doc[s:e]
    ms = list(re.finditer(r"```text\n(.*?)\n```", section, flags=re.DOTALL))
    if len(ms) <= idx:
        raise SystemExit("[DOC] %s 节里找不到第 %d 个 ```text 围栏" % (start_mark, idx))
    m = ms[idx]
    return s + m.start(1), s + m.end(1), m.group(1)


def render(doc: str) -> tuple[str, list[str]]:
    notes = []
    for start_mark, end_mark, product, idx, expect_lines in SEGMENTS:
        path = DEMO / product
        if not path.exists():
            raise SystemExit("[DOC] 缺产物 %s —— 先跑 `sh src/AgentRuntime.Tui/demo.sh`" % path)
        want = path.read_text(encoding="utf-8").rstrip("\n")

        if expect_lines is not None:
            got = want.count("\n") + 1
            if str(got) != expect_lines:
                notes.append("[DOC] ⚠️ %s 现有 %d 行（文档标题里写的是 %s 行，请人工核对）"
                             % (product, got, expect_lines))

        begin, end, current = slice_block(doc, start_mark, end_mark, idx)
        label = "%s[%d]" % (start_mark.strip("、# "), idx)
        if current.rstrip("\n") != want:
            notes.append("[DOC] 替换 %s ← %s（%d 行）" % (label, product, want.count("\n") + 1))
            doc = doc[:begin] + want + doc[end:]
        else:
            notes.append("[DOC] %s 已与 %s 一致" % (label, product))
    return doc, notes


def main() -> int:
    check = "--check" in sys.argv
    doc = DOC.read_text(encoding="utf-8")
    new, notes = render(doc)

    stale = [s for s in STALE if s in new]
    for n in notes:
        print(n)
    if stale:
        print("[DOC] ⚠️ 正文里仍有旧值（散文要人改）：%s" % ", ".join(stale))

    if check:
        if new != doc:
            print("[DOC] 文档与产物不一致 ⇒ 跑 `python3 src/AgentRuntime.Tui/build-demo-doc.py`", file=sys.stderr)
            return 1
        if stale:
            print("[DOC] 围栏已一致，但散文仍有旧值 ⇒ 见上", file=sys.stderr)
            return 1
        print("[DOC] 文档与产物一致 ✅")
        return 0

    if new != doc:
        DOC.write_text(new, encoding="utf-8")
        print("[DOC] 已重建：%s" % DOC.relative_to(ROOT))
    else:
        print("[DOC] 无变化")
    return 0


if __name__ == "__main__":
    sys.exit(main())

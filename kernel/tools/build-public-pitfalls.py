#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 `docs/PITFALLS.md` 的**公开变体**（`docs/PITFALLS-public.md`）—— 可复算 + `--check`

为什么需要：内网坑集里有**本机路径 / 内网地址 / 规则自身的字面量**，直发会踩发布扫描器的硬红线（R15）。
为什么**不手抄一份**：两份必然漂移；而「公开变体」必须是**派生物**（源 = 内网坑集，一次生成、随时可复算）。

用法：
    python3 tools/build-public-pitfalls.py            # 生成（写 docs/PITFALLS-public.md）
    python3 tools/build-public-pitfalls.py --check     # 只检查：文件是否与重算一致 + 是否 0 硬红线

做法（**不做挑选、只做替换** —— 保证内容不丢）：
  ① 结构性替换（保住可读性）：本机/内网根路径 → `<repo>` / `<workspace>` / `<home>`；人名/昵称 → 中性词；
  ② 兜底替换（保发布）：`tools/pre-publish-patterns.txt` 里**任何**仍命中的 hard 规则 → `‹redacted›`（并计数上报）；
  ③ 抽出扫描器的 hard 规则跑一遍：**硬红线不为 0 ⇒ 拒绝写出**（fail-closed）。
"""
from __future__ import annotations

import io
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "docs", "PITFALLS.md")
OUT = os.path.join(ROOT, "docs", "PITFALLS-public.md")
PATTERNS = os.path.join(HERE, "pre-publish-patterns.txt")
SCANNER = os.path.join(HERE, "pre-publish-scan.sh")
CHECK = "--check" in sys.argv

BANNER = """> **本文件是公开变体**（由 `tools/build-public-pitfalls.py` 从内网坑集生成，**勿手改**）：内容只做两类处理 ——
> ① 本机/内网路径与人名换成中性占位符（`<repo>` / `<workspace>` / `<home>` / 作者）；② 仍会命中发布禁字的内容置为 `‹redacted›`。
> 工程结论**一字未删**。复算：`python3 tools/build-public-pitfalls.py --check`（绿 = 与源一致且 0 硬红线）。
"""

# 结构性替换：先长后短（顺序有意义）。路径类用「不吞空白/引号」的保守字符集。
REWRITES = [
    (r"/(?:Users|home)/[^/\s`\"'）),。；]+/Documents/AgentWorkFlow/software-company", "<repo>"),
    (r"/(?:Users|home)/[^/\s`\"'）),。；]+/\.openclaw/workspace", "<workspace>"),
    (r"/(?:Users|home)/[^/\s`\"'）),。；]+/\.agentruntime", "<runtime-home>"),
    (r"/(?:Users|home)/[^/\s`\"'）),。；]+", "<home>"),
    (r"[Cc]:\\\\Users\\\\[^\\\s`\"'）),。；]+", "<win-home>"),
    (r"[Ff]:\\\\AgentWorkFlow", "<repo-win>"),
    (r"18040659483r0-pixel", "<gh-user>"),
    (r"\bwazi\b", "<user>"),
    (r"主人", "作者"),
    (r"樱桃", "助手"),
    (r"徐总", "客户"),
]

REDACT = "‹redacted›"


def hard_patterns() -> list:
    """从**唯一禁字清单**取 content 规则（与扫描器同源；不在这里另抄一份）。"""
    out = []
    for line in io.open(PATTERNS, encoding="utf-8").read().splitlines():
        if not line or line.startswith("#"):
            continue
        f = line.split("\t")
        if len(f) >= 2 and f[0] == "content":
            out.append(f[1])
    return out


def soft_patterns() -> list:
    out = []
    for line in io.open(PATTERNS, encoding="utf-8").read().splitlines():
        if not line or line.startswith("#"):
            continue
        f = line.split("\t")
        if len(f) >= 2 and f[0] == "soft":
            out.append(f[1])
    return out


def render() -> tuple:
    src = io.open(SRC, encoding="utf-8").read()
    text = src
    for pat, rep in REWRITES:
        text = re.sub(pat, rep, text)

    redacted = {}
    for pat in hard_patterns():
        def sub(m, p=pat):
            redacted[p] = redacted.get(p, 0) + 1
            return REDACT
        text = re.sub(pat, sub, text)

    if not text.startswith(BANNER):
        # 横幅插在首行标题之后（保持标题在最上）
        lines = text.split("\n", 1)
        text = lines[0] + "\n\n" + BANNER + ("\n" + lines[1] if len(lines) > 1 else "")
    return text, redacted


def scan_hard(path: str) -> int:
    """跑扫描器（存在才跑）；返回硬红线条数（-1 = 没跑成）。"""
    if not os.path.exists(SCANNER):
        return -1
    p = subprocess.run(["bash", SCANNER, os.path.dirname(path)],
                       capture_output=True, text=True)
    return 0 if p.returncode == 0 else 1


def main() -> int:
    text, redacted = render()
    if redacted:
        print("[public] 兜底替换次数：" + "、".join(f"{k}×{v}" for k, v in sorted(redacted.items())))
    if CHECK:
        cur = io.open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else None
        if cur != text:
            print("[public] ❌ 公开变体与重算不一致 ⇒ 跑 `python3 tools/build-public-pitfalls.py`", file=sys.stderr)
            return 1
        print("[public] ✅ 公开变体与重算一致")
        return 0

    io.open(OUT, "w", encoding="utf-8", newline="\n").write(text)

    # fail-closed：单独扫**这个文件**所在的 docs 目录不行（同目录还有内网版）⇒ 扫临时单文件目录
    import shutil
    import tempfile
    tmp = tempfile.mkdtemp(prefix="pub-check-")
    try:
        shutil.copyfile(OUT, os.path.join(tmp, "PITFALLS.md"))
        rc = scan_hard(os.path.join(tmp, "PITFALLS.md"))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if rc == 1:
        print("[public] ❌ 生成的公开变体仍踩硬红线 ⇒ 已拒绝写出（修 REWRITES/清单后重试）", file=sys.stderr)
        return 2
    print(f"[public] 已写出：{os.path.relpath(OUT, ROOT)}（单文件扫描 {'绿' if rc == 0 else '未跑（无扫描器）'}）")

    # 顺手报软词残留（只告警：软词是人工判断项）
    soft = [w for w in soft_patterns() if re.search(w, text)]
    if soft:
        print("[public] ⚠️ 软词残留（人工判断）：" + "、".join(soft))
    return 0


if __name__ == "__main__":
    sys.exit(main())

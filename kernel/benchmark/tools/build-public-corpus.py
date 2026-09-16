#!/usr/bin/env python3
"""从真实工程资料生成『可公开』的 benchmark 语料（去隐私 + 按字符预算切片）。

用法：
    python3 tools/build-public-corpus.py                    # 默认读 ~/.openclaw/workspace
    python3 tools/build-public-corpus.py --workspace <dir>  # 换源
    python3 tools/build-public-corpus.py --check            # 只扫描现有产物，不重建

产出（约 10k token 档，与 benchmark.config.json 的 tiers 对齐）：
    AgentRuntime.Benchmark/corpus/rules.md      ← AGENTS.md        （铁则）
    AgentRuntime.Benchmark/corpus/knowledge.md  ← METHODOLOGY.md    （抽象方法论）
    AgentRuntime.Benchmark/corpus/memory.md     ← memory/INDEX.md   （记忆索引）

铁则：
  1. 只做**部分**注入（按预算切片），不整份搬运；
  2. 一切隐私（人名 / 本机路径 / 内网地址 / 账号 / 口令 / 令牌 / 电话 / 网盘码 / 金额）→ 占位符；
  3. 产物是**可公开**的（供他人复现），构建后必须自检 0 命中。
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

# (正则, 替换) —— 顺序即优先级（先路径、后标识、再人名）
SCRUBS: list[tuple[str, str]] = [
    (r"/Users/[A-Za-z0-9_.\-]+/\.openclaw/workspace", "<workspace>"),
    (r"/Users/[A-Za-z0-9_.\-]+/Documents/[A-Za-z0-9_./\-]*", "<工作副本>"),
    (r"/Users/[A-Za-z0-9_.\-]+", "<用户目录>"),
    (r"C:\\\\Users\\\\[A-Za-z0-9_.\-]+", "<用户目录>"),
    (r"C:\\\\Mac\\\\Home\\\\Documents\\\\AgentWorkFlow", "<工作副本>"),
    (r"F:\\\\AgentWorkFlow", "<工作副本>"),
    (r"\b\d{1,3}(?:\.\d{1,3}){3}\b", "<内网地址>"),
    (r"\bxuxinyue\b", "<账号>"),
    (r"\bnm-[0-9a-f]{8,}\b", "<口令>"),
    (r"\bNM-[0-9a-f]{8,}\b", "<节点id>"),
    (r"18040659483r0-pixel", "<github-user>"),
    (r"\b1[3-9]\d{9}\b", "<电话>"),
    (r"sk-[A-Za-z0-9_\-]{8,}", "[REDACTED]"),
    (r"github_pat_[A-Za-z0-9_]+", "[REDACTED]"),
    (r"ghp_[A-Za-z0-9]+", "[REDACTED]"),
    (r"https?://pan\.baidu\.com/\S+", "<网盘链接>"),
    (r"提取码[:：]?\s*\S+", "提取码:<略>"),
    (r"\d[\d,]*\.?\d*\s*元", "<金额>"),
    (r"徐总", "用户"),
    (r"主人", "用户"),
    (r"樱桃", "助手"),
    (r"\bwazi\b", "user"),
    (r"\b001\b", "WIN端"),
]

# 源 → 产物 + 字符预算（≈10k token 档；与 benchmark.config.json 的 tiers 对齐）
SOURCES = [
    ("rules", "AGENTS.md", 4406),
    ("knowledge", "METHODOLOGY.md", 8686),
    ("memory", os.path.join("memory", "INDEX.md"), 5600),
]

# 发布前自检：产物中绝不允许出现
LEAK_PATTERNS = [
    r"/Users/", r"C:\\\\", r"F:\\\\", r"\b\d{1,3}(?:\.\d{1,3}){3}\b",
    r"\bxuxinyue\b", r"\bnm-[0-9a-f]{8,}", r"\bNM-[0-9a-f]{8,}",
    r"\bsk-[A-Za-z0-9_\-]{8,}", r"\bghp_[A-Za-z0-9]{8,}", r"\bgithub_pat_[A-Za-z0-9_]{8,}",
    r"\b1[3-9]\d{9}\b", r"徐总", r"主人", r"樱桃", r"\bwazi\b", r"pan\.baidu\.com",
]


def slice_text(text: str, budget: int) -> str:
    """按字符预算切片，并回退到最后一个换行，避免截断半句。"""
    if len(text) <= budget:
        return text
    cut = text[:budget]
    nl = cut.rfind("\n")
    return cut[:nl] if nl > 0 else cut


def scrub(text: str) -> str:
    for pattern, replacement in SCRUBS:
        text = re.sub(pattern, replacement, text)
    return text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", default=os.path.expanduser("~/.openclaw/workspace"))
    parser.add_argument("--out", default=None)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    here = Path(__file__).resolve().parent
    out_dir = Path(args.out) if args.out else here.parent / "AgentRuntime.Benchmark" / "corpus"
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.check:
        return check(out_dir)

    total = 0
    for name, relative, budget in SOURCES:
        source = Path(args.workspace) / relative
        if not source.exists():
            print(f"[语料] 源文件不存在：{source}", file=sys.stderr)
            return 2

        text = source.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")
        text = scrub(text)
        text = slice_text(text, budget)
        target = out_dir / f"{name}.md"
        target.write_text(text, encoding="utf-8")
        print(f"[语料] {name:9s} ← {relative:16s} {len(text):6d} 字符 → {target}")
        total += len(text)

    print(f"[语料] 合计 {total} 字符（≈ {total / 1.88:.0f} token，按实测 ~1.88 字符/token 折算）")

    return check(out_dir)


def check(out_dir: Path) -> int:
    """自检：产物中不得出现任何隐私模式。"""
    hits = 0
    for file in sorted(out_dir.glob("*.md")):
        text = file.read_text(encoding="utf-8")
        for pattern in LEAK_PATTERNS:
            for match in re.finditer(pattern, text):
                hits += 1
                print(f"[自检] {file.name}: 命中 {pattern} → {match.group(0)!r}", file=sys.stderr)

    if hits:
        print(f"[自检] ❌ 发现 {hits} 处疑似隐私，禁止发布。", file=sys.stderr)
        return 1

    print("[自检] ✅ 未发现隐私模式，产物可公开。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

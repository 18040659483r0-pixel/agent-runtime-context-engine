#!/usr/bin/env python3
"""sync-demo-doc.py —— `docs/TUI-DEMO-SAMPLE.md` 的**产物块同步器**（生成 + 自检）。

为什么需要它（`skills/generated-view-sync`）：文档里嵌的是 **harness 真写出来的产物**
（转录 / 帧 / 落盘表），上游一变（协议 / 回复 / 版式），文档里的字节就悄悄落后；
而 `verify-demo.sh` 那时正红着 —— 红着的闸门和绿的闸门看起来是一样的。

分工：
- **本脚本**：按「节标题 + 该节内第几个 ```text 块」定位，**只换围栏里的正文**，散文一字不动；`--check` 只报不改。
- **`verify-demo.sh`**：独立复验（帧逐行比对，墙钟归一化）—— 两者成对使用。

用法：
    python3 sync-demo-doc.py --check     # 有不一致 ⇒ 逐块打印 diff + exit 1
    python3 sync-demo-doc.py --write     # 把产物拼回去（散文不动）
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
DOC = ROOT / "docs" / "TUI-DEMO-SAMPLE.md"
DEMO = HERE / ".demo"

#: （节标题前缀, 该节内 ```text 块序号, 产物文件）
BLOCKS: list[tuple[str, int, pathlib.Path]] = [
    ("## 二、", 0, HERE / "demo-input.txt"),
    ("## 二、", 1, HERE / "demo-frame-input.txt"),
    ("## 三、", 0, DEMO / "transcript.txt"),
    ("## 六、", 0, DEMO / "stack-dump.txt"),
    ("## 七、", 0, DEMO / "frame-96x30.txt"),
    ("## 八、", 0, DEMO / "frame-80x24.txt"),
]

#: 散文里引用的数字（改产物不改散文 = 悄悄说谎）：(节标题前缀, 正则, 从产物算出的真值函数名)
QUOTED = [
    ("## 六、", r"那 (\d+) 行", lambda p: len(p.read_text(encoding="utf-8").rstrip("\n").split("\n"))),
]


#: 墙钟归一化：任意 `[数字(.数字)]ms` → `ms`（**与 `verify-demo.sh` 同一正则**）。
#: 为什么：产物里嵌的是**这一次跑出来的耗时**（7.5ms / 6.4ms …）⇒ 不归一化则每跑一次演示，
#: 文档就必然落后一版，于是每轮都要提交一次**毫无意义的 doc 改动**（2026-09-22 主人令顺手修）。
WALLCLOCK = re.compile(r"[0-9]+(?:\.[0-9]+)?\s*ms")

#: `/hits` 汇总表的**末两列**（`开销ms` / `总ms`）也是墙钟，但**值不带单位** ⇒ 上一条正则够不着。
#: 实测（2026-09-24）：每跑一次演示这两列必变（6.7/30.4 → 7.2/31.7）⇒ `--check` 恒红（§十·51）。
#: 按**整行形状**认（前七列是确定性的），末两列换成**同宽 `-`**：保列对齐，且看得出那里是数值列。
HITS_ROW = re.compile(
    r"^(?P<head>\s*\d+\s+\d+\s+\d+\s+\d+\s+[0-9.]+%\s+\d+\s+\d+)"
    r"(?P<wall>\s+[0-9.]+\s+[0-9.]+)\s*$",
    re.M,
)


def _dash_wall(m):
    return m.group("head") + re.sub(r"[0-9.]", "-", m.group("wall"))


def norm(text: str) -> str:
    return HITS_ROW.sub(_dash_wall, WALLCLOCK.sub("ms", text))


def load_product(path: pathlib.Path) -> list[str]:
    """产物不存在 ⇒ **大声死掉**（不许写空块 —— 空块看起来像成功）。"""
    if not path.is_file():
        sys.exit(f"[sync] 产物不存在：{path}\n       先跑 `sh src/AgentRuntime.Tui/demo.sh` 再生一次。")
    return norm(path.read_text(encoding="utf-8")).rstrip("\n").split("\n")


def find_block(lines: list[str], section: str, index: int, where: str) -> tuple[int, int]:
    """返回 (围栏体首行下标, 体末行下标-1)；找不到 ⇒ 抛错（错的下标会静默改写别的块）。"""
    start = next((i for i, line in enumerate(lines) if line.startswith(section)), None)
    if start is None:
        raise SystemExit(f"[sync] 文档里找不到节：{section}（{where}）")

    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    seen = 0
    i = start
    while i < end:
        if lines[i].strip() == "```text":
            body = i + 1
            close = next((j for j in range(body, end) if lines[j].strip() == "```"), None)
            if close is None:
                raise SystemExit(f"[sync] {section} 第 {seen} 个代码块没有收尾围栏（{where}）")
            if seen == index:
                return body, close - 1
            seen += 1
            i = close + 1
            continue
        i += 1

    raise SystemExit(f"[sync] {section} 里没有第 {index} 个 ```text 块（{where}）")


def main() -> int:
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="只报不改（有不一致 ⇒ exit 1）")
    mode.add_argument("--write", action="store_true", help="把产物拼回文档（只动围栏体）")
    args = parser.parse_args()

    text = DOC.read_text(encoding="utf-8")
    lines = text.split("\n")
    changed = 0

    for section, index, product in BLOCKS:
        want = load_product(product)
        first, last = find_block(lines, section, index, product.name)
        have = lines[first : last + 1]
        if have == want:
            print(f"[sync] 一致   {section} #{index} ← {product.name}（{len(want)} 行）")
            continue

        changed += 1
        print(
            f"[sync] {'替换' if args.write else '不一致'} {section} #{index} ← {product.name}"
            f"（文档 {len(have)} 行 / 产物 {len(want)} 行）",
            file=sys.stderr if args.check else sys.stdout,
        )
        if args.check:
            for i in range(max(len(have), len(want))):
                a = have[i] if i < len(have) else "<缺>"
                b = want[i] if i < len(want) else "<多>"
                if a != b:
                    print(f"   第 {i + 1} 行：\n     文档: {a!r}\n     产物: {b!r}", file=sys.stderr)
                    break
        else:
            lines[first : last + 1] = want

    # 散文里引用的数字：产物变了、散文没跟 ⇒ 只是警告（不阻塞），但要让人看见。
    for section, pattern, compute in QUOTED:
        start = next((i for i, line in enumerate(lines) if line.startswith(section)), None)
        if start is None:
            continue
        import re

        match = re.search(pattern, lines[start])
        source = next(p for s, _, p in BLOCKS if s == section)
        actual = compute(source)
        if match and match.group(1) != str(actual):
            print(
                f"[sync] ⚠️ STALE：{section} 散文写「{match.group(1)}」，产物实际 {actual}"
                f"（散文要人改：{source.name}）",
                file=sys.stderr,
            )

    if args.write and changed:
        DOC.write_text("\n".join(lines), encoding="utf-8")
        print(f"[sync] 已替换 {changed} 个块 → {DOC}")

    if args.check and changed:
        print(f"[sync] {changed} 个块不一致 ⇒ exit 1", file=sys.stderr)
        return 1

    print("[sync] ✅ 文档与产物一致" if not changed else "[sync] ✅ 已同步")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

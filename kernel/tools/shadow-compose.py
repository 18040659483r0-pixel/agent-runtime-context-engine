#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""影子模式 · **prompt 组成对照表**（常态化跑法 · 零模型调用）

主人 2026-09-16 02:41 定（QT5）：影子模式第一件事 = **prompt 组成对照表**。
本工具把它从「一次性实验」变成**一条命令 + 一道闸门**：

    python3 tools/shadow-compose.py            # 重算并写进结果文档的标记块（散文不动）
    python3 tools/shadow-compose.py --check     # 只校验：重算 == 文档里的块？（漂移 ⇒ exit 1）
    python3 tools/shadow-compose.py --print     # 只打印块（管道/人工看）

**为什么要这样**：数字写在散文里迟早会**静默落后**（Stage 0 那版就落后过；同族的
`TUI-DEMO-SAMPLE.md` 也是协议升 v7 后一直没重生）。⇒ 数字一律由本工具生成进**标记块**，
散文只写「为什么」。`--check` 让漂移**跑一次就现形**。

两侧口径（诚实说明，别混账）：
  · **[WB]（我们的内核）**：可脚本化导出**区栈表**（区 / 字节 / 指纹 sha256 前 12 / 版本 / 段数 / 模块）
    + **分段明细** + 合计指纹 —— 走 TUI 无头模式 + `/stack dump`，**不依赖任何模型调用**。
  · **[OC]（OpenClaw）**：**不暴露组装 trace** ⇒ 只给「注入了哪些文件」+ 字节 + 指纹（可测的部分）；
    平台固定开销（底座文本 / 工具 schema）属**灰箱**，沿用既有实测口径**单列**（`METHODOLOGY` §十·20：
    平台固定与自控开销分开记账）。

**只有共享语义段可比**：`rules` / `knowledge` / `memory-index`。其余段**不要求规模相同** ——
要求的是「每一段是什么、为什么」说得清。
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                              # projects/AgentRuntime
TUI = os.path.join(ROOT, "src/AgentRuntime.Tui")
DLL = os.path.join(TUI, "bin/Debug/net10.0/AgentRuntime.Tui.dll")
SHADOW_CFG = os.path.join(TUI, "config.shadow.json")      # 真语料（frozen-private）+ 全模块 + mock
DOC = os.path.join(ROOT, "docs/REHEARSAL-STAGE0-RESULT.md")

BEGIN = "<!-- shadow:compose begin"
END = "<!-- shadow:compose end -->"

OC_WS = os.path.expanduser("~/.openclaw/workspace")        # [OC] 宿主自己的语料根
WB_WS = os.path.join(ROOT, "whitebox", "workspace")        # [WB] **自持**语料根

# [OC] 那侧「被注入的文件」（可测的部分；只写**确实存在**的）
OC_INJECTED = ["AGENTS.md", "SOUL.md", "IDENTITY.md", "USER.md", "MEMORY.md"]

# [WB] 自持语料根里的文件（构建期输入，**运行期不读**）
WB_CORPUS = ["AGENTS.md", "SOUL.md", "IDENTITY.md", "USER.md", "GLOSSARY.md",
             "KNOWLEDGE.md", "MEMORY.md", "MEMORYINDEX.md", "knowledge/knowledge.md"]

# 可比段：[WB] 分段明细里的 id 前缀 → [OC] 对应物（None = 明确没有等价常驻）
COMPARABLE = [
    ("铁则 rules",         "rules.",      ["AGENTS.md", "SOUL.md", "IDENTITY.md", "USER.md"]),
    ("知识 knowledge",     "knowledge.",  None),
    ("记忆索引 memory-index", "memoryindex.", ["MEMORY.md"]),
]

# [OC] 平台固定开销（灰箱 · 既有实测口径 2026-09-15，单列不摊进明细）
OC_FIXED = [
    ("平台底座文本", 8800),
    ("工具 JSON schema", 14900),
    ("技能目录（name+description）", 2000),
    ("工作区额外（仓库识别等）", 3300),
]

ROW = re.compile(r"^(\d+)\s+(R1-P|R1|R2|R4|R5|R3)\s+(\d+)\s+([0-9a-f]+)\s+(\S+)\s+(\d+)\s+(\S+)\s*$")
# 分段明细行形如：`  · R1-P protocol.global @ 7 → 1323 字节 / 56687e7d8031`
# ⚠️ `·` 后面先有**区名**、才是**段 id**（第一版正则漏了区名 ⇒ 逐条不匹配、明细全空、三条可比段全显「缺」）。
DETAIL = re.compile(r"^\s*·\s+(\S+)\s+(\S+)\s+@\s+(\S+)\s+→\s+(\d+)\s+字节\s+/\s+([0-9a-f]+)\s*$")
TOTAL = re.compile(r"合计：(\d+)\s*字节\s*/\s*指纹\s*([0-9a-f]+)")
PROTO = re.compile(r"协议区版本：v(\d+)")


def sha12(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:12]


def group_fp(texts: list[str]) -> str:
    """组指纹口径：**按清单顺序拼接**后取 sha256 前 12（清单即口径，别改顺序）。"""
    return sha12("\n".join(texts))


def wb_dump() -> str:
    """跑一次无头 TUI，把区栈表抓回来（**零模型调用**）。"""
    dump = "/tmp/wb-shadow-stack.txt"
    if os.path.exists(dump):
        os.remove(dump)

    env = dict(os.environ, AGENTRUNTIME_MOCK_API_KEY="shadow")
    p = subprocess.run(
        ["dotnet", DLL, "--config", SHADOW_CFG, "--ui", "split",
         "--snapshot", "/tmp/wb-shadow-frame.txt", "--frame-size", "96x30"],
        input="/stack dump %s\n/quit\n" % dump,
        capture_output=True, text=True, cwd=TUI, env=env, timeout=180)

    if not os.path.exists(dump):
        print("[shadow] 没拿到区栈 dump；stderr 尾部：\n" + p.stderr[-800:], file=sys.stderr)
        raise SystemExit(2)
    return open(dump, encoding="utf-8").read()


def parse_wb(dump: str) -> dict:
    rows, details, total, proto = [], [], None, "?"
    for line in dump.splitlines():
        if (m := ROW.match(line)):
            rows.append({"seq": int(m.group(1)), "zone": m.group(2), "bytes": int(m.group(3)),
                         "fp": m.group(4), "version": m.group(5), "segments": int(m.group(6)),
                         "modules": m.group(7)})
        elif (m := DETAIL.match(line)):
            details.append({"zone": m.group(1), "id": m.group(2), "version": m.group(3),
                            "bytes": int(m.group(4)), "fp": m.group(5)})
        elif (m := TOTAL.search(line)):
            total = {"bytes": int(m.group(1)), "fp": m.group(2)}
        elif (m := PROTO.search(line)):
            proto = m.group(1)

    if not rows or total is None:
        raise SystemExit("[shadow] 区栈表解析失败（表格形状变了？）")
    return {"rows": rows, "details": details, "total": total, "protocol": proto}


def _rows(root: str, names: list[str]) -> list[dict]:
    out = []
    for name in names:
        path = os.path.join(root, name)
        if not os.path.exists(path):
            out.append({"name": name, "bytes": None, "fp": "（不存在）"})
            continue
        text = open(path, encoding="utf-8").read()
        out.append({"name": name, "bytes": len(text.encode("utf-8")), "fp": sha12(text), "text": text})
    return out


def comparable_row(label: str, prefix: str, oc_names: list[str] | None, wb: dict, oc: dict) -> str:
    seg = next((d for d in wb["details"] if d["id"].startswith(prefix)), None)
    wb_cell = f"`{seg['id']}` @ {seg['version']}" if seg else "**（缺）**"
    wb_b = f"{seg['bytes']:,}" if seg else "—"
    wb_f = f"`{seg['fp']}`" if seg else "—"

    if oc_names is None:
        return f"| {label} | {wb_cell} | {wb_b} | {wb_f} | **无等价常驻**（[OC] 按需 `read`） | — | — |"

    picked = [oc[n] for n in oc_names if n in oc]
    missing = [n for n in oc_names if n not in oc or oc[n]["bytes"] is None]
    total_b = sum(p["bytes"] for p in picked if p["bytes"])
    fp = group_fp([p["text"] for p in picked if p.get("text")])
    what = " + ".join(f"`{n}`" for n in oc_names if n not in missing) or "—"
    if missing:
        what += " ⚠️缺" + ",".join(missing)
    return f"| {label} | {wb_cell} | {wb_b} | {wb_f} | {what} | {total_b:,} | `{fp}` |"


def render_block() -> tuple[str, dict]:
    wb = parse_wb(wb_dump())
    oc_list = _rows(OC_WS, OC_INJECTED)
    oc = {r["name"]: r for r in oc_list}
    wb_corpus = _rows(WB_WS, WB_CORPUS)

    L = [BEGIN + "（tools/shadow-compose.py 生成；**勿手改** —— 跑 `--check` 看漂移） -->", ""]
    L += ["### [WB] 区栈（零模型调用 · TUI 无头 `/stack dump`）", "", "```text"]
    L += [f"{r['seq']:<5} {r['zone']:<6} {r['bytes']:<10} {r['fp']:<14} {r['version']:<10} "
          f"{r['segments']:<5} {r['modules']}" for r in wb["rows"]]
    L += [f"（合计 {wb['total']['bytes']:,} 字节 / 指纹 `{wb['total']['fp']}`；"
          f"协议 **v{wb['protocol']}**；配置 `config.shadow.json`）", "```", ""]

    L += ["### 可比段数值（[WB] 常驻段 vs [OC] 注入物）", "",
          "> 组指纹口径 = **按清单顺序拼接**后 sha256 前 12。", "",
          "| 语义段 | [WB] 段 @ 版本 | [WB] 字节 | [WB] 指纹 | [OC] 对应物 | [OC] 字节 | [OC] 组指纹 |",
          "|---|---|---|---|---|---|---|"]
    for label, prefix, oc_names in COMPARABLE:
        L.append(comparable_row(label, prefix, oc_names, wb, oc))
    L += ["", "### 分段明细（[WB] 侧，逐段可追）", "", "| 段 id @ 版本 | 字节 | 指纹 |", "|---|---|---|"]
    for d in wb["details"]:
        L.append(f"| `{d['id']}` @ {d['version']} | {d['bytes']:,} | `{d['fp']}` |")
    L += ["", "### [OC] 注入清单（可测部分）", "", "| 文件 | 字节 | 指纹(sha256 前 12) |", "|---|---|---|"]
    for r in oc_list:
        size = "（不存在）" if r["bytes"] is None else f"{r['bytes']:,}"
        L.append(f"| `{r['name']}` | {size} | `{r['fp']}` |")
    L += ["", "### [OC] 平台固定开销（灰箱 · 既有实测口径 · 单列不摊进明细）", "", "| 项 | ≈token |", "|---|---|"]
    for k, v in OC_FIXED:
        L.append(f"| {k} | {v:,} |")
    L += ["", "### [WB] 自持语料源（构建期输入，**运行期不读**）", "",
          "| 文件 | 字节 | 指纹(sha256 前 12) |", "|---|---|---|"]
    for r in wb_corpus:
        size = "（不存在）" if r["bytes"] is None else f"{r['bytes']:,}"
        L.append(f"| `{r['name']}` | {size} | `{r['fp']}` |")
    L += ["", END]

    meta = {"wb_total_fp": wb["total"]["fp"], "wb_total_bytes": wb["total"]["bytes"],
            "protocol": wb["protocol"], "missing_oc": [r["name"] for r in oc_list if r["bytes"] is None],
            "missing_wb_corpus": [r["name"] for r in wb_corpus if r["bytes"] is None],
            "missing_segments": [label for label, prefix, _ in COMPARABLE
                                 if not any(d["id"].startswith(prefix) for d in wb["details"])]}
    return "\n".join(L) + "\n", meta


def split_doc(text: str) -> tuple[str, str] | None:
    i = text.find(BEGIN)
    j = text.find(END)
    if i < 0 or j < 0:
        return None
    return text[:i], text[j + len(END):]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只校验（不写）")
    ap.add_argument("--print", dest="do_print", action="store_true", help="只打印块")
    args = ap.parse_args()

    block, meta = render_block()

    if args.do_print:
        sys.stdout.write(block)
        return 0

    doc = open(DOC, encoding="utf-8").read()
    parts = split_doc(doc)

    if args.check:
        if parts is None:
            print("[shadow] ❌ 文档里没有标记块 ⇒ 先跑 `python3 tools/shadow-compose.py`", file=sys.stderr)
            return 1
        head, tail = parts
        current = doc[len(head):len(doc) - len(tail)]
        if current.rstrip("\n") != block.rstrip("\n"):
            print("[shadow] ❌ 对照表已漂移（重算 ≠ 文档）⇒ 跑 `python3 tools/shadow-compose.py` 重生成。", file=sys.stderr)
            a, b = current.rstrip("\n").splitlines(), block.rstrip("\n").splitlines()
            for i in range(min(len(a), len(b))):
                if a[i] != b[i]:
                    print(f"  首个差异 第 {i + 1} 行：", file=sys.stderr)
                    print(f"    文档: {a[i]}", file=sys.stderr)
                    print(f"    重算: {b[i]}", file=sys.stderr)
                    break
            else:
                print(f"  行数不同：文档 {len(a)} vs 重算 {len(b)}", file=sys.stderr)
            return 1
        if meta["missing_oc"]:
            print("[shadow] ❌ [OC] 清单里有不存在的文件：%s" % ", ".join(meta["missing_oc"]), file=sys.stderr)
            return 1
        if meta["missing_segments"]:
            print("[shadow] ❌ 可比段在 [WB] 侧找不到对应段：%s（段消失/改名 ⇒ 对照失去意义）"
                  % ", ".join(meta["missing_segments"]), file=sys.stderr)
            return 1
        print("[shadow] ✅ 对照表与重算一致（[WB] 合计 %s 字节 / 指纹 %s；协议 v%s）"
              % (f"{meta['wb_total_bytes']:,}", meta["wb_total_fp"], meta["protocol"]))
        return 0

    if parts is None:
        print("[shadow] ❌ 文档里没有标记块；请先手工放一对 %s ... %s" % (BEGIN, END), file=sys.stderr)
        return 1
    head, tail = parts
    new = head + block + tail
    if new != doc:
        open(DOC, "w", encoding="utf-8", newline="\n").write(new)
        print("[shadow] 已重建标记块：%s" % os.path.relpath(DOC, ROOT))
    else:
        print("[shadow] 无变化")
    print("[shadow] [WB] 合计 %s 字节 / 指纹 %s；协议 v%s" % (f"{meta['wb_total_bytes']:,}",
                                                              meta["wb_total_fp"], meta["protocol"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())

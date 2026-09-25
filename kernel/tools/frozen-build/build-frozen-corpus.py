#!/usr/bin/env python3
"""确定性迁移器：把 openclaw 工作区里「有价值的内容」按 AgentRuntime 的
「区 / 层 / 域」格式汇成**冻结语料**（默认 `frozen-private/`）。

设计铁则（与 METHODOLOGY §十·7~10 一致）
========================================
1) **唯一声明处**：段清单 = 本文件顶部的 `SPEC`（`--spec` 可打印）。
2) **逐字节确定**：LF、无 BOM、无时间戳、索引按 id 排序（NFC 归一）。
3) **正文与账本分离**：版本头 `<!-- frozen: version=N -->` 只服务账本；
   `_manifest.json`（源哈希 / 字符数 / 全局指纹）**不进 prompt**。
4) **版本闸门**：源变了而版本没 bump → `--check` 报错（静默违反是最贵的错）。
5) **L3 只留索引**：技能 / 踩坑集 / 交接 / 项目文档 / 工具 的**正文不进冻结区**，
   只留索引；正文将来按需**逐条**进 Append Stream。

用法
====
    python3 build-frozen-corpus.py                 # A 档 → <project>/frozen-private/
    python3 build-frozen-corpus.py --tier B        # B 档 = A + 项目文档索引
    python3 build-frozen-corpus.py --check         # 幂等校验（重算并逐字节比对）
    python3 build-frozen-corpus.py --spec          # 打印段清单（唯一声明处）
    python3 build-frozen-corpus.py --out <dir>     # 换输出目录
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import unicodedata
from pathlib import Path

# --------------------------------------------------------------------------
# 唯一声明处：段清单
# --------------------------------------------------------------------------
# out      : 输出文件（相对输出根）
# zone/layer/domain/project : 区 / 层 / 域 / 项目（与 FileFrozenContentSource 的槽位一致）
# version_key : 键名（版本号在 versions.json 里，与账本分离）
# blocks   : 段内块（title + 来源）
#   mode = full          读单个源文件全文
#   mode = persona       读多个源文件，逐个子标题拼接
#   mode = knowledge-l12  生成技能知识库的常驻部分（L1 法则 + L2 问题地图；来自 knowledge/.derived/）
#   mode = skill-index   生成技能索引（含所属域）—— 已被 knowledge-l12 取代，保留作历史口径
#   mode = pitfall-index 生成踩坑集**节级**索引
#   mode = handoff-index 生成交接索引
#   mode = project-index 生成项目文档索引（B 档）
#   mode = pointer       生成 L3 资源指针（不含正文）
#   mode = mem-headings  生成长期记忆**标题级**索引
SPEC: list[dict] = [
    {
        "out": "rules/global.md", "zone": "rules", "layer": "global",
        "version_key": "rules.global",
        "blocks": [
            {"title": "一、铁则与流程", "mode": "full", "source": "AGENTS.md"},
            {"title": "二、人格与协作边界", "mode": "persona",
             "sources": ["SOUL.md", "IDENTITY.md", "USER.md", "GLOSSARY.md"]},   # OWNER.md 已并入 USER.md（主人 2026-09-16 定）
            {"title": "三、L3 资源指针（正文不在冻结区）", "mode": "pointer"},
        ],
    },
    {
        "out": "knowledge/global.md", "zone": "knowledge", "layer": "global",
        "version_key": "knowledge.global",
        "blocks": [
            {"title": "一、知识当前版本（KNOWLEDGE.md：L1/L2 + 唯一索引入口）", "mode": "full", "source": "KNOWLEDGE.md"},
            {"title": "二、技能知识库（L1 法则 + L2 问题地图；L3 正文按需逐条取）", "mode": "knowledge-l12"},
            {"title": "三、踩坑集索引（L3：节级）", "mode": "pitfall-index"},
            {"title": "四、跨端交接索引（L3）", "mode": "handoff-index"},
            {"title": "五、项目文档索引（L3）", "mode": "project-index", "tier": "B"},
        ],
    },
    {
        "out": "memory/index.md", "zone": "memory-index", "layer": "global",
        "version_key": "memory.index",
        "blocks": [
            {"title": "一、记忆索引（时间线）", "mode": "full", "source": "MEMORYINDEX.md"},
            {"title": "二、长期记忆标题索引（索引而非全文）", "mode": "mem-headings", "source": "MEMORY.md"},
        ],
    },
]

# 技能 → 专业域（配合 KnowledgeDomains 的 17 个预设域）
SKILL_DOMAINS: dict[str, str] = {
    "svn-workflow": "software", "agent-file-editing": "software", "log-debug": "software",
    "dual-track-release": "software", "incremental-archive": "software",
    "context-index-maintenance": "software", "csharp-wpf-development": "software",
    "swiftui-desktop-app": "software", "webview-app": "software",
    "windows-uia-automation": "software", "screenshot-uia": "software",
    "powershell-51-scripting": "software", "image-ocr": "software",
    "windows-deployment": "ops",
    "icon-forge": "design", "macos-chinese-image-text": "design", "taobao-listing-assets": "business",
    "license-delivery": "business", "license-keygen": "business",
    "media-scan-pairing": "photography", "image-compress": "photography",
    "llm-context-benchmark": "research", "model-speed-benchmark": "research",
    "voice-archive": "research", "architecture-paper-publishing": "writing",
}

DESC_MAX = 60          # 技能索引里「触发场景」截断长度
BYTES_PER_TOKEN = 1.88  # 实测（2026-09-14）：字符/token


def nfc(s: str) -> str:
    return unicodedata.normalize("NFC", s)


def read_text(path: Path) -> str:
    """读文件并归一为 LF、去 BOM。"""
    raw = path.read_text(encoding="utf-8-sig", errors="replace")
    return raw.replace("\r\n", "\n").replace("\r", "\n")


def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def kb(n: int) -> float:
    return n / 1024


# --------------------------------------------------------------------------
# 生成器
# --------------------------------------------------------------------------
def gen_pointer(ws: Path) -> str:
    return (
        "以下资源的**正文不在冻结区**（L3），按需取用；索引见 knowledge/global：\n\n"
        "- 环境 / 部署 / 主机 / 工具：`TOOLS.md`\n"
        "- 技能手册：**`knowledge/knowledge.md`**（L1 法则 + L2 问题地图常驻；**L3 条正文按 id 逐条取**）\n"
        "- 踩坑集：`projects/*/PITFALLS.md`（清单 = 踩坑集节级索引）\n"
        "- 跨端交接：`handoff/*.md`（清单 = 交接索引）\n"
        "- 项目方法论文档：`projects/*/docs/`（清单 = 项目文档索引）\n"
    )


def gen_skill_index(ws: Path) -> str:
    rows = []
    for path in sorted(ws.glob("skills/*/SKILL.md"), key=lambda p: nfc(p.parent.name)):
        name = path.parent.name
        text = read_text(path)
        desc = ""
        m = re.search(r"^---\n(.*?)\n---", text, re.S)
        if m:
            for line in m.group(1).splitlines():
                if line.strip().startswith("description:"):
                    desc = line.split(":", 1)[1].strip().strip('"')
                    break
        if not desc:
            m2 = re.search(r"^#\s+(.+)$", text, re.M)
            desc = m2.group(1).strip() if m2 else ""
        desc = nfc(desc)[:DESC_MAX]
        domain = SKILL_DOMAINS.get(name, "misc")
        rows.append(f"| `{name}` | {domain} | {desc} | {len(path.read_bytes()) // 1024} KB |")
    return ("| 技能 | 域 | 触发场景 | 规模 |\n|---|---|---|---|\n" + "\n".join(rows) + "\n\n"
            f"共 {len(rows)} 本；正文一律不进冻结区。\n")


def gen_pitfall_index(shared: Path) -> str:
    files = sorted(shared.glob("projects/**/PITFALLS*.md"), key=lambda p: nfc(str(p)))
    out, total = [], 0
    for path in files:
        rel = nfc(str(path.relative_to(shared)))
        out.append(f"### `{rel}`（{len(path.read_bytes()) // 1024} KB）")
        for line in read_text(path).splitlines():
            if re.match(r"^#{2,3} ", line):
                out.append("- " + line.strip().lstrip("# ").strip())
                total += 1
        out.append("")
    return ("\n".join(out) + f"\n共 {len(files)} 份、{total} 个节条目；正文按需取。\n")


def gen_handoff_index(ws: Path) -> str:
    out = []
    for path in sorted(ws.glob("handoff/*.md"), key=lambda p: nfc(p.name)):
        text = read_text(path)
        m = re.search(r"^#{1,3} (.+)$", text, re.M)
        title = nfc(m.group(1).strip()) if m else ""
        out.append(f"- `{nfc(path.name)}` — {title}（{len(path.read_bytes()) // 1024} KB）")
    return "\n".join(out) + f"\n\n共 {len(out)} 份；正文按需取。\n"


def gen_project_index(shared: Path) -> str:
    root = shared / "projects"
    files = [
        p for p in root.rglob("*.md")
        if "/bin/" not in str(p) and "/obj/" not in str(p)
        and "AgentRuntime" not in str(p.relative_to(root))
        and "/runs/" not in str(p) and "/recordings/" not in str(p)
        and not p.name.startswith("PITFALLS")
    ]
    out = []
    for path in sorted(files, key=lambda p: nfc(str(p))):
        rel = nfc(str(path.relative_to(shared)))
        m = re.search(r"^#{1,3} (.+)$", read_text(path), re.M)
        title = nfc(m.group(1).strip()) if m else ""
        out.append(f"- `{rel}` — {title}（{len(path.read_bytes()) // 1024} KB）")
    return "\n".join(out) + f"\n\n共 {len(out)} 篇；正文按需取。\n"


def gen_mem_headings(ws: Path) -> str:
    text = read_text(ws / "MEMORY.md")
    out = []
    for line in text.splitlines():
        if re.match(r"^#{1,3} ", line):
            out.append("- " + line.strip().lstrip("# ").strip())
    return ("长期记忆**是索引而非全文**（论文 §3.4）：以下为条目索引，正文按需 `memory_search` / `memory_get`。\n\n"
            + "\n".join(out) + f"\n\n共 {len(out)} 条。\n")


def gen_knowledge_l12(ws: Path) -> str:
    """技能知识库的**常驻部分**：L1 法则 + L2 问题地图（L3 正文按需逐条取）。

    来源 = `<workspace>/knowledge/.derived/{l1,l2}.jsonl`（由 `tools/skill-repo` 从 knowledge.md 派生）。
    为什么不再读 `skills/`：知识已**收敛为 L1/L2/L3**（用户可见形态 knowledge.md），源技能不再作为常驻来源。
    """
    derived = ws / "knowledge" / ".derived"
    l1_path, l2_path = derived / "l1.jsonl", derived / "l2.jsonl"
    if not l1_path.exists() or not l2_path.exists():
        raise FileNotFoundError(
            f"知识库派生缺失：{derived}（先跑 tools/skill-repo/knowledge-repo.py --derive-only）")

    l1 = [json.loads(x) for x in read_text(l1_path).splitlines() if x.strip()]
    l2 = [json.loads(x) for x in read_text(l2_path).splitlines() if x.strip()]

    out = ["### L1 · 法则（1 技能 = 1 条；最抽象的定位）", ""]
    for row in l1:
        out.append(f"- `{row['skill']}`：{row['l1']}")
    out += ["", "### L2 · 问题地图（1 行 = 1 个具体问题 → 见 L3 的**某几条**；L3 正文按需逐条取）", ""]
    for row in l2:
        ids = " ".join(row.get("ids", []))
        out.append(f"- `{row['skill']}` [{row.get('domain', '')}] {row['q']} → 见 L3 {ids}")
    out += ["", f"（共 {len(l1)} 条法则 / {len(l2)} 个问题；L3 正文逐条按 id 取用，不进常驻。）"]
    return "\n".join(out) + "\n"


def gen_persona(ws: Path, sources: list[str]) -> str:
    parts = []
    for name in sources:
        path = ws / name
        if not path.exists():
            continue
        body = read_text(path).strip()
        if not body:
            continue
        parts.append(f"### {name}\n\n{body}")
    return "\n\n".join(parts) + "\n"


def build_block(block: dict, ws: Path, shared: Path) -> str | None:
    mode = block["mode"]
    if mode == "full":
        return read_text(ws / block["source"]).strip() + "\n"
    if mode == "persona":
        return gen_persona(ws, block["sources"])
    if mode == "pointer":
        return gen_pointer(ws)
    if mode == "skill-index":
        return gen_skill_index(ws)
    if mode == "knowledge-l12":
        return gen_knowledge_l12(ws)
    if mode == "pitfall-index":
        return gen_pitfall_index(shared)
    if mode == "handoff-index":
        return gen_handoff_index(ws)
    if mode == "project-index":
        return gen_project_index(shared)
    if mode == "mem-headings":
        return gen_mem_headings(ws)
    raise ValueError(f"未知 mode: {mode}")


def render(spec_entry: dict, version: str, ws: Path, shared: Path) -> tuple[str, dict]:
    """把一个区渲染成最终文本 + 账本条目。"""
    blocks, meta = [], []
    for block in spec_entry["blocks"]:
        body = build_block(block, ws, shared)
        if body is None:
            continue
        blocks.append(f"## {nfc(block['title'])}\n\n{body.rstrip()}\n")
        meta.append({"title": nfc(block["title"]), "mode": block["mode"],
                     "chars": len(body.rstrip()) + 1,
                     "sha256": sha256(body.rstrip() + "\n")})
    text = f"<!-- frozen: version={version} -->\n" + "\n".join(blocks)
    body_only = text.split("-->\n", 1)[1]      # 版本头不计入指纹（它只服务账本）
    entry = {
        "out": spec_entry["out"], "zone": spec_entry["zone"], "layer": spec_entry["layer"],
        "version": version, "chars": len(body_only), "kb": round(kb(len(body_only.encode())), 1),
        "sha256": sha256(body_only), "blocks": meta,
    }
    return text, entry


# --------------------------------------------------------------------------
# 版本号（与账本分离的唯一声明处）
# --------------------------------------------------------------------------
def load_versions(tool_dir: Path) -> dict:
    p = tool_dir / "versions.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}


def main() -> int:
    here = Path(__file__).resolve().parent
    project = here.parent.parent                     # projects/AgentRuntime
    ap = argparse.ArgumentParser()
    # [WB]（WhiteBox）**自持**的语料工作区（主人 2026-09-16 12:13 定）：
    # 与 [OC] 的 ~/.openclaw/workspace **互不干涉**，两边**没有任何共享机制**（知识互换靠远端 AI 手工进行）。
    # 见 whitebox/README.md。
    ap.add_argument("--workspace", default=str(project / "whitebox" / "workspace"))
    ap.add_argument("--shared", default=str(project.parent.parent))  # software-company
    ap.add_argument("--out", default=str(project / "frozen-private"))
    ap.add_argument("--tier", default="A", choices=["A", "B"])
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--spec", action="store_true")
    ap.add_argument("--bump", metavar="KEY|all", default=None,
                    help="声明一次 bump：版本号 +1、刷新源锚，并重写产物（改内容必须走这里）")
    args = ap.parse_args()

    ws, shared, out = Path(args.workspace), Path(args.shared), Path(args.out)
    versions = load_versions(here)

    if args.spec:
        print(json.dumps(SPEC, ensure_ascii=False, indent=1))
        print("SKILL_DOMAINS:", json.dumps(SKILL_DOMAINS, ensure_ascii=False))
        return 0

    rendered, manifest = [], []
    for entry in SPEC:
        blocks = [b for b in entry["blocks"] if b.get("tier", "A") <= args.tier]
        version = str(versions.get(entry["version_key"], 1))
        text, meta = render({**entry, "blocks": blocks}, version, ws, shared)
        rendered.append((entry["out"], text))
        manifest.append(meta)

    fingerprint = sha256("".join(m["sha256"] for m in manifest))
    ledger = {"tier": args.tier, "fingerprint": fingerprint, "sections": manifest,
              "note": "账本不进 prompt；版本号在 tools/frozen-build/versions.json"}

    if args.bump:
        return do_bump(here, args, manifest, ledger, rendered, out)
    if args.check:
        return check(out, rendered, ledger, versions)

    write_products(out, rendered, ledger)
    (out / "FROZEN.md").write_text(
        "# 冻结区语料（私有：SVN 内网可入库，**GitHub 绝不发布**）\n\n"
        "- 本目录由 `tools/frozen-build/build-frozen-corpus.py` **确定性生成**，**不要手改**。\n"
        "- 改内容请改源（openclaw 工作区）→ 在 `versions.json` bump 版本 → 重跑 → 收尾。\n"
        "- 正文与账本分离：`_manifest.json` 是账本，**不进 prompt**。\n"
        "- 发布红线：GitHub 发布物**绝不允许**含本目录；发布前先跑 `tools/pre-publish-scan.sh <暂存目录>`。\n",
        encoding="utf-8")
    total = sum(m["kb"] for m in manifest)
    chars = sum(m["chars"] for m in manifest)
    print(f"[语料] 合计 {total:.1f} KB / {chars} 字符 ≈ {chars / BYTES_PER_TOKEN:.0f} token"
          f"（档位 {args.tier}）")
    print(f"[账本] 全局指纹 {fingerprint[:16]}…")
    return 0


def write_products(out: Path, rendered: list[tuple[str, str]], ledger: dict) -> None:
    """写产物 + 账本（正文与账本分离；产物是派生物，禁手改）。"""
    for rel, text in rendered:
        target = out / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
        print(f"[语料] {rel:22s} {len(text.encode()) / 1024:7.1f} KB  version={next(m['version'] for m in ledger['sections'] if m['out'] == rel)}")
    (out / "_manifest.json").write_text(
        json.dumps(ledger, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")


def do_bump(tool_dir: Path, args, manifest: list[dict], ledger: dict,
            rendered: list[tuple[str, str]], out: Path) -> int:
    """声明一次 bump：把 **源锚** 钉到当前正文 sha、版本号 +1，然后重写产物。

    为什么要有它（2026-09-23）：只靠「产物 vs 重算」抓不住「**重跑了但没 bump**」——
    重跑之后两边天然一致，版本号却没动（改内容 = 改前缀 = 缓存代价，绝不能静默）。
    锚放在 `versions.json`（**只有本子命令会改**）⇒ 源内容一变就必须 bump，否则 `--check` 一直红。
    """
    versions = load_versions(tool_dir)
    key_by_out = {e["out"]: e["version_key"] for e in SPEC}
    anchored = versions.setdefault("source_sha256", {})
    chosen = list(key_by_out.values()) if args.bump == "all" else [args.bump]
    unknown = [k for k in chosen if k not in key_by_out.values()]
    if unknown:
        print(f"[bump] ❌ 不认识的 version_key：{', '.join(unknown)}"
              f"（可选：{' / '.join(key_by_out.values())} / all）", file=sys.stderr)
        return 2

    changed = 0
    for section in manifest:
        key = key_by_out[section["out"]]
        if key not in chosen:
            continue
        now = section["sha256"]
        was = anchored.get(key)
        if was and was.get("tier") == ledger["tier"] and was.get("sha256") == now:
            print(f"[bump] {key}：源未变（锚已一致）⇒ **拒绝空 bump**")
            continue
        old_version = versions.get(key, 1)
        versions[key] = int(old_version) + 1
        anchored[key] = {"tier": ledger["tier"], "sha256": now}
        section["version"] = str(versions[key])          # 版本头随之一并升
        changed += 1
        print(f"[bump] {key}：version {old_version} → {versions[key]}（锚 tier={ledger['tier']} · {now[:12]}）")

    if changed == 0:
        return 1

    (tool_dir / "versions.json").write_text(
        json.dumps(versions, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # 重写产物：正文一字不动，只把头里的版本号换成新号。
    for i, (rel, text) in enumerate(rendered):
        section = next(m for m in ledger["sections"] if m["out"] == rel)
        head, sep, rest = text.partition("\n")
        if head.startswith("<!-- frozen: version="):
            rendered[i] = (rel, f"<!-- frozen: version={section['version']} -->{sep}{rest}")

    write_products(out, rendered, ledger)
    print(f"[bump] 已声明 {changed} 处变更；产物已重写（档位 {ledger['tier']}）。")
    return 0


def check(out: Path, rendered: list[tuple[str, str]], ledger: dict, versions: dict) -> int:
    """幂等校验：产物必须与「用当前源重算」逐字节一致，且**版本与源锚**纪律成立。"""
    bad = 0
    for rel, text in rendered:
        target = out / rel
        if not target.exists():
            print(f"[校验] ❌ 缺产物 {rel}", file=sys.stderr)
            bad += 1
            continue
        if read_text(target) != text:
            print(f"[校验] ❌ 产物与重算不一致（源改过但没重跑）：{rel}", file=sys.stderr)
            bad += 1
    old_p = out / "_manifest.json"
    if old_p.exists():
        old = json.loads(old_p.read_text(encoding="utf-8"))
        for new_s in ledger["sections"]:
            old_s = next((s for s in old["sections"] if s["out"] == new_s["out"]), None)
            if old_s and old_s["sha256"] != new_s["sha256"] and old_s["version"] == new_s["version"]:
                print(f"[校验] ❌ {new_s['out']} 内容已变但版本未 bump"
                      f"（version={new_s['version']}）→ 先改 versions.json", file=sys.stderr)
                bad += 1
        if old["fingerprint"] == ledger["fingerprint"]:
            print(f"[校验] 指纹未变：{ledger['fingerprint'][:16]}…")

    # **源锚**（2026-09-23）：上面那条只抓得住「源改了但产物没重跑」；
    # 重跑之后 manifest 与重算天然一致 ⇒ 「**重跑了但没 bump**」原本无人管（版本号静默不变 = 前缀静默变）。
    # 锚放在 `versions.json`（只有 `--bump` 会改它）⇒ 源一变就必须走 `--bump`，重跑不会让它变绿。
    key_by_out = {e["out"]: e["version_key"] for e in SPEC}
    anchored = versions.get("source_sha256") or {}
    for new_s in ledger["sections"]:
        key = key_by_out[new_s["out"]]
        was = anchored.get(key)
        if was is None:
            print(f"[校验] ❌ {new_s['out']} 源未锚定（key={key}）→ 先跑 --bump {key}（或 --bump all）", file=sys.stderr)
            bad += 1
        elif was.get("tier") == ledger["tier"] and was.get("sha256") != new_s["sha256"]:
            print(f"[校验] ❌ {new_s['out']} 源内容与「上次 bump 的锚」不一致 ⇒ **必须 bump**"
                  f"（改内容就得改版本号）：--bump {key}；重跑产物**不会**让它变绿", file=sys.stderr)
            bad += 1
    if bad:
        print(f"[校验] ❌ {bad} 项不合格", file=sys.stderr)
        return 1
    print("[校验] ✅ 产物与源一致、版本纪律成立")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

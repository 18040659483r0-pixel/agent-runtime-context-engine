#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""build-skill-repo.py —— SKILL.md → L1/L2/L3 技能仓库（切条 = 远端 AI 抽象）。

设计依据：docs/DESIGN-SKILL-LAYERS.md
  §一 对等转换（信息不丢）/ §二·1 L2 形态 / §三 仓库形态 / §4.1 切条规则 / §4.2 往返 / §七 闸门 S1~S6

核心纪律（与语料迁移器同规）：
  1. 切分决策由**远端 AI** 给出，但**文本取自源文件的逐字切片**（AI 只给行号区间）
     ⇒ 覆盖 = 100%、往返 = 逐字节相等，这两条由构造保证，而不是靠模型自觉。
  2. AI 原始响应**落盘缓存**（按源文件 sha256 命名）⇒ 重跑不重复花钱、可离线复算。
  3. 三道闸门：① 源指纹变但账本未 bump ⇒ 报错；② 产物被手改 ⇒ 报错；③ 重算不一致 ⇒ 报错。

密钥：只从 `--api-key-file` 读；本脚本**从不打印 / 写日志 / 写配置**密钥内容。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

# ---------------------------------------------------------------- 常量

EXTRACTOR_NAME = "build-skill-repo.py"
EXTRACTOR_VERSION = "skill-repo/1.0.0"
DEFAULT_GRANULARITY = "fine"
PROMPT_VERSIONS = {"fine": "p7", "coarse": "p7-coarse"}
HYBRID_THRESHOLD_DEFAULT = 0.15      # 相对闸门：ratio = (L1+L2)/整份 ≤ 阈值（主人 2026-09-16 00:18 定：10%→15%）
ABS_TOKEN_CAP_DEFAULT = 2000         # 绝对闸门：常驻 L1+L2 ≤ 上限（token）（主人 2026-09-16 00:31 定）
BYTES_PER_TOKEN = 3.36               # 与语料账本同源（84.0 KB → 25,605 token）
HYBRID_RULE_VERSION = "hybrid-rule/3"  # 双条件：相对 ≤ 阈值 且 绝对 ≤ token 上限
HYBRID_GUIDE_TMPL = "本技能体量小、未拆分 —— 需要时直接整份取用"
HYBRID_ID_SUFFIX = "000"
SMALL_FILE_HYBRID_CAP = 2000           # 整份 ≤ 此值 ⇒ 直接 hybrid（收益比策略的硬闸门）
DEFAULT_ENDPOINT = "https://api.deepseek.com/chat/completions"
DEFAULT_MODEL = "deepseek-flash"

#: 密钥默认来源 = **运行时自己用的那一把**：config.*.json 的 `apiKeyFile`（本机 = `~/.agentruntime/api_key`），
#: 也可用环境变量 `AGENTRUNTIME_API_KEY_FILE` 覆盖。
#: 为什么要有默认（主人 2026-09-22 17:3x 指出）：密钥本来就摆在运行时手边，
#: **再向主人要一次是多余的门槛** —— 工具自己去找，别让人替工具跑腿。
DEFAULT_KEY_FILE = os.path.expanduser(os.environ.get("AGENTRUNTIME_API_KEY_FILE") or "~/.agentruntime/api_key")
MAX_TOKENS = 32000          # 注意：deepseek-flash 是**带思维链**的模型，reasoning_tokens 与正文共用这个上限
RETRY_DELAYS = [2, 5, 15]

KINDS = ["Overview", "Principle", "Procedure", "Pitfall", "Gate", "Command", "Check", "Reference",
         "WholeSkill"]
DOMAINS = ["software", "data", "finance", "quant", "photography", "design", "product",
           "business", "writing", "law", "research", "history", "ops", "security",
           "hardware", "education", "misc"]

ARTIFACT_NAMES = ["L1.md", "L2.md", "L3.jsonl"]
REPO_LEVEL_NAMES = ["_l1.jsonl", "_l2.jsonl"]

SECRET_RE = re.compile(r"sk-[A-Za-z0-9_\-]{4,}")
FM_RE = re.compile(r"^---[ \t]*\r?\n(.*?)\r?\n---[ \t]*\r?\n", re.S)


# ---------------------------------------------------------------- 小工具

def sha_b(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha_s(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def jcs(obj) -> bytes:
    """规范 JSON 字节（指纹用：键排序、紧凑、不转义中文）。"""
    return json.dumps(obj, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def redact(text: str) -> str:
    """任何要打印/落盘的外部文本都先过这里（防密钥片段进日志）。"""
    return SECRET_RE.sub("sk-***REDACTED***", text or "")


def die(msg: str, code: int = 2):
    print(f"[FAIL] {msg}", file=sys.stderr)
    sys.exit(code)


def split_frontmatter(text: str):
    m = FM_RE.match(text)
    if not m:
        return "", text
    return text[:m.end()], text[m.end():]


def split_lines(body: str):
    ends_nl = body.endswith("\n")
    lines = body.split("\n")
    if ends_nl:
        lines = lines[:-1]
    if lines == [""]:
        lines = []
    return lines, ends_nl


def numbered(lines) -> str:
    return "\n".join("%4d| %s" % (i + 1, ln) for i, ln in enumerate(lines))


# ---------------------------------------------------------------- AI 调用

SYSTEM_PROMPT_TEMPLATE = """你是「技能三层抽取器」。输入是一份 SKILL.md 的正文（已去掉 frontmatter，逐行带行号）。

输出**三段**，合起来 = 该技能的**全面目**：
 ① **L1 = 最抽象的法则**：这个技能属于「解决某一类问题的综合方法」中的**哪一条法则/方法**——不是技能简介。
 ② **L2 = 具体问题 → 指向若干条 L3**（**可一对多，一对多且常态**）：一个具体问题往往要看好几条 L3，不要强行压成一条。
 ③ **L3 = 每条正文的定位（行号区间）**；正文由程序按行号从源文件**逐字切片**——你**不许改写正文**，只给行号。

【切条规则】
1. **按功能单元切，不按字节长度切**。一条 = 一个完整可执行单元：步骤 / 命令 / 判据 / 坑 / 铁则。同一条只讲一件事。
2. %(granularity)s
3. 每条给 start_line / end_line（对正文的 1-based 闭区间）。
4. **必须首尾相接、无重叠、无空档**：第一条 start_line=1，最后一条 end_line=正文总行数，且 end_line+1 == 下一条的 start_line。空行 / 分隔线 / 小标题行**不得**单独成条，归入它所属的那一条；页标题（`# 标题`）归入第一条。
5. **不许丢内容**：不确定归属的行，归给语义最近的那一条，不要另立「其它」条。

【L2 问题地图规则（优先级：**先全覆盖，再压字节**）】
6. 每条问题：`q` = **具体问题/触发场景**，**≤28 个字**；去掉口语尾巴（“怎么办？”“我该…”）与冗余修饰，只留下“一看到就知道要不要看”的硬信息；`seqs` = 要看的 L3 条**序号**（1 个或多个，允许并优先一对多）。
7. **第一优先级 = 覆盖**：**每一条 L3 都必须被至少一条问题引用**（可为同一片区域写多个角度不同的问题）。交差前自查：所有 L3 序号是否都出现过？
8. **第二优先级 = 在满足 7 的前提下**再把问题地图压小：问题文字 ≤28 字、近义问题合并、每条只列**真正必需**的序号。
9. 问题条目数建议 **8~24**；**字节预算只是参考上限**（≈ ≤ 整份的 8%%~15%%）：**只有**当“全覆盖”真的撑不进这个上限时，才允许留下**尽量少**的未引用条。
10. 每条问题给 `domain`：%(domains)s 之一。

【字段】
- short_name：本技能短名（2~6 位小写字母/数字，用于条 id，如 llm-context-benchmark → bench）
- l1：**一句话法则级定位**，格式「<哪类问题>的法则：<法则内容>」，≤80 字
- questions：[{domain, q, seqs:[...]}]（**先写 L1，再写映射表，最后写 strips**）
- strips：[{title(≤24 字), kind(Overview|Principle|Procedure|Pitfall|Gate|Command|Check|Reference), domain, start_line, end_line}]

只输出一个 JSON 对象，不要 markdown 代码块、不要任何解释文字。格式：
{"short_name":"...","l1":"...","questions":[{"domain":"...","q":"...","seqs":[1,4]}],"strips":[{"title":"...","kind":"...","domain":"...","start_line":1,"end_line":9}]}
"""

GRANULARITY_RULE = {
    "fine": "**条数尽量多**：宁可细，不可粗。长文（>8KB）通常 15~40 条；短文（<2KB）通常 3~8 条。**不许**只切三五条就交差。",
    "coarse": ("**粒度以「独立可用」为准，不追求条数**：一条要能单独拿出来执行/理解（通常覆盖 4~15 行）。\n"
               "   - 同一个功能单元里并列的多个小节**合为一条**（例：一组「六条铁则」= 1 条；「步骤 1~8」按阶段合并为 2~4 条）。\n"
               "   - 全文条数参考：正文行数 ÷ 8 左右（长文 12~25 条；短文 3~6 条）。\n"
               "   - 但**不得**把两个不相干的功能单元硬凑成一条 —— 功能不重复、不混装优先于条数指标。"),
}

SYSTEM_PROMPTS = {g: SYSTEM_PROMPT_TEMPLATE % {"granularity": r, "domains": "/".join(DOMAINS)}
                  for g, r in GRANULARITY_RULE.items()}


def system_prompt(granularity: str) -> str:
    return SYSTEM_PROMPTS[granularity]


def build_prompt(skill_name: str, description: str, lines, l2_budget: int = 2000) -> str:
    if l2_budget <= 0:
        budget_line = (
            "【L2 字节预算（参考）】%d B（**已为负** = 本技能体量小、不适合索引化）⇒ 只写 3~5 个最关键的问题就行，"
            "但**仍要保证每条 L3 都被至少一个问题引用**（除非实在做不到）。\n" % l2_budget)
    else:
        budget_line = (
            "【L2 字节预算（**参考上限**，非硬约束）】问题地图总字节（含标题行约 60 B）目标 ≤ %d B（≈ 整份的 8%%）。\n"
            "  - **优先级**：① **全覆盖**（每条 L3 都被至少一个问题引用）→ ② 在①成立的前提下再把地图压小（减问题条数 / 问题文字更短 / 每条只列真正必需的序号）；\n"
            "  - **只有**当①真的撑不进上述上限时，才允许留下尽量少的未引用条。\n" % l2_budget)
    return (
        "【技能名】%s\n"
        "【技能描述】%s\n"
        "【正文行数】%d（行号 = 去掉 frontmatter 后的正文行号，1-based，含空行）\n"
        "【正文字节】%d\n"
        "%s\n"
        "【正文】（每行前缀 `行号| ` 只是定位标记，不属于内容）\n%s\n\n"
        "【输出】严格按三段给出 JSON：① l1（法则级定位）② questions（具体问题 → L3 序号，可一对多）③ strips（行号区间）。"
        % (skill_name, description, len(lines),
           sum(len(x.encode("utf-8")) for x in lines) + (1 if lines else 0),
           budget_line, numbered(lines))
    )


def http_post_json(url: str, payload: dict, api_key: str, timeout: int = 300):
    """POST 并返回 (raw_bytes, parsed)。调用方负责计数与错误处理。"""
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", "Bearer " + api_key)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
    return raw, json.loads(raw.decode("utf-8"))


def _accum_usage(prev, calls, usage):
    """累加用量（用于「这个技能历史上真花了多少」；remode 不增加）。"""
    base = dict(prev or {})
    for k, src in (("prompt_tokens", "prompt_tokens"), ("completion_tokens", "completion_tokens"),
                   ("total_tokens", "total_tokens"), ("prompt_cache_hit_tokens", "prompt_cache_hit_tokens"),
                   ("reasoning_tokens", "reasoning_tokens"), ("calls", None)):
        v = calls if src is None else ((usage.get(src) or 0) if src != "reasoning_tokens"
                                       else ((usage.get("completion_tokens_details") or {}).get("reasoning_tokens") or 0))
        base[k] = int(base.get(k, 0)) + int(v or 0)
    return base


def cache_ok(parsed, lines):
    """缓存可用性：必须正常收尾 + 能通过同一套深校验（切条归一 + 映射表），否则当坏缓存重抽。

    注意：只检查「能不能解析」不够 —— AI 给出行号越界/重叠/悬空引用时，缓存会一直失败。
    """
    try:
        ch = parsed["choices"][0]
        if ch.get("finish_reason") != "stop":
            return False, "finish_reason=%s" % ch.get("finish_reason")
        obj = extract_json_object(ch["message"]["content"])
        if not isinstance(obj, dict) or not obj.get("strips"):
            return False, "content 无 strips"
        strips, _ = normalize_strips(obj["strips"], len(lines))
        normalize_questions(obj.get("questions"), make_strip_records(strips, lines, "x", "x"))
        return True, "ok"
    except Exception as e:
        return False, "%s: %s" % (type(e).__name__, e)


def call_ai(cache_path: Path, api_key: str, model: str, endpoint: str,
            skill_name: str, description: str, lines, log, max_tokens: int, granularity: str,
            l2_budget: int):
    """返回 (raw_bytes, parsed, calls_made, cache_hit)。cache 命中则 0 次调用。"""
    sp = system_prompt(granularity)
    prompt = build_prompt(skill_name, description, lines, l2_budget)
    req_desc = {"endpoint": endpoint, "model": model, "max_tokens": max_tokens,
                "temperature": 0, "granularity": granularity,
                "prompt_version": PROMPT_VERSIONS[granularity],
                "prompt_sha256": sha_s(sp + "\n\n" + prompt)}
    req_path = cache_path.with_suffix(".req.json")

    if cache_path.exists():
        raw = cache_path.read_bytes()
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except Exception as e:
            parsed = None
            log.append("缓存不可解析（%s）⇒ 重抽" % e)
        if parsed is not None:
            good, why = cache_ok(parsed, lines)
            prev_req = json.loads(req_path.read_text(encoding="utf-8")) if req_path.exists() else None
            if not good:
                log.append("缓存无效（%s）⇒ 重抽（旧缓存已被覆盖）" % why)
            elif prev_req and prev_req.get("prompt_sha256") != req_desc["prompt_sha256"]:
                log.append("缓存与当前请求不符（prompt/model/token 变了）⇒ 重抽")
            else:
                log.append("cache HIT %s（0 次调用，不花钱）" % cache_path.name)
                if not req_path.exists():
                    req_path.write_text(json.dumps(req_desc, ensure_ascii=False, indent=2) + "\n",
                                        encoding="utf-8")
                return raw, parsed, 0, True

    payload = {
        "model": model,
        "messages": [
            {"role": "system", "content": sp},
            {"role": "user", "content": prompt},
        ],
        "response_format": {"type": "json_object"},
        "temperature": 0,
        "max_tokens": max_tokens,
        "stream": False,
    }
    calls = 0
    last_err = None
    for attempt, delay in enumerate([0] + RETRY_DELAYS):
        if delay:
            time.sleep(delay)
        calls += 1
        t0 = time.time()
        try:
            raw, parsed = http_post_json(endpoint, payload, api_key)
            log.append("AI 调用 #%d 成功：%.1fs，model=%s，prompt=%d 字符，max_tokens=%d"
                       % (calls, time.time() - t0, model, len(prompt), max_tokens))
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            cache_path.write_bytes(raw)          # 原始响应落盘（重跑不重复花钱）
            req_path.write_text(json.dumps(req_desc, ensure_ascii=False, indent=2) + "\n",
                                encoding="utf-8")
            return raw, parsed, calls, False
        except urllib.error.HTTPError as e:
            detail = redact(e.read().decode("utf-8", "replace"))[:600]
            last_err = "HTTP %s: %s" % (e.code, detail)
            log.append("AI 调用 #%d 失败（%.1fs）：%s" % (calls, time.time() - t0, last_err))
            if e.code in (400, 401, 403, 404):
                break                            # 不可重试
        except Exception as e:                   # 网络 / 超时 / 解析
            last_err = "%s: %s" % (type(e).__name__, redact(str(e))[:300])
            log.append("AI 调用 #%d 失败（%.1fs）：%s" % (calls, time.time() - t0, last_err))
    raise RuntimeError("AI 调用失败（已尝试 %d 次）：%s" % (calls, last_err))


def extract_json_object(text: str) -> dict:
    s = text.strip()
    if s.startswith("```"):
        s = re.sub(r"^```[a-zA-Z]*\s*", "", s)
        s = re.sub(r"\s*```$", "", s)
    try:
        return json.loads(s)
    except json.JSONDecodeError:
        i, j = s.find("{"), s.rfind("}")
        if i >= 0 and j > i:
            return json.loads(s[i:j + 1])
        raise


# ---------------------------------------------------------------- 切条归一

def normalize_strips(ai_strips, n_lines: int):
    """按 AI 给的行号区间归一为**完整覆盖、无重叠**的分区。

    缺口（AI 少切的行）一律并入**后一条**（小标题 / 空行跟着它所属的那一节走）；
    尾部缺口并入最后一条。归一前先校验重叠与升序 —— 重叠 = AI 越界，直接报错。
    """
    strips = []
    for i, s in enumerate(ai_strips):
        try:
            a, b = int(s["start_line"]), int(s["end_line"])
        except (KeyError, TypeError, ValueError):
            raise ValueError("第 %d 条缺 start_line/end_line 或非整数" % (i + 1))
        if not (1 <= a <= b <= n_lines):
            raise ValueError("第 %d 条行号越界：%d..%d（正文共 %d 行）" % (i + 1, a, b, n_lines))
        strips.append({
            "title": str(s.get("title", "")).strip() or "（无标题）",
            "kind": str(s.get("kind", "")).strip(),
            "domain": str(s.get("domain", "")).strip().lower(),
            "problem": str(s.get("problem", "")).strip(),
            "_ai_start": a, "_ai_end": b,
        })

    strips.sort(key=lambda x: x["_ai_start"])
    for i in range(1, len(strips)):
        if strips[i]["_ai_start"] <= strips[i - 1]["_ai_end"]:
            raise ValueError("条间重叠：第 %d 条 %d..%d 与第 %d 条 %d..%d"
                             % (i, strips[i - 1]["_ai_start"], strips[i - 1]["_ai_end"],
                                i + 1, strips[i]["_ai_start"], strips[i]["_ai_end"]))
    if not strips:
        raise ValueError("AI 未产出任何条")

    claimed, gaps = 0, []
    prev_end = 0
    for s in strips:
        claimed += s["_ai_end"] - s["_ai_start"] + 1
        if s["_ai_start"] > prev_end + 1:
            gaps.append([prev_end + 1, s["_ai_start"] - 1])
        prev_end = s["_ai_end"]
    if prev_end < n_lines:
        gaps.append([prev_end + 1, n_lines])

    for i, s in enumerate(strips):              # 缺口并入后一条（首条并入其起始）
        s["start"] = 1 if i == 0 else strips[i - 1]["_ai_end"] + 1
        s["end"] = n_lines if i == len(strips) - 1 else s["_ai_end"]

    # 归一后再查一遍（理论上不可能，防御性）
    cur = 1
    for s in strips:
        if s["start"] != cur:
            raise ValueError("归一失败：第 %d 条 start=%d，期望 %d" % (s["seq"] if "seq" in s else 0, s["start"], cur))
        if s["end"] < s["start"]:
            raise ValueError("归一失败：空区间")
        cur = s["end"] + 1
    if cur != n_lines + 1:
        raise ValueError("归一失败：覆盖 %d 行，正文 %d 行" % (cur - 1, n_lines))

    gap_lines = sum(b - a + 1 for a, b in gaps)
    return strips, {"ai_claimed_lines": claimed, "gap_lines": gap_lines, "gap_ranges": gaps}


def make_strip_records(strips, lines, skill_name: str, short_name: str):
    for i, s in enumerate(strips):
        s["seq"] = i + 1
        s["id"] = "S-%s-%03d" % (short_name, i + 1)
        s["text"] = "\n".join(lines[s["start"] - 1:s["end"]])
    return strips


# ---------------------------------------------------------------- 产物

def normalize_questions(ai_questions, strips):
    """校验「具体问题 → L3 序号」映射表：悬空序号 ⇒ 报错（不是警告）。"""
    n = len(strips)
    out = []
    for i, q in enumerate(ai_questions or []):
        if not isinstance(q, dict):
            raise ValueError("第 %d 条问题不是对象" % (i + 1))
        text = str(q.get("q", "")).strip()
        if not text:
            raise ValueError("第 %d 条问题的 q 为空" % (i + 1))
        seqs = q.get("seqs")
        if not isinstance(seqs, list) or not seqs:
            raise ValueError("问题「%s」未指向任何 L3（seqs 为空）" % text[:20])
        norm = []
        for s in seqs:
            try:
                v = int(s)
            except (TypeError, ValueError):
                raise ValueError("问题「%s」的 seqs 含非整数：%r" % (text[:20], s))
            if not (1 <= v <= n):
                raise ValueError("悬空 id：问题「%s」指向第 %d 条，但只有 %d 条 L3" % (text[:20], v, n))
            if v not in norm:
                norm.append(v)
        dom = str(q.get("domain", "")).strip().lower()
        out.append({"domain": dom if dom in DOMAINS else "misc", "q": text, "seqs": norm,
                    "ids": [strips[v - 1]["id"] for v in norm],
                    "one_to_many": len(norm) > 1})
    return out


def question_stats(questions, strips):
    refs = sorted({v for q in questions for v in q["seqs"]})
    orphans = [s["id"] for s in strips if s["seq"] not in refs]
    return {
        "questions": len(questions),
        "refs_total": sum(len(q["seqs"]) for q in questions),
        "one_to_many": sum(1 for q in questions if q["one_to_many"]),
        "refs_per_question_avg": round(sum(len(q["seqs"]) for q in questions) / len(questions), 2) if questions else 0,
        "strips_referenced": len(refs),
        "orphan_strips": orphans,
    }


# ---------------------------------------------------------------- 产物

def build_artifacts(skill_name: str, description: str, short_name: str, l1: str,
                    questions, strips, frontmatter_raw: str, ends_nl: bool):
    l1_body = (l1 or description or skill_name).strip().replace("\n", " ")
    l1_md = "%s — %s\n" % (skill_name, l1_body)

    rows = ["# %s · L2 问题地图（1 行 = 1 个具体问题；→ 指向若干条 L3，可一对多）" % skill_name, ""]
    for q in questions:
        rows.append("- [%s] %s → 见 L3 %s" % (q["domain"], q["q"], " ".join(q["ids"])))
    l2_md = "\n".join(rows) + "\n"

    l3_lines = []
    for s in strips:
        rec = {"id": s["id"], "skill": skill_name, "seq": s["seq"], "title": s["title"],
               "kind": s["kind"], "domain": s["domain"], "text": s["text"]}
        l3_lines.append(json.dumps(rec, ensure_ascii=False))
    l3_jsonl = "\n".join(l3_lines) + "\n"

    body = "\n".join(s["text"] for s in strips) + ("\n" if ends_nl else "")
    return {
        "L1.md": l1_md.encode("utf-8"),
        "L2.md": l2_md.encode("utf-8"),
        "L3.jsonl": l3_jsonl.encode("utf-8"),
        "_body_reconstructed": (frontmatter_raw + body).encode("utf-8"),
        "_l1_row": {"skill": skill_name, "short_name": short_name, "id_prefix": "S-" + short_name,
                    "description": description, "l1": l1_body, "entries": len(strips),
                    "questions": len(questions)},
        "_l2_rows": [{"skill": skill_name, "domain": q["domain"], "q": q["q"],
                      "ids": q["ids"], "seqs": q["seqs"]} for q in questions],
    }


def derive_products(ai: dict, lines, ends_nl: bool, frontmatter_raw: str,
                    skill_name: str, description: str, short_name: str):
    """AI 抽象 → 产物（纯函数，供抽取与 --check 重算共用，保证同输入同产物）。"""
    strips, cov = normalize_strips(ai.get("strips") or [], len(lines))
    strips = make_strip_records(strips, lines, skill_name, short_name)
    for s in strips:
        if s["kind"] not in KINDS:
            s["kind"] = "Reference"
        if s["domain"] not in DOMAINS:
            s["domain"] = "misc"
    questions = normalize_questions(ai.get("questions"), strips)
    arts = build_artifacts(skill_name, description, short_name, str(ai.get("l1") or ""),
                           questions, strips, frontmatter_raw, ends_nl)
    return strips, questions, cov, arts


def majority_domain(strips, questions):
    cnt = {}
    for s in strips:
        cnt[s["domain"]] = cnt.get(s["domain"], 0) + 1
    if cnt:
        top = max(cnt.values())
        return sorted(d for d, c in cnt.items() if c == top)[0]
    return questions[0]["domain"] if questions else "misc"


def make_hybrid_artifacts(skill_name: str, description: str, short_name: str, l1: str,
                          lines, frontmatter_raw: str, ends_nl: bool, domain: str):
    """hybrid 态：L1 法则（保留）+ L2 只有一条引导 + L3 就一条（整份正文逐字）。"""
    l1_body = (l1 or description or skill_name).strip().replace("\n", " ")
    l1_md = "%s — %s\n" % (skill_name, l1_body)
    wid = "S-%s-%s" % (short_name, HYBRID_ID_SUFFIX)
    text = "\n".join(lines)
    l2_md = ("# %s · L2（hybrid：体量小、不拆分）\n\n"
             "- [%s] %s → 见 L3 %s\n" % (skill_name, domain, HYBRID_GUIDE_TMPL, wid))
    rec = {"id": wid, "skill": skill_name, "seq": 1, "title": "%s 整份正文" % skill_name,
           "kind": "WholeSkill", "domain": domain, "text": text}
    l3_jsonl = json.dumps(rec, ensure_ascii=False) + "\n"
    body = text + ("\n" if ends_nl else "")
    return {
        "L1.md": l1_md.encode("utf-8"),
        "L2.md": l2_md.encode("utf-8"),
        "L3.jsonl": l3_jsonl.encode("utf-8"),
        "_body_reconstructed": (frontmatter_raw + body).encode("utf-8"),
        "_l1_row": {"skill": skill_name, "short_name": short_name, "id_prefix": "S-" + short_name,
                    "description": description, "l1": l1_body, "entries": 1, "questions": 1},
        "_l2_rows": [{"skill": skill_name, "domain": domain, "q": HYBRID_GUIDE_TMPL,
                      "ids": [wid], "seqs": [1]}],
    }


def apply_mode(arts_indexed, mode, skill_name, description, short_name, l1, lines,
               frontmatter_raw, ends_nl, questions, strips, src_bytes, threshold,
               abs_token_cap=ABS_TOKEN_CAP_DEFAULT, force_mode=None):
    """按两态机制定产物 + 账本字段。双条件：相对 ratio ≤ 阈值 **且** 绝对 L1+L2 ≤ token 上限。

    indexed: 原样；hybrid: L1 保留 + 单条引导 + 整份 L3。
    force_mode（strategy=benefit 时）由仓库级分配决定，绕开本地双条件。
    """
    l1_b = len(arts_indexed["L1.md"])
    l2_indexed_b = len(arts_indexed["L2.md"])
    idx_bytes = l1_b + l2_indexed_b
    ratio_indexed = round(idx_bytes / src_bytes, 4)
    idx_tokens = round(idx_bytes / BYTES_PER_TOKEN, 1)
    strip_bytes = [len(s["text"].encode("utf-8")) for s in strips]
    base = {
        "rule": HYBRID_RULE_VERSION, "threshold": threshold, "abs_token_cap": abs_token_cap,
        "bytes_per_token": BYTES_PER_TOKEN,
        "ratio_indexed": ratio_indexed, "layout_indexed_bytes": idx_bytes,
        "layout_indexed_tokens": idx_tokens,
        "indexed_l1_bytes": l1_b, "indexed_l2_bytes": l2_indexed_b,
        "indexed_strips": len(strips),
        "indexed_strips_avg_bytes": round(sum(strip_bytes) / len(strip_bytes), 1) if strip_bytes else 0,
        "indexed_strips_min_bytes": min(strip_bytes) if strip_bytes else 0,
        "indexed_strips_max_bytes": max(strip_bytes) if strip_bytes else 0,
    }
    decision = force_mode or ("indexed" if (ratio_indexed <= threshold and idx_tokens <= abs_token_cap)
                              else "hybrid")
    if decision == "indexed":
        return "indexed", arts_indexed, dict(base, **{
            "mode": "indexed", "ratio": ratio_indexed,
            "layout_bytes": idx_bytes, "layout_tokens": idx_tokens,
            "src_bytes": src_bytes,
        })
    dom = majority_domain(strips, questions)
    arts_hybrid = make_hybrid_artifacts(skill_name, description, short_name, l1, lines,
                                        frontmatter_raw, ends_nl, dom)
    l1_h = len(arts_hybrid["L1.md"])
    l2_h = len(arts_hybrid["L2.md"])
    why = []
    if force_mode == "hybrid" and ratio_indexed <= threshold and idx_tokens <= abs_token_cap:
        why.append("仓库级收益比/预算分配未入选（非本地双条件）")
    if ratio_indexed > threshold:
        why.append("相对 ratio=%.4f > 阈值 %.4f" % (ratio_indexed, threshold))
    if idx_tokens > abs_token_cap:
        why.append("绝对 %.1f token > 上限 %d" % (idx_tokens, abs_token_cap))
    return "hybrid", arts_hybrid, dict(base, **{
        "mode": "hybrid", "ratio": round((l1_h + l2_h) / src_bytes, 4),
        "layout_bytes": l1_h + l2_h,
        "layout_tokens": round((l1_h + l2_h) / BYTES_PER_TOKEN, 1),
        "src_bytes": src_bytes,
        "guide_id": "S-%s-%s" % (short_name, HYBRID_ID_SUFFIX), "guide_domain": dom,
        "reason": "indexed 口径 L1+L2 = %d B / %.1f token（整份 %d B）⇒ %s ⇒ 索引不划算"
                  % (idx_bytes, idx_tokens, src_bytes, "且".join(why)),
    })


def hybrid_cost_bytes(m, bpt=BYTES_PER_TOKEN):
    """某技能处于 hybrid 时的常驻字节（L1 + 单条引导）。已 hybrid 的账本有实测值。"""
    if m.get("mode") == "hybrid" and m.get("layout_bytes"):
        return m["layout_bytes"]
    l1 = (m.get("hybrid") or {}).get("indexed_l1_bytes") or m.get("layout_bytes") or 180
    return l1 + 165                       # 引导行实测 ≈ 155~172 B


def benefit_plan(metas, budget_tokens, small_file_cap=SMALL_FILE_HYBRID_CAP,
                 hard_token_cap=ABS_TOKEN_CAP_DEFAULT, bpt=BYTES_PER_TOKEN):
    """收益比 = (整份 − 单条) ÷ L2（indexed 口径）；按收益比降序贪心选到常驻预算用完。

    硬闸门：① indexed 后 L1+L2 ≤ hard_token_cap；② 整份 ≤ small_file_cap ⇒ 直接 hybrid。
    """
    plan, cand = {}, []
    for m in metas:
        name = m["skill"]
        src = m["source"]["bytes"]
        hy = m.get("hybrid") or {}
        idx_b = hy.get("layout_indexed_bytes") or m.get("layout_indexed_bytes")
        idx_t = hy.get("layout_indexed_tokens") or m.get("layout_indexed_tokens")
        l2_idx = hy.get("indexed_l2_bytes")
        avg = hy.get("indexed_strips_avg_bytes")
        if not idx_b or not l2_idx or not avg:
            plan[name] = {"selected": False, "benefit_ratio": None, "cost_tokens": None,
                          "reason": "账本缺 indexed 口径字段 ⇒ 先 --remode 重建账本"}
            continue
        info = {"benefit_ratio": round((src - avg) / l2_idx, 3), "src_bytes": src,
                "strip_avg_bytes": avg, "l2_indexed_bytes": l2_idx,
                "l1_indexed_bytes": hy.get("indexed_l1_bytes", 0),
                "cost_tokens": round(idx_b / bpt, 1),
                "hybrid_cost_tokens": round(hybrid_cost_bytes(m) / bpt, 1),
                "gate_tokens_ok": idx_t <= hard_token_cap,
                "gate_size_ok": src > small_file_cap}
        if not info["gate_size_ok"]:
            info.update(selected=False, reason="整份 %d B ≤ %d B 的小技能 ⇒ 直接 hybrid" % (src, small_file_cap))
        elif not info["gate_tokens_ok"]:
            info.update(selected=False, reason="L1+L2 %.1f token > 硬上限 %d" % (idx_t, hard_token_cap))
        else:
            info.update(selected=None, reason="候选（等预算分配）")
            cand.append((info["benefit_ratio"], name))
        plan[name] = info

    cand.sort(key=lambda x: (-x[0], x[1]))
    used = sum(plan[m["skill"]]["hybrid_cost_tokens"] for m in metas
               if plan[m["skill"]].get("cost_tokens") is None or not plan[m["skill"]].get("selected"))
    for rank, (ratio, name) in enumerate(cand, 1):
        info = plan[name]
        if used + info["cost_tokens"] <= budget_tokens:
            info["cost_tokens"] = info["cost_tokens"]     # 入选：从 hybrid 成本改成 indexed 成本
            used = used - info["hybrid_cost_tokens"] + info["cost_tokens"]
            info.update(selected=True, reason="收益比排序第 %d 位，预算内入选" % rank)
        else:
            info.update(selected=False, reason="预算用尽（%s）" % ("%.0f token" % budget_tokens))
    selected = sorted(n for n, i in plan.items() if i.get("selected"))
    return plan, {"budget_tokens": budget_tokens, "used_tokens": round(used, 1),
                  "indexed": selected,
                  "hybrid": sorted(n for n in plan if n not in selected)}


def hybrid_strip_records(skill_name: str, short_name: str, lines, domain: str):
    """hybrid 态的账本条表（1 条 = 整份正文）。"""
    text = "\n".join(lines)
    return [{"id": "S-%s-%s" % (short_name, HYBRID_ID_SUFFIX), "seq": 1,
             "title": "%s 整份正文" % skill_name, "kind": "WholeSkill", "domain": domain,
             "start": 1, "end": len(lines), "lines": len(lines),
             "bytes": len(text.encode("utf-8")), "text_sha256": sha_s(text), "text": text}]


def write_artifacts(outdir: Path, arts):
    outdir.mkdir(parents=True, exist_ok=True)
    for name in ARTIFACT_NAMES:
        (outdir / name).write_bytes(arts[name])


# ---------------------------------------------------------------- 抽取 / 复用

def extract_skill(skill_dir: Path, out_root: Path, api_key: str, model: str, endpoint: str,
                  bump: int, prev_meta, log, max_tokens: int, granularity: str,
                  hybrid_threshold: float, no_ai: bool = False,
                  abs_token_cap: float = ABS_TOKEN_CAP_DEFAULT, strategy: str = "dual",
                  force_mode=None, benefit=None):
    src_path = skill_dir / "SKILL.md"
    raw_bytes = src_path.read_bytes()
    text = raw_bytes.decode("utf-8")
    sha = sha_b(raw_bytes)
    frontmatter_raw, body = split_frontmatter(text)
    lines, ends_nl = split_lines(body)
    if not lines:
        raise ValueError("正文为空")

    name_m = re.search(r'^name:\s*"?([^"\n]+)"?', frontmatter_raw, re.M)
    desc_m = re.search(r'^description:\s*"?([^"\n]+)"?', frontmatter_raw, re.M)
    skill_name = (name_m.group(1).strip() if name_m else skill_dir.name)
    description = desc_m.group(1).strip() if desc_m else ""

    cache_path = out_root / ".cache" / (sha + ".json")
    l2_budget = int(hybrid_threshold * len(raw_bytes)) - 200      # 留给 L1 一行 + 余量
    ai, calls_sum, any_hit = None, 0, False
    if no_ai:
        if not cache_path.exists():
            raise ValueError("--remode 需要原始响应缓存：%s 不存在（不许调 AI）" % cache_path.name)
        raw_resp = cache_path.read_bytes()
        parsed = json.loads(raw_resp.decode("utf-8"))
        calls_sum, any_hit = 0, True
        log.append("--remode：从缓存重算产物（0 次调用、不花钱）")
    for attempt in range(1 if no_ai else 4):
        if not no_ai:
            raw_resp, parsed, calls, cache_hit = call_ai(cache_path, api_key, model, endpoint,
                                                         skill_name, description, lines, log, max_tokens,
                                                         granularity, l2_budget)
            calls_sum += calls
            any_hit = any_hit or cache_hit
        content = parsed["choices"][0]["message"]["content"]
        usage = parsed.get("usage") or {}
        finish = parsed["choices"][0].get("finish_reason")
        try:
            ai = extract_json_object(content)
            if finish == "length":
                raise ValueError("finish_reason=length（被 max_tokens=%d 截断）" % max_tokens)
            # 深校验：切条归一 + 映射表（悬空 id / 越界 / 重叠 在这里暴露）
            _s, _ = normalize_strips(ai.get("strips") or [], len(lines))
            normalize_questions(ai.get("questions"), make_strip_records(_s, lines, "x", "x"))
            break
        except Exception as e:
            if no_ai or attempt == 3:
                raise
            log.append("AI 回应未过校验（%s）⇒ 丢弃这次缓存并重抽（第 %d 次）" % (e, attempt + 1))
            try:
                cache_path.unlink()          # 坏回应不留：它永远用不了
            except OSError:
                pass
            ai = None
    calls = calls_sum
    cache_hit = (calls_sum == 0)
    log.append("AI 产出：strips=%d，finish_reason=%s，usage=%s"
               % (len(ai.get("strips") or []), finish,
                  {k: usage.get(k) for k in ("prompt_tokens", "completion_tokens",
                                             "prompt_cache_hit_tokens")}))

    short_name = str(ai.get("short_name") or "").strip().lower()
    raw_short = short_name
    short_name = re.sub(r"[^a-z0-9]", "", short_name)[:6]
    if prev_meta and prev_meta.get("short_name"):
        if short_name != prev_meta["short_name"]:
            log.append("注意：AI 给了新短名 %r，沿用账本里的 %r（保 id 稳定）"
                       % (short_name, prev_meta["short_name"]))
        short_name = prev_meta["short_name"]
    else:
        taken = set()
        for other in out_root.glob("*/.meta.json"):
            if other.parent.name == skill_dir.name:
                continue
            try:
                taken.add(json.loads(other.read_text(encoding="utf-8"))["short_name"])
            except Exception:
                pass
        if short_name in taken:
            suffix = 0
            while (short_name[:5] + str(suffix)) in taken or len(short_name[:5] + str(suffix)) < 2:
                suffix += 1
            short_name = short_name[:5] + str(suffix)
    if not re.fullmatch(r"[a-z0-9]{2,6}", short_name or ""):
        fb = (re.sub(r"[^a-z0-9]", "", skill_name.lower()) or "sk")[:6]
        log.append("短名不合法（%r）⇒ 改用从技能名推导的 %r" % (raw_short, fb))
        short_name = fb
    if raw_short != short_name:
        log.append("短名归一：%r → %r" % (raw_short, short_name))

    strips, questions, cov, arts_idx = derive_products(ai, lines, ends_nl, frontmatter_raw,
                                                      skill_name, description, short_name)
    qstats_idx = question_stats(questions, strips)
    log.append("L2 问题地图（indexed 口径）：%d 个问题（一对多 %d 条，均引用 %.2f 条）→ 指向 %d/%d 条 L3；孤儿条 %d"
               % (qstats_idx["questions"], qstats_idx["one_to_many"], qstats_idx["refs_per_question_avg"],
                  qstats_idx["strips_referenced"], len(strips), len(qstats_idx["orphan_strips"])))
    log.append("L1 法则：%s" % arts_idx["_l1_row"]["l1"])

    if arts_idx["_body_reconstructed"] != raw_bytes:
        raise ValueError("覆盖校验失败：重建字节 != 源字节（不许丢内容，硬闸门）")

    # ---- 两态机制（indexed / hybrid）
    mode, arts, mode_info = apply_mode(arts_idx, None, skill_name, description, short_name,
                                       ai.get("l1") or "", lines, frontmatter_raw, ends_nl,
                                       questions, strips, len(raw_bytes), hybrid_threshold,
                                       abs_token_cap, force_mode=force_mode)
    if mode == "hybrid":
        log.append("两态（双条件）：indexed 口径 %.4f / %.1f token ⇒ **hybrid**（%s）"
                   % (mode_info["ratio_indexed"], mode_info["layout_indexed_tokens"], mode_info["reason"]))
        strips = hybrid_strip_records(skill_name, short_name, lines, mode_info["guide_domain"])
        questions = []
        log.append("hybrid 产出：L1 法则（保留）+ L2 单条引导 → 见 L3 %s + L3 一条（整份正文 %d B）"
                   % (mode_info["guide_id"], len("\n".join(lines).encode("utf-8"))))
    else:
        log.append("两态（双条件）：ratio %.4f ≤ %.4f 且 %.1f token ≤ %d ⇒ **indexed**（常驻 %d B）"
                   % (mode_info["ratio_indexed"], hybrid_threshold,
                      mode_info["layout_indexed_tokens"], abs_token_cap, mode_info["layout_bytes"]))
    qstats = question_stats(questions, strips) if questions else {
        "questions": 1, "refs_total": 1, "one_to_many": 0,
        "refs_per_question_avg": 1.0, "strips_referenced": 1, "orphan_strips": [],
    }

    prev_ids = set((prev_meta or {}).get("ids") or [])
    new_ids = [s["id"] for s in strips]
    tombstones = sorted(prev_ids - set(new_ids))

    meta = {
        "schema": "skill-repo/meta/1",
        "skill": skill_name,
        "short_name": short_name,
        "id_prefix": "S-" + short_name,
        "mode": mode,
        "strategy": strategy,
        "threshold": hybrid_threshold,
        "ratio": mode_info["ratio"],
        "ratio_indexed": mode_info["ratio_indexed"],
        "layout_bytes": mode_info["layout_bytes"],
        "layout_tokens": mode_info["layout_tokens"],
        "layout_indexed_bytes": mode_info["layout_indexed_bytes"],
        "layout_indexed_tokens": mode_info["layout_indexed_tokens"],
        "hybrid_threshold": hybrid_threshold,
        "abs_token_cap": abs_token_cap,
        "bytes_per_token": BYTES_PER_TOKEN,
        "hybrid_rule": HYBRID_RULE_VERSION,
        "benefit": benefit,
        "l1_domain": majority_domain(strips, questions),
        "hybrid": mode_info,
        "extractor": {"name": EXTRACTOR_NAME, "version": EXTRACTOR_VERSION,
                      "granularity": granularity, "prompt_version": PROMPT_VERSIONS[granularity]},
        "bump": bump,
        "source": {
            "path": str(src_path.resolve()),
            "sha256": sha,
            "bytes": len(raw_bytes),
            "frontmatter_sha256": sha_s(frontmatter_raw),
            "description": description,
            "body_lines": len(lines),
            "body_ends_with_newline": ends_nl,
        },
        "frontmatter_raw": frontmatter_raw,
        "ai": {
            "endpoint": endpoint,
            "model": model,
            "prompt_sha256": sha_s(system_prompt(granularity) + "\n\n"
                                   + build_prompt(skill_name, description, lines, l2_budget)),
            "l2_budget_bytes": l2_budget,
            "cache_path": str(cache_path.relative_to(out_root.parent)) if cache_path.parent.parent == out_root else str(cache_path),
            "cache_sha256": sha_b(raw_resp),
            "calls": calls,
            "calls_total": int(((prev_meta or {}).get("ai") or {}).get("calls_total", 0)) + calls,            "usage_total": _accum_usage(((prev_meta or {}).get("ai") or {}).get("usage_total"), calls, usage),
            "cache_hit": cache_hit,
            "finish_reason": finish,
            "max_tokens": max_tokens,
            "usage": {k: usage.get(k) for k in ("prompt_tokens", "completion_tokens", "total_tokens",
                                                "prompt_cache_hit_tokens", "prompt_cache_miss_tokens")},
            "reasoning_tokens": (usage.get("completion_tokens_details") or {}).get("reasoning_tokens"),
        },
        "l1": arts["_l1_row"]["l1"],
        "l2_questions": [{"domain": q["domain"], "q": q["q"], "seqs": q["seqs"], "ids": q["ids"],
                          "one_to_many": q["one_to_many"]} for q in questions],
        "l2_stats": qstats,
        "l2_stats_indexed": qstats_idx,
        "strips": [{"id": s["id"], "seq": s["seq"], "title": s["title"], "kind": s["kind"],
                    "domain": s["domain"],
                    "start_line": s["start"], "end_line": s["end"],
                    "lines": s["end"] - s["start"] + 1, "bytes": len(s["text"].encode("utf-8")),
                    "text_sha256": sha_s(s["text"])} for s in strips],
        "coverage": {
            "body_lines": len(lines),
            "claimed_lines": cov["ai_claimed_lines"],
            "gap_lines_absorbed": cov["gap_lines"],
            "gap_ranges_absorbed_into_next": cov["gap_ranges"],
            "ai_raw_coverage_pct": round(100.0 * cov["ai_claimed_lines"] / len(lines), 2),
            "overlap_lines": 0,
            "complete": True,
        },
        "artifacts": {n: sha_b(arts[n]) for n in ARTIFACT_NAMES},
        "abstract_sha256": sha_b(jcs({
            "short_name": short_name,
            "mode": mode,
            "ratio": mode_info["ratio"],
            "l1": arts["_l1_row"]["l1"],
            "questions": [{"domain": q["domain"], "q": q["q"], "ids": q["ids"]} for q in questions],
            "strips": [{"id": s["id"], "seq": s["seq"], "title": s["title"], "kind": s["kind"],
                        "domain": s["domain"], "start": s["start"],
                        "end": s["end"], "text_sha256": sha_s(s["text"])} for s in strips],
        })),
        "ids": new_ids,
        "tombstones": tombstones,
    }

    outdir = out_root / skill_name
    write_artifacts(outdir, arts)
    (outdir / ".meta.json").write_bytes(
        (json.dumps(meta, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    log.append("写出 %s/：L1.md / L2.md / L3.jsonl / .meta.json（%d 条）" % (skill_name, len(strips)))
    return meta


# ---------------------------------------------------------------- 校验

def check_skill(out_root: Path, skill_name: str):
    """三闸门 + 覆盖 + 映射 + id + 域；返回 (results, meta)。"""
    outdir = out_root / skill_name
    res = []

    def ok(name, detail=""):
        res.append(("PASS", name, detail))

    def warn(name, detail=""):
        res.append(("WARN", name, detail))

    def bad(name, detail=""):
        res.append(("FAIL", name, detail))

    meta_path = outdir / ".meta.json"
    if not meta_path.exists():
        bad("账本存在", "%s 不存在（先 build）" % meta_path)
        return res, None
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    ok("账本存在", str(meta_path))

    src_path = Path(meta["source"]["path"])
    if not src_path.exists():
        warn("源可读", "源已归档/卸载：%s（跳过源指纹与重算比对，不算失败）" % src_path)
        return res, meta
    raw = src_path.read_bytes()
    sha = sha_b(raw)

    # ---- 闸门 ① 源指纹
    if sha != meta["source"]["sha256"]:
        bad("闸门①源指纹", "源已变更（现 %s ≠ 账本 %s）⇒ 加 --bump 重新抽取"
            % (sha[:12], meta["source"]["sha256"][:12]))
    else:
        ok("闸门①源指纹", "源 %s 与账本一致（bump=%d）" % (sha[:12], meta["bump"]))

    # ---- 闸门 ② 产物被手改
    for name in ARTIFACT_NAMES:
        p = outdir / name
        if not p.exists():
            bad("闸门②产物完好", "%s 不存在" % name)
        elif sha_b(p.read_bytes()) != meta["artifacts"][name]:
            bad("闸门②产物完好", "%s 被手改（sha 不符）" % name)
        else:
            ok("闸门②产物完好", name)

    # ---- 闸门 ③ 重算一致
    cache_path = out_root / ".cache" / (meta["source"]["sha256"] + ".json")
    if not cache_path.exists():
        bad("闸门③重算一致", "AI 原始响应缓存缺失（%s）⇒ 无法复算" % cache_path.name)
    else:
        cached = cache_path.read_bytes()
        if sha_b(cached) != meta["ai"]["cache_sha256"]:
            bad("闸门③重算一致", "缓存文件被改（sha 不符）")
        else:
            text = raw.decode("utf-8")
            frontmatter_raw, body = split_frontmatter(text)
            lines, ends_nl = split_lines(body)
            ai = extract_json_object(json.loads(cached.decode("utf-8"))["choices"][0]["message"]["content"])
            ai = dict(ai)
            ai["l1"] = meta.get("l1", "")
            strips, questions, _cov, arts_idx = derive_products(ai, lines, ends_nl, frontmatter_raw,
                                                               meta["skill"],
                                                               meta["source"].get("description", ""),
                                                               meta["short_name"])
            thr = (meta.get("hybrid") or {}).get("threshold", HYBRID_THRESHOLD_DEFAULT)
            cap = (meta.get("hybrid") or {}).get("abs_token_cap", ABS_TOKEN_CAP_DEFAULT)
            strat_chk = meta.get("strategy", "dual")
            fm = None
            if strat_chk == "benefit":
                fm = "indexed" if (meta.get("benefit") or {}).get("selected") else "hybrid"
            mode, arts, _mi = apply_mode(arts_idx, None, meta["skill"],
                                         meta["source"].get("description", ""), meta["short_name"],
                                         meta.get("l1", ""), lines, frontmatter_raw, ends_nl,
                                         questions, strips, meta["source"]["bytes"], thr, cap,
                                         force_mode=fm)
            same = all(arts[n] == (outdir / n).read_bytes() for n in ARTIFACT_NAMES)
            if same and sha_b(arts["_body_reconstructed"]) == sha and mode == meta.get("mode"):
                ok("闸门③重算一致", "从缓存重算产物与磁盘逐字节相同（mode=%s%s）"
                   % (mode, "，strategy=benefit" if strat_chk == "benefit" else ""))
            else:
                bad("闸门③重算一致", "从缓存重算产物/模式与磁盘不一致（重算 mode=%s，账本 %s）"
                    % (mode, meta.get("mode")))

    # ---- 覆盖校验（不许丢内容）
    l3 = [json.loads(ln) for ln in (outdir / "L3.jsonl").read_text(encoding="utf-8").splitlines() if ln.strip()]
    text = raw.decode("utf-8")
    frontmatter_raw, body = split_frontmatter(text)
    lines, ends_nl = split_lines(body)
    rebuilt = "\n".join(r["text"] for r in l3) + ("\n" if ends_nl else "")
    if rebuilt == body:
        ok("覆盖校验", "L3 逐条拼接 == 源正文（%d 行 / %d 字节，逐字节相等，0 丢失）"
           % (len(lines), len(body.encode("utf-8"))))
    else:
        bad("覆盖校验", "L3 拼接 != 源正文（%d vs %d 字节）" % (len(rebuilt.encode("utf-8")), len(body.encode("utf-8"))))
    if frontmatter_raw + rebuilt == text:
        ok("frontmatter 保留", "整份重建（frontmatter+正文）逐字节等于源文件")
    else:
        bad("frontmatter 保留", "整份重建与源文件不等")

    # ---- 映射完整（源块 → 条，每行恰好命中 1 条；无重叠）
    owner = [0] * len(lines)
    for s in meta["strips"]:
        for ln in range(s["start_line"], s["end_line"] + 1):
            owner[ln - 1] += 1
    holes = [i + 1 for i, c in enumerate(owner) if c == 0]
    dups = [i + 1 for i, c in enumerate(owner) if c > 1]
    if not holes and not dups:
        cov = meta["coverage"]
        ok("映射完整/不重叠", "%d/%d 行各命中恰好 1 条（AI 原始覆盖 %.2f%%，缺口 %d 行已并入后一条）"
           % (len(lines), len(lines), cov["ai_raw_coverage_pct"], cov["gap_lines_absorbed"]))
    else:
        bad("映射完整/不重叠", "未覆盖行 %d 个（%s…）；重叠行 %d 个" % (len(holes), holes[:5], len(dups)))

    # ---- L2 映射表（问题 → 若干条 L3）：悬空 id 必须报错
    l3_ids = {r["id"] for r in l3}
    l2_text = (outdir / "L2.md").read_text(encoding="utf-8") if (outdir / "L2.md").exists() else ""
    l2_rows = [ln for ln in l2_text.splitlines() if ln.startswith("- [")]
    refd, dangling, no_ref, bad_fmt = [], [], [], []
    for ln in l2_rows:
        m = re.match(r"^- \[([a-z]+)\] (.+?) → 见 L3 (S-[a-z0-9]+-\d{3}(?: S-[a-z0-9]+-\d{3})*)$", ln)
        if not m:
            bad_fmt.append(ln[:40])
            continue
        ids = m.group(3).split()
        refd.extend(ids)
        for i in ids:
            if i not in l3_ids:
                dangling.append(i)
    if bad_fmt:
        bad("L2 行格式", "不符合「- [domain] 问题 → 见 L3 <id> [<id>…]」：%s" % bad_fmt[:2])
    elif dangling:
        bad("L2 映射表 id 存在", "**悬空 id**（L3 里没有）：%s" % sorted(set(dangling))[:5])
    elif not l2_rows:
        bad("L2 映射表 id 存在", "L2 没有任何问题行")
    else:
        st = meta.get("l2_stats") or question_stats(
            [{"domain": q_["domain"], "q": q_["q"], "seqs": q_["seqs"], "ids": q_["ids"],
              "one_to_many": len(q_["ids"]) > 1} for q_ in meta.get("l2_questions", [])], meta["strips"])
        ok("L2 映射表 id 存在", "%d 个问题行 / %d 次引用，全部 id 在 L3 中存在（一对多 %d 条）"
           % (len(l2_rows), len(refd), st["one_to_many"]))
        if st["orphan_strips"]:
            warn("条被问题引用", "有 %d 条 L3 未被任何问题引用：%s"
                 % (len(st["orphan_strips"]), st["orphan_strips"][:5]))
        else:
            ok("条被问题引用", "%d/%d 条 L3 至少被一个具体问题引用（0 孤儿）"
               % (st["strips_referenced"], len(meta["strips"])))

    # ---- 两态门槛自洽（dual：相对+绝对；benefit：仓库级收益比/预算分配）
    mode = meta.get("mode", "indexed")
    strat = meta.get("strategy", "dual")
    if strat == "benefit":
        b = meta.get("benefit") or {}
        br = b.get("benefit_ratio")
        sel = b.get("selected")
        want = "indexed" if sel else "hybrid"
        g_ok = bool(b.get("gate_tokens_ok")) and bool(b.get("gate_size_ok"))
        if br is None:
            bad("两态门槛自洽", "strategy=benefit 但账本缺 benefit_ratio")
        elif mode != want:
            bad("两态门槛自洽", "strategy=benefit：mode=%s 与 selected=%s 不匹配" % (mode, sel))
        elif sel and not g_ok:
            bad("两态门槛自洽", "入选 indexed 但硬闸门不过：tokens_ok=%s size_ok=%s"
                % (b.get("gate_tokens_ok"), b.get("gate_size_ok")))
        else:
            ok("两态门槛自洽", "strategy=benefit：收益比 %.3f，%s（硬闸门 %s）"
               % (br, "入选 indexed" if sel else "未入选 ⇒ hybrid", "过" if g_ok else "不过"))
    else:
        hy = meta.get("hybrid") or {}
        thr = hy.get("threshold", meta.get("hybrid_threshold", HYBRID_THRESHOLD_DEFAULT))
        cap = hy.get("abs_token_cap", meta.get("abs_token_cap", ABS_TOKEN_CAP_DEFAULT))
        ratio = meta.get("ratio")
        ri = meta.get("ratio_indexed", ratio)
        ib = hy.get("layout_indexed_bytes", meta.get("layout_indexed_bytes"))
        it = hy.get("layout_indexed_tokens", meta.get("layout_indexed_tokens"))
        lb = meta.get("layout_bytes", hy.get("layout_bytes"))
        lt = meta.get("layout_tokens", hy.get("layout_tokens"))
        if ri is None or ib is None:
            bad("两态门槛自洽", "账本缺 ratio_indexed / layout_indexed_bytes（旧账本 ⇒ 需 --remode 重建）")
        elif ib != lb and mode == "indexed":
            bad("两态门槛自洽", "indexed 态下 layout_bytes(%s) ≠ layout_indexed_bytes(%s)" % (lb, ib))
        elif mode == "indexed" and ri <= thr and it <= cap:
            ok("两态门槛自洽", "indexed：ratio %.4f ≤ %.4f 且常驻 %d B = %.1f token ≤ %.0f"
               % (ri, thr, ib, it, cap))
        elif mode == "hybrid" and (ri > thr or it > cap):
            ok("两态门槛自洽", "hybrid：indexed 口径 ratio %.4f / %.1f token（阈 %.4f / 限 %.0f）→ 触发：%s"
               % (ri, it, thr, cap,
                  "、".join((["相对"] if ri > thr else []) + (["绝对"] if it > cap else []))))
        else:
            bad("两态门槛自洽", "mode=%s 与 ratio_indexed=%s / tokens=%s / 阈=%s / 限=%s 不自洽"
                % (mode, ri, it, thr, cap))
        lb = meta.get("layout_bytes", hy.get("layout_bytes"))
        lt = meta.get("layout_tokens", hy.get("layout_tokens"))
        if lb is not None:
            ok("常驻预算", "layout_bytes=%s / layout_tokens=%s（口径 %.2f B/token）"
               % (lb, lt, hy.get("bytes_per_token", BYTES_PER_TOKEN)))

    if mode == "hybrid":
        wid = "S-%s-%s" % (meta["short_name"], HYBRID_ID_SUFFIX)
        if (len(l3) == 1 and l3[0]["id"] == wid and l3[0]["seq"] == 1
                and l3[0]["kind"] == "WholeSkill" and l3[0]["text"] == "\n".join(lines)):
            ok("hybrid 形态", "L3 就一条 %s（kind=WholeSkill，text == 整份正文逐字 %d B）"
               % (wid, len(l3[0]["text"].encode("utf-8"))))
        else:
            bad("hybrid 形态", "L3 不是「S-<短名>-000 那条整份正文」的单条形态")

    # ---- id 稳定 + 逐条可寻址
    if mode == "hybrid":
        want = ["S-%s-%s" % (meta["short_name"], HYBRID_ID_SUFFIX)]
    else:
        want = ["S-%s-%03d" % (meta["short_name"], i + 1) for i in range(len(l3))]
    if [r["id"] for r in l3] == want and [r["seq"] for r in l3] == list(range(1, len(l3) + 1)):
        ok("id 稳定/可寻址", "id == S-%s-<%s>，L3 第 N 行 = seq N（%d 条，mode=%s）"
           % (meta["short_name"], "000" if mode == "hybrid" else "seq:03d", len(l3), mode))
    else:
        bad("id 稳定/可寻址", "id 或 seq 与行号不对应")
    if meta["ids"] == want:
        ok("账本 id 表", "与 L3 一致（墓碑 %d 个）" % len(meta.get("tombstones") or []))
    else:
        bad("账本 id 表", "与 L3 不一致")

    # ---- 域合法
    bad_dom = [r["id"] for r in l3 if r["domain"] not in DOMAINS]
    if bad_dom:
        bad("域合法", "非预设域：%s" % bad_dom[:5])
    else:
        ok("域合法", "全部落在 KnowledgeDomains 预设域")

    # ---- 条间功能不重叠（文本级：两两不同）
    dup = {}
    for r in l3:
        dup.setdefault(sha_s(r["text"]), []).append(r["id"])
    same = [v for v in dup.values() if len(v) > 1]
    if same:
        bad("条间内容不重复", "重复条：%s" % same[:3])
    else:
        ok("条间内容不重复", "无逐字节重复的条")

    u = meta["ai"]["usage"]
    ut = meta["ai"].get("usage_total") or {}
    ok("AI 用量", "本次 calls=%d（cache_hit=%s）prompt=%s completion=%s total=%s cache_hit_tokens=%s；历史累计 calls=%s / prompt=%s / completion=%s"
       % (meta["ai"]["calls"], meta["ai"]["cache_hit"], u.get("prompt_tokens"),
          u.get("completion_tokens"), u.get("total_tokens"), u.get("prompt_cache_hit_tokens"),
          meta["ai"].get("calls_total"), ut.get("prompt_tokens"), ut.get("completion_tokens")))
    return res, meta


def _l2_rows(m):
    """一个技能的 L2 汇总行：indexed = 每条 l2_question 一行；hybrid = 单条引导一行。"""
    if m.get("mode") == "hybrid":
        domain = ((m.get("strips") or [{}])[0].get("domain")
                  or m.get("l1_domain") or "misc")
        wid = m.get("id_prefix", "S-x") + "-" + HYBRID_ID_SUFFIX
        return [{"skill": m["skill"], "domain": domain, "q": HYBRID_GUIDE_TMPL,
                 "ids": [wid], "seqs": [1], "one_to_many": False}]
    return [{"skill": m["skill"], "domain": q["domain"], "q": q["q"],
             "ids": q["ids"], "seqs": q["seqs"], "one_to_many": q["one_to_many"]}
            for q in m.get("l2_questions", [])]


def _l1_row(m):
    return {"skill": m["skill"], "short_name": m["short_name"], "id_prefix": m["id_prefix"],
            "mode": m.get("mode"), "ratio": m.get("ratio"), "ratio_indexed": m.get("ratio_indexed"),
            "description": m["source"].get("description", ""), "l1": m["l1"],
            "entries": len(m["strips"]), "questions": len(_l2_rows(m)),
            "domains": sorted({s["domain"] for s in m["strips"]})}


def check_repo_level(out_root: Path, metas):
    """校验全库汇总：存在 + 条数 == 全库技能数 / 问题数（防增量静默截断）。"""
    res = []
    expected = {"_l1.jsonl": len(metas), "_l2.jsonl": sum(len(_l2_rows(m)) for m in metas)}
    for name in REPO_LEVEL_NAMES:
        p = out_root / name
        if not p.exists():
            res.append(("FAIL", "全库汇总 %s" % name, "缺失"))
            continue
        n = sum(1 for ln in p.read_text(encoding="utf-8").splitlines() if ln.strip())
        if n == expected[name]:
            res.append(("PASS", "全库汇总 %s" % name, "%d 行（= 全库 %d）" % (n, expected[name])))
        else:
            res.append(("FAIL", "全库汇总 %s" % name, "%d 行 ≠ 全库 %d（增量静默截断？）" % (n, expected[name])))
    return res


def write_repo_level(out_root: Path, metas):
    """写全库汇总（**合并语义**）：读回已有 _l1/_l2，按 skill 键替换本次处理的技能、其余保留 ⇒ 增量运行不截断。"""
    l1, l2 = {}, {}          # l2: skill -> 行列表（保持该技能内行序）
    p1, p2 = out_root / "_l1.jsonl", out_root / "_l2.jsonl"
    if p1.exists():
        for ln in p1.read_text(encoding="utf-8").splitlines():
            if ln.strip():
                r = json.loads(ln)
                l1[r["skill"]] = r
    if p2.exists():
        for ln in p2.read_text(encoding="utf-8").splitlines():
            if ln.strip():
                r = json.loads(ln)
                l2.setdefault(r["skill"], []).append(r)
    for m in metas:
        l1[m["skill"]] = _l1_row(m)
        l2[m["skill"]] = _l2_rows(m)
    (out_root / "_l1.jsonl").write_text(
        "\n".join(json.dumps(l1[s], ensure_ascii=False) for s in sorted(l1)) + "\n", encoding="utf-8")
    l2_lines = []
    for s in sorted(l2):
        l2_lines.extend(json.dumps(r, ensure_ascii=False) for r in l2[s])
    (out_root / "_l2.jsonl").write_text("\n".join(l2_lines) + "\n", encoding="utf-8")


# ---------------------------------------------------------------- main

def load_key(path: str) -> str:
    p = Path(path).expanduser()
    if not p.exists():
        die("密钥文件不存在：%s（只传路径，内容不打印）" % p)
    key = p.read_text(encoding="utf-8").strip()
    if not key:
        die("密钥文件为空：%s" % p)
    print("[KEY ] 已从文件读取密钥（%s）；内容不回显、不写日志、不写配置" % p)
    return key


def main():
    ap = argparse.ArgumentParser(description="SKILL.md → L1/L2/L3 技能仓库（切条 = 远端 AI 抽象）")
    ap.add_argument("--skills", required=True, help="技能源目录（含 <name>/SKILL.md；只读）")
    ap.add_argument("--out", required=True, help="仓库输出根目录")
    ap.add_argument("--only", default=None, help="只处理这些技能（逗号分隔）")
    ap.add_argument("--api-key-file", default=DEFAULT_KEY_FILE,
                    help="密钥文件路径（默认 = 运行时那一把：%s；只传路径，内容不读不打印）" % DEFAULT_KEY_FILE)
    ap.add_argument("--model", default=DEFAULT_MODEL, help="模型（默认 %s）" % DEFAULT_MODEL)
    ap.add_argument("--endpoint", default=DEFAULT_ENDPOINT, help="OpenAI 兼容端点")
    ap.add_argument("--max-tokens", type=int, default=MAX_TOKENS,
                    help="单次输出上限（含思维链，默认 %d）" % MAX_TOKENS)
    ap.add_argument("--granularity", choices=["fine", "coarse"], default=DEFAULT_GRANULARITY,
                    help="切条粒度：fine=尽量多（默认）；coarse=完整功能单元优先（条数少、L2 更短）")
    ap.add_argument("--hybrid-threshold", type=float, default=HYBRID_THRESHOLD_DEFAULT,
                    help="相对闸门：ratio=(L1+L2)/整份 ≤ 阈值 ⇒ 可索引（默认 %.2f）"
                         % HYBRID_THRESHOLD_DEFAULT)
    ap.add_argument("--abs-token-cap", type=float, default=ABS_TOKEN_CAP_DEFAULT,
                    help="绝对闸门：常驻 L1+L2 ≤ 上限 token（默认 %d；口径 %.2f B/token）"
                         % (ABS_TOKEN_CAP_DEFAULT, BYTES_PER_TOKEN))
    ap.add_argument("--remode", action="store_true",
                    help="按 --hybrid-threshold 从**缓存重算**两态并重写产物（不调 AI、不花钱；需已建账本）")
    ap.add_argument("--strategy", choices=["dual", "benefit"], default="dual",
                    help="两态决策策略：dual=本地双条件（默认）；benefit=仓库级收益比+常驻预算分配")
    ap.add_argument("--resident-budget", default="8000",
                    help="benefit 策略的全库常驻预算（token；可给多个逗号分隔值做多档演示）")
    ap.add_argument("--mode-dry-run", action="store_true",
                    help="只读：按 --hybrid-threshold 重算两态，列出「会翻态」的技能（不调 AI、不写产物）")
    ap.add_argument("--check", action="store_true", help="只校验（三闸门 + 覆盖 + 映射）")
    ap.add_argument("--bump", action="store_true", help="源已变更时显式接受并重抽（bump+1）")
    args = ap.parse_args()

    skills_root = Path(args.skills).expanduser().resolve()
    out_root = Path(args.out).expanduser().resolve()
    if args.only:
        names = [n.strip() for n in args.only.split(",") if n.strip()]
    elif args.check and out_root.exists():
        # --check 的天然范围 = 仓库里已有的技能（而不是源目录里的全部）
        names = sorted(p.name for p in out_root.iterdir()
                       if p.is_dir() and (p / ".meta.json").exists())
    else:
        names = sorted(p.name for p in skills_root.iterdir()
                       if p.is_dir() and (p / "SKILL.md").exists())
    if not names:
        die("没有可处理的技能（%s）" % skills_root)

    print("[INFO] 技能源：%s（只读）" % skills_root)
    print("[INFO] 输出：%s" % out_root)
    print("[INFO] 抽取器：%s %s / prompt %s / 模型 %s" % (EXTRACTOR_NAME, EXTRACTOR_VERSION,
                                                         PROMPT_VERSIONS[args.granularity], args.model))
    print("[INFO] 目标技能：%s" % ", ".join(names))

    if args.check:
        failed = 0
        metas = []
        for name in names:
            print("\n=== --check %s ===" % name)
            res, meta = check_skill(out_root, name)
            if meta:
                metas.append(meta)
            for status, cname, detail in res:
                print("  [%s] %-16s %s" % (status, cname, detail))
                if status == "FAIL":
                    failed += 1
        if len(names) > 1 and len(metas) == len(names):
            print("\n=== --check 全库汇总 ===")
            for status, cname, detail in check_repo_level(out_root, metas):
                print("  [%s] %-16s %s" % (status, cname, detail))
                if status == "FAIL":
                    failed += 1
        print("\n[CHECK] %s（失败 %d 项）" % ("全部通过" if failed == 0 else "存在失败项", failed))
        sys.exit(1 if failed else 0)

    if args.mode_dry_run and args.strategy == "benefit":
        metas_all = [json.loads((out_root / p.name / ".meta.json").read_text(encoding="utf-8"))
                     for p in sorted(out_root.iterdir())
                     if p.is_dir() and (p / ".meta.json").exists()
                     and (not args.only or p.name in args.only.split(","))]
        print("[DRY-RUN] 策略=benefit（只读、0 调用、不写产物）")
        print("收益比 = (整份 − 单条) ÷ L2；硬闸门：L1+L2 ≤ %.0f token，整份 ≤ %d B 直接 hybrid；"
              "口径 %.2f B/token" % (args.abs_token_cap, SMALL_FILE_HYBRID_CAP, BYTES_PER_TOKEN))
        for budget in [float(x) for x in str(args.resident_budget).split(",") if x.strip()]:
            plan, summ = benefit_plan(metas_all, budget, hard_token_cap=args.abs_token_cap)
            print("\n### 常驻预算 %.0f token" % budget)
            print("| 排序 | 技能 | 整份 B | 单条 B | L2(indexed) B | 收益比 | 入选中成本 token | 决策 | 原因 |")
            print("|---|---|---|---|---|---|---|---|---|")
            ranked = sorted([(n, i) for n, i in plan.items() if i.get("benefit_ratio") is not None],
                            key=lambda x: -x[1]["benefit_ratio"])
            for rank, (n, i) in enumerate(ranked, 1):
                print("| %d | %s | %d | %.0f | %d | %.3f | %.1f | **%s** | %s |"
                      % (rank, n, i["src_bytes"], i["strip_avg_bytes"], i["l2_indexed_bytes"],
                         i["benefit_ratio"], i["cost_tokens"],
                         "indexed" if i.get("selected") else "hybrid", i["reason"]))
            for n, i in sorted(plan.items()):
                if i.get("benefit_ratio") is None:
                    print("| — | %s | — | — | — | — | — | **hybrid** | %s |" % (n, i["reason"]))
            print("[DRY-RUN] 预算 %.0f：indexed %d / hybrid %d；结果常驻 %.1f token（预算占用 %.1f%%）"
                  % (budget, len(summ["indexed"]), len(summ["hybrid"]), summ["used_tokens"],
                     100.0 * summ["used_tokens"] / budget))
            print("[DRY-RUN] indexed：%s" % ", ".join(summ["indexed"]))
        print("\n[DRY-RUN] 真的应用：`--remode --strategy benefit --resident-budget <x> --abs-token-cap %.0f`"
              % args.abs_token_cap)
        return

    if args.mode_dry_run:
        names = [n.strip() for n in args.only.split(",")] if args.only else \
                sorted(p.name for p in out_root.iterdir()
                       if p.is_dir() and (p / ".meta.json").exists())
        thr = args.hybrid_threshold
        cap = args.abs_token_cap
        flips, same = [], []
        print("[DRY-RUN] 只读重算：相对阈 %.4f + 绝对限 %.0f token（口径 %.2f B/token）；不调 AI、不写产物"
              % (thr, cap, BYTES_PER_TOKEN))
        print("| 技能 | 整份 B | L1 B | L2(indexed) B | ratio_indexed | index 口径 token | 当前 mode | 重算 mode | 翻态 | 触发 |")
        print("|---|---|---|---|---|---|---|---|---|---|")
        for n in names:
            mp = out_root / n / ".meta.json"
            if not mp.exists():
                print("| %s | — | — | — | — | — | 无账本 | — | — |" % n)
                continue
            m = json.loads(mp.read_text(encoding="utf-8"))
            hy = m.get("hybrid") or {}
            ri = m.get("ratio_indexed", m.get("ratio"))
            cur = m.get("mode", "indexed")
            ib = hy.get("layout_indexed_bytes")
            it = hy.get("layout_indexed_tokens") or (round(ib / BYTES_PER_TOKEN, 1) if ib else None)
            l1 = hy.get("l1_bytes") if False else None
            l1 = (out_root / n / "L1.md").stat().st_size if (out_root / n / "L1.md").exists() else None
            l2i = (ib - l1) if (ib is not None and l1 is not None) else None
            new = "indexed" if (ri <= thr and it is not None and it <= cap) else "hybrid"
            why = []
            if ri > thr:
                why.append("相对")
            if it is not None and it > cap:
                why.append("绝对")
            flag = "→ %s" % new if new != cur else "—"
            print("| %s | %d | %s | %s | %.4f | %s | %s | **%s** | %s | %s |"
                  % (n, m["source"]["bytes"], l1 if l1 is not None else "—",
                     l2i if l2i is not None else "—", ri, it if it is not None else "—",
                     cur, new, flag if new != cur else "—",
                     ("、".join(why) if new == "hybrid" else "双条件都过")))
            (flips if new != cur else same).append((n, cur, new))
        print("\n[DRY-RUN] 会翻态 %d 个：%s" % (len(flips), ", ".join("%s %s→%s" % f for f in flips) or "（无）"))
        print("[DRY-RUN] 不变 %d 个" % len(same))
        print("[DRY-RUN] 真的切换：`--remode --hybrid-threshold %.4f --abs-token-cap %.0f`（从缓存重算，0 次调用）"
              % (thr, cap))
        return

    metas = []
    total_calls = 0
    benefit_by_name = {}
    if args.remode and args.strategy == "benefit":
        all_metas = [json.loads((p / ".meta.json").read_text(encoding="utf-8"))
                     for p in sorted(out_root.iterdir())
                     if p.is_dir() and (p / ".meta.json").exists()]
        budget = float(str(args.resident_budget).split(",")[0])
        plan, summ = benefit_plan(all_metas, budget, hard_token_cap=args.abs_token_cap)
        benefit_by_name = plan
        print("[PLAN] 策略=benefit，常驻预算 %.0f token ⇒ indexed %d / hybrid %d（预计常驻 %.1f token）"
              % (budget, len(summ["indexed"]), len(summ["hybrid"]), summ["used_tokens"]))
    for name in names:
        skill_dir = skills_root / name
        outdir = out_root / name
        meta_path = outdir / ".meta.json"
        prev = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else None
        src_sha = sha_b((skill_dir / "SKILL.md").read_bytes())

        if args.remode:
            if prev is None:
                die("--remode 需要已建账本：%s 不存在" % meta_path)
            key = args.api_key_file and load_key(args.api_key_file) or None
            bump = prev["bump"] + 1
            force_mode, benefit = None, None
            if args.strategy == "benefit":
                bi = benefit_by_name.get(name) or {}
                if bi.get("benefit_ratio") is None:
                    die("%s 账本缺 indexed 口径字段，无法做收益比分配" % name)
                force_mode = "indexed" if bi.get("selected") else "hybrid"
                benefit = dict(bi, budget_tokens=float(str(args.resident_budget).split(",")[0]))
            print("\n=== %s：--remode（策略 %s，阈值 %.4f，bump=%d）==="
                  % (name, args.strategy, args.hybrid_threshold, bump))
            log = []
            try:
                meta = extract_skill(skill_dir, out_root, key, args.model, args.endpoint, bump, prev, log,
                                     args.max_tokens, args.granularity, args.hybrid_threshold, no_ai=True,
                                     abs_token_cap=args.abs_token_cap, strategy=args.strategy,
                                     force_mode=force_mode, benefit=benefit)
            except Exception as e:
                for line in log:
                    print("  · %s" % line)
                die("%s --remode 失败：%s" % (name, redact(str(e))))
            for line in log:
                print("  · %s" % line)
            metas.append(meta)
            res, _ = check_skill(out_root, name)
            for status, cname, detail in res:
                print("  [%s] %-16s %s" % (status, cname, detail))
            if any(st == "FAIL" for st, _, _ in res):
                die("%s --remode 后自检未通过" % name)
            continue

        if prev and prev["source"]["sha256"] != src_sha and not args.bump:
            die("闸门①：%s 的源已变更（%s → %s），账本未 bump ⇒ 加 --bump 重新抽取"
                % (name, prev["source"]["sha256"][:12], src_sha[:12]))
        if prev and prev["source"]["sha256"] == src_sha and not args.bump:
            print("\n=== %s：源未变，复用账本 ===" % name)
            res, meta = check_skill(out_root, name)
            for status, cname, detail in res:
                print("  [%s] %-16s %s" % (status, cname, detail))
            if any(st == "FAIL" for st, _, _ in res):
                die("复用失败：账本/产物不自洽 ⇒ 加 --bump 重抽")
            metas.append(meta)
            continue

        if prev is None and not args.api_key_file:
            die("首次构建需要 --api-key-file <路径>（只传路径）")
        key = load_key(args.api_key_file)
        bump = (prev["bump"] + 1) if prev else 1
        print("\n=== %s：抽取（bump=%d）===" % (name, bump))
        log = []
        t0 = time.time()
        try:
            meta = extract_skill(skill_dir, out_root, key, args.model, args.endpoint, bump, prev, log,
                                 args.max_tokens, args.granularity, args.hybrid_threshold,
                                 abs_token_cap=args.abs_token_cap)
        except Exception as e:
            for line in log:
                print("  · %s" % line)
            die("%s 抽取失败：%s" % (name, redact(str(e))))
        for line in log:
            print("  · %s" % line)
        print("  · 耗时 %.1fs" % (time.time() - t0))
        total_calls += meta["ai"]["calls"]
        metas.append(meta)

        res, _ = check_skill(out_root, name)
        for status, cname, detail in res:
            print("  [%s] %-16s %s" % (status, cname, detail))
        if any(st == "FAIL" for st, _, _ in res):
            die("%s 构建后自检未通过" % name)

    if metas:
        write_repo_level(out_root, metas)
        print("\n[INFO] 已写全库汇总：_l1.jsonl（%d 技能）/ _l2.jsonl（%d 个问题）"
              % (len(metas), sum(len(m.get("l2_questions") or []) for m in metas)))

    print("\n[汇总] 本次真实 API 调用 %d 次" % total_calls)
    for m in metas:
        u = m["ai"]["usage"]
        print("  · %-24s 条数 %3d | 问题 %3d | prompt %6s | completion %6s | total %6s | cache_hit_tokens %s"
              % (m["skill"], len(m["strips"]), (m.get("l2_stats") or {}).get("questions", 0),
                 u.get("prompt_tokens"), u.get("completion_tokens"),
                 u.get("total_tokens"), u.get("prompt_cache_hit_tokens")))
    print("[汇总] 再跑 `--check` 应全 PASS（S1~S6）")


if __name__ == "__main__":
    main()

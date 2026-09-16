#!/usr/bin/env bash
# 发布前扫描 —— GitHub 发布物**绝不允许**包含私有语料或隐私串。
#
# 用法：
#   tools/pre-publish-scan.sh <待发布目录>
#
# 判定：
#   ❌ 硬红线（必须 0 命中，命中即禁止发布）—— **规则来自唯一禁字清单** `tools/pre-publish-patterns.txt`
#      · 私有语料目录 / 快照：frozen-private* · local-corpus · runs · records.jsonl
#      · 本机路径：/Users/  C:\Users  F:\AgentWorkFlow
#      · 内网地址：192.168.
#      · 凭据：sk-… / ghp_… / github_pat_（前面带 `(^|[^A-Za-z0-9])` 防假阳性）
#      · 网盘：pan.baidu.com
#   ⚠️ 软提示（只告警，人工判断）：人名 / 昵称 / 用户名
#   📌 具名豁免（2026-09-16 方案① + 2026-09-17 B 方案）：
#      ① `knowledge.md` 的**技能标记行**（`<!-- skill: … | src: … -->`）—— `src:` 是归档定位，
#         **整行严格匹配该形态**才豁免（字段序/名/enum 任一不符 ⇒ 不豁免）；
#      ② **规则文件自身**（`tools/pre-publish-patterns.txt`）—— 它就是禁字清单的定义，必然含这些模式；
#      ③ 既有脱敏器/扫描器/测试里的**假密钥字面量**（ALLOW_RE）。
#      正文里的本机路径 / 内网地址 / 网盘地址**一律照旧硬红线**。
#
# 退出码：0 = 可发布；1 = 禁止发布；2 = 用法/规则文件缺失。
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PATTERNS="$HERE/pre-publish-patterns.txt"

# 唯一禁字清单（单一来源；缺了**不许静默降级**）
if [ ! -f "$PATTERNS" ]; then
  echo "[扫描] ❌ 缺禁字清单：$PATTERNS（单一来源，不可缺）" >&2
  exit 2
fi
PATH_RULES="$(awk -F'\t' '$1=="path"{print $2}' "$PATTERNS")"
HARD_RE="$(awk -F'\t' '$1=="content"{print $2}' "$PATTERNS" | paste -sd'|' -)"
SOFT_RE="$(awk -F'\t' '$1=="soft"{print $2}' "$PATTERNS" | paste -sd'|' -)"
if [ -z "$HARD_RE" ]; then
  echo "[扫描] ❌ 禁字清单里没有 content 规则：$PATTERNS（不许空规则通过）" >&2
  exit 2
fi

target="${1:-}"
if [ -z "$target" ] || [ ! -d "$target" ]; then
  echo "用法: $0 <待发布目录>" >&2
  exit 2
fi

fail=0

echo "== 硬红线 · 目录/文件 =="
while IFS= read -r bad; do
  [ -n "$bad" ] || continue
  hits="$(find "$target" -path '*/.git' -prune -o -name "*${bad}*" -print 2>/dev/null | head -5)"
  if [ -n "$hits" ]; then
    echo "[扫描] ❌ 命中：$bad"
    echo "$hits" | sed 's/^/    /'
    fail=1
  fi
done <<< "$PATH_RULES"

# 允许名单：这些文件**本身就是**脱敏器 / 扫描器 / **规则清单**，命中模式属于正常工作内容。
# （最后两个是单元测试里的**假密钥**字面量，故意写的，不是凭据。）
ALLOW_RE='benchmark/tools/build-public-corpus\.py|CorpusLibrary\.cs|tools/pre-publish-scan\.sh|tools/pre-publish-patterns\.txt|tools/build-public-pitfalls\.py|RuntimeConfigurationTests\.cs|OpenAICompatibleClientTests\.cs'

# 窄豁免（2026-09-16 主人按方案①定）：知识库 knowledge.md 的**技能标记行**里的 `src:` 字段是
# **归档定位**（指向本地归档目录），不含凭据、只有内网 SVN 需要 ⇒ 豁免**整行严格匹配该形态**的行。
# 严格性（fail-closed）：字段序/字段名/enum 值任一不符 ⇒ **不豁免**，照旧硬红线；
# 其它任何位置的本机路径（含正文里的真路径）**一律照旧命中**。
KNOWLEDGE_MARKER_RE='^[^:]+:[0-9]+:<!-- skill: [^|]* \| src: /Users/[^|]* \| ends_with_newline: (true|false) \| mode: (indexed|hybrid) -->$'

echo "== 硬红线 · 内容 =="
hard="$(grep -rInE "$HARD_RE" "$target" \
  --exclude-dir=.svn --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj 2>/dev/null | grep -vE "$ALLOW_RE" | grep -vE "$KNOWLEDGE_MARKER_RE" | head -20)"
if [ -n "$hard" ]; then
  echo "[扫描] ❌ 命中隐私串："
  echo "$hard" | sed 's/^/    /'
  fail=1
fi

echo "== 软提示 · 人名/用户名 =="
soft="$(grep -rInE "$SOFT_RE" "$target" \
  --exclude-dir=.svn --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj 2>/dev/null | grep -vE "$ALLOW_RE" | head -10)"
if [ -n "$soft" ]; then
  echo "[扫描] ⚠️ 以下位置出现人名/用户名（人工判断是否可公开）："
  echo "$soft" | sed 's/^/    /'
fi

if [ "$fail" -eq 0 ]; then
  echo "[扫描] ✅ 硬红线 0 命中，可发布"
else
  echo "[扫描] ❌ 禁止发布" >&2
fi
exit "$fail"

#!/usr/bin/env bash
# selftest-gates.sh —— 负测：证明三道闸门**真的会拦**（不是纸面结论）。
# 全部在 /tmp 的副本上做，绝不碰技能源与试点产物。
# 用法：tools/skill-repo/selftest-gates.sh [技能名] [密钥文件路径]
set -u
SKILL="${1:-image-compress}"
KEYFILE="${2:-$HOME/.agentruntime/api_key}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"          # 项目根
PILOT="$ROOT/skill-repo-pilot"
T="$(mktemp -d /tmp/skillrepo-neg.XXXXXX)"
pass=0; fail=0

run() {  # run <期望 PASS|FAIL> <说明> <命令...>
  local want="$1"; shift; local what="$1"; shift
  local out; out="$("$@" 2>&1)"; local code=$?
  local got=$([ $code -eq 0 ] && echo PASS || echo FAIL)
  if [ "$got" = "$want" ]; then pass=$((pass+1)); echo "  [OK  ] $what → ${got}（期望 ${want}）"
  else fail=$((fail+1)); echo "  [BAD ] $what → ${got}（期望 ${want}）"; echo "$out" | grep -E "FAIL" | head -3 | sed 's/^/         /'; fi
}

echo "临时工作区：$T"
cp -R "$PILOT" "$T/repo"
mkdir -p "$T/src"; cp -R "$HOME/.openclaw/workspace/skills/$SKILL" "$T/src/$SKILL"
CHK=(python3 "$ROOT/tools/skill-repo/build-skill-repo.py" --skills "$T/src" --out "$T/repo" --only "$SKILL" --check)

echo "① 基线（未动任何东西）"
run PASS "干净副本 --check" "${CHK[@]}"

echo "② 闸门②：手改产物"
echo "手改" >> "$T/repo/$SKILL/L2.md"
run FAIL "手改 L2.md 后 --check" "${CHK[@]}"
cp "$PILOT/$SKILL/L2.md" "$T/repo/$SKILL/L2.md"

echo "③ 闸门③：缓存被改 / 重算不一致"
printf ' ' >> "$T/repo/.cache/$(python3 -c "import json,sys;print(json.load(open('$PILOT/$SKILL/.meta.json'))['source']['sha256'])").json"
run FAIL "缓存被污染后 --check" "${CHK[@]}"
cp "$PILOT/.cache/"*.json "$T/repo/.cache/" 2>/dev/null

echo "④ 覆盖校验：删掉 L3 一条"
python3 - "$T/repo/$SKILL/L3.jsonl" <<'PY'
import sys
p=sys.argv[1]; ls=open(p,encoding='utf-8').read().splitlines(True)
open(p,'w',encoding='utf-8').writelines(ls[:1]+ls[2:])   # 删掉第 2 行
PY
run FAIL "删 L3 一条后 --check" "${CHK[@]}"
cp "$PILOT/$SKILL/L3.jsonl" "$T/repo/$SKILL/L3.jsonl"

echo "⑤ 闸门①：源变更但账本未 bump"
echo "- 外部改动" >> "$T/src/$SKILL/SKILL.md"
python3 - "$T/repo/$SKILL/.meta.json" "$T/src/$SKILL/SKILL.md" <<'PY'
import json,sys
p,src=sys.argv[1],sys.argv[2]; m=json.load(open(p,encoding='utf-8'))
m['source']['path']=src; json.dump(m,open(p,'w',encoding='utf-8'),ensure_ascii=False,indent=2)
PY
run FAIL "源变未 bump → --check" "${CHK[@]}"
run FAIL "源变未 bump → build（应被闸门①挡住）" python3 "$ROOT/tools/skill-repo/build-skill-repo.py" --skills "$T/src" --out "$T/repo" --only "$SKILL"

echo "⑥ bump 重抽 → 再 --check（真机 1 次调用）"
run PASS "build --bump 重抽" python3 "$ROOT/tools/skill-repo/build-skill-repo.py" --skills "$T/src" --out "$T/repo" --only "$SKILL" --api-key-file "$KEYFILE" --bump
run PASS "bump 后 --check" "${CHK[@]}"
run PASS "round-trip（改过的源）" python3 "$ROOT/tools/skill-repo/export-skill.py" --repo "$T/repo" --only "$SKILL" --out "$T/export"
run PASS "源未变 → 复用账本（0 次调用）" python3 "$ROOT/tools/skill-repo/build-skill-repo.py" --skills "$T/src" --out "$T/repo" --only "$SKILL"

echo
echo "负测结果：OK=$pass BAD=${fail}（副本：${T}）"
[ "$fail" -eq 0 ] || exit 1

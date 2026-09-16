#!/usr/bin/env bash
# 打包「冻结语料」给另一台机器（例如 001）手动搬运 —— 不依赖 SVN 的备用通道。
#
#   tools/pack-transfer.sh              # 打 A 档
#   tools/pack-transfer.sh --tier B     # 打 B 档
#   tools/pack-transfer.sh --tier A --out /tmp
#
# 产物：<out>/agentruntime-frozen-<tier>-<指纹8>.zip  + 同名 .sha256
# 包内容：语料目录 + 迁移器（生成器/版本/规格）+ 拉起配置 + 使用说明
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

tier="A"
out="$HERE/dist"
while [ $# -gt 0 ]; do
  case "$1" in
    --tier) tier="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

case "$tier" in
  A) corpus="frozen-private";   cfg="config.private.json" ;;
  B) corpus="frozen-private-b"; cfg="config.private-b.json" ;;
  *) echo "--tier 只能是 A 或 B" >&2; exit 2 ;;
esac

[ -d "$HERE/$corpus" ] || { echo "缺语料目录 ${corpus}（先生成：python3 tools/frozen-build/build-frozen-corpus.py --tier ${tier}）" >&2; exit 2; }

fp="$(python3 -c "import json,sys;print(json.load(open('$HERE/$corpus/_manifest.json'))['fingerprint'][:8])")"
mkdir -p "$out"
zip="$out/agentruntime-frozen-$tier-$fp.zip"
rm -f "$zip"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/agentruntime-frozen"
cp -R "$HERE/$corpus" "$work/agentruntime-frozen/$corpus"
cp -R "$HERE/tools/frozen-build" "$work/agentruntime-frozen/tools-frozen-build"
cp "$HERE/src/AgentRuntime.Cli/$cfg" "$work/agentruntime-frozen/"
cat > "$work/agentruntime-frozen/README-先读我.txt" <<EOF
AgentRuntime 冻结语料包（${tier} 档，全局指纹 ${fp}）

内容
  $corpus/                 冻结语料（区/层/域：rules / knowledge / memory-index）+ 账本 _manifest.json
  tools-frozen-build/      迁移器（确定性生成 + 版本闸门），可重跑
  $cfg                拉起配置（只含路径，不含密钥）

接收端怎么用
  1) 解到 AgentRuntime 项目根，例如：
       cp -R $corpus tools-frozen-build ../   # 放到与 src/ 同级
  2) 把你的 config 里 frozen.root 指向该目录（或直接用包里的配置改名）
  3) 跑：./run.sh --config src/AgentRuntime.Cli/$cfg --verbose "你好"

注意
  · 语料是**派生文件**，由 tools-frozen-build/build-frozen-corpus.py 确定性生成；不要手改。
  · 本包含私有内容（人名 / 本机路径 / 商业信息）——**绝不进 GitHub 或任何公开渠道**。
  · 版本号在 tools-frozen-build/versions.json；改源后先 --check，再 bump，再重跑。
EOF

( cd "$work" && zip -qr "$zip" agentruntime-frozen )
( cd "$out" && shasum -a 256 "$(basename "$zip")" > "$(basename "$zip").sha256" )

echo "[打包] $zip"
echo "[打包] $(du -h "$zip" | cut -f1) · $(cat "$out/$(basename "$zip").sha256")"

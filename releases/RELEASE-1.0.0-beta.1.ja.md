# Agent Runtime / WhiteBox · **1.0.0-beta.1**

> English: [RELEASE-1.0.0-beta.1.md](RELEASE-1.0.0-beta.1.md) · 中文: [RELEASE-1.0.0-beta.1.zh-CN.md](RELEASE-1.0.0-beta.1.zh-CN.md)

**最初の 1.x beta。** 製品バージョンは旧 0.9.x / Alpha の採番から **1.x** に移行します。
本次の狙いは一つ：**パッケージをダウンロードし、4 つの質問に答えるだけで WhiteBox が動くこと。**

## ダウンロードと実行

1. **`deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz`** をダウンロード（下記 Assets）。
2. 検証：`shasum -a 256 -c deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz.sha256`
3. 展開してウィザードを実行：

```bash
tar -xzf deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz
cd deploy-manager-1.0.0-beta.1
dotnet wcm.dll setup
./run.sh            # Windows: run.cmd
```

詳細：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.0.0-beta.1 の新機能

### 1) デプロイマネージャ（新規）

`wcm setup` はターミナルウィザードで、インストールを一度に完了します。

- **質問は 4 つだけ** — インストール先 → provider → 推論レベル → API キー；
- 既定は **DeepSeek 公式 `deepseek-v4-flash`**、推論レベル **low**。`high` は非推奨（遅く高価で効果は小さい）。**どのレベルでも思考過程は WhiteBox 自身が描画**します；
- ランタイム、凍結コーパス、知識シード 5 件、初回ガイド、プロセス発見台帳を配置；
- 知識インデックスを構築し、`doctor` を実行し、TUI 起動を確認；
- キーは**インストール先の中**（`state/api_key`、0600）だけ。ホームにも `config.xml` にも書きません；
- 自動化向け：`setup --yes --no-run --api-key-file <file>`。

### 2) プロセス発見が正式な知識ソースに

- 決定レポートは **`found knowledge`**（再利用可能な新知見）と **`found status`**（プロジェクトの現状）を区別し、**knowledge だけを永続化**します。
- 採用されたレポートから knowledge 項目を `workspace/docs/PROCESS-FINDINGS.md` に自動追記（レポート指紋で冪等）。
- 収尾で知識ツリーの **L4**（`L4-pfd-NNN`、連番）に移し、通常のループで L3 → L2 → L1 に育てます。
- プロトコル：**v106** — 26 行 / 22,269 文字 / **5,568 token**（予算 ≤ 5,600）。

### 3) TUI2 の入力改善

- **↑/↓ 履歴**：送信済みテキストを最大 100 件保持。↑ で古い方、↓ で新しい方、最新を越えると**下書きを復元**。複数行では通常のカーソル移動。
- **短い貼り付けはインライン**：**100 文字以下**は入力欄に直接挿入。それより長い場合だけ 1 つの貼り付けラベルにまとめます。

### 4) 収尾の信頼性修正

- 必須成果物が無い場合は空のプレースホルダを自動作成（最小デプロイでも収尾可能）。
- 明示的に命名されたソースの L4 id は**ファイル内の序数**を使い、`1..N` の連続性を保証。
- レポートの節見出しは完全に言語パック駆動（既定は英語）。見出し判定は**行全体の完全一致**（本文全体が見出し色になる不具合を修正）。
- リクエストファイルの指紋は**行頭**の `## 请求` から計算。
- `Why/Impact` の区切りは中国語ダッシュ `——` も許容。

## 検証（実測）

| 項目 | 値 |
|---|---|
| ユニットテスト | **1,368 / 1,368** |
| TUI2 `--selftest` | **PASS** |
| デプロイテストスイート **1.0.0**（D1–D8） | **PASS**（D8 は実 provider 1 往復） |
| パッケージスモーク | `setup --yes --no-run` → `doctor` 緑 → デプロイ版 TUI `--selftest` PASS |
| プロトコル | v106 · 5,568 / 5,600 token |

## 既知の制限

- **プラットフォーム: macOS (arm64) のみ。** 本リリースに **Windows パッケージはありません**。移植の複製・検証後に別途提供します。
- フレームワーク依存：**.NET 10 のインストールが必要**。
- `wcm rule-check` は Windows 側の `deploy-template.json` の差分をまだ報告します（修正は Windows 側で対応中）。
- 知識シード 5 件は意図的に最小です。L3/L2/L1 は最初の実収尾後に現れます。

## Assets

- `deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz` — 自己完結のデプロイマネージャパッケージ（ランタイム + 凍結コーパス + ワークスペース種 + ドキュメント）
- `deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz.sha256` — チェックサム

## 論文

本リリースで三言語版を更新：
[日本語](README.ja.md) · [English](README.md) · [中文](README.zh-CN.md)

# DEPLOY — WhiteBox 1.0.0-beta.1（日本語）

> 🌐 [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · **日本語**

ダウンロードから起動まで 5 分。これはデプロイ手順書であり、アーキテクチャ論文ではありません。

## 0. 必要なもの

| 項目 | 説明 |
|---|---|
| **.NET 10** | `dotnet --version` が `10.x`。入手: https://dotnet.microsoft.com/download/dotnet/10.0 |
| **プラットフォーム** | **本パッケージ: macOS (arm64)。** 本リリースに **Windows は含まれません**（移植検証後に別途提供）。 |
| macOS / Windows / Linux | .NET 10 アセンブリ自体はクロスプラットフォームですが、今回は macOS ビルドのみ |
| provider キー | 既定は **DeepSeek 公式**（`https://api.deepseek.com`、モデル `deepseek-v4-flash`） |
| ネットワーク | モデル呼び出しのみ。setup 自体はオフライン |

## 1. 展開

```bash
tar -xzf deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz
cd deploy-manager-1.0.0-beta.1
```

Windows は任意の解凍ツールで展開し、`cmd` / PowerShell で以下を実行（`/` は `\` に）。

## 2. セットアップウィザード

```bash
dotnet wcm.dll setup
```

聞かれるのは 4 点だけです。

1. **インストール先** — 既定 `~/WhiteBox`（Windows は `%USERPROFILE%\WhiteBox`）。
2. **Provider** — 1) DeepSeek 公式（推奨） / 2) カスタム OpenAI 互換（BaseUrl + モデル）。
3. **推論レベル** — 既定 **low**（速い・安い・十分）。`high` は非推奨。どちらでも WhiteBox 自身が思考過程を表示するため、思考が見えない問題は起きません。
4. **API キー** — マスク入力（エコーなし）。`<インストール先>/state/api_key` に `0600` で保存。

その後、ランタイム・凍結コーパス・知識シード 5 件・初回ガイド・プロセス発見台帳を配置し、知識インデックスを構築して `doctor` を実行します。

## 3. 起動

```bash
./run.sh            # macOS / Linux
run.cmd             # Windows
# または
dotnet wcm.dll run --target ~/WhiteBox
```

## 非対話（CI / 一括）

```bash
dotnet wcm.dll setup --target /opt/wb --yes --no-run \
  --api-key-file ~/.agentruntime/api_key
```

- `--from-config <file>`: 既存 WhiteBox の `config.xml` を再利用（BaseUrl / モデル / 推論レベル）。
- `--api-key-file <file>`: キーをファイルから読む — **コマンドラインにキーを書かない**。
- `--think low|medium|high`、`--build`（同梱バイナリが無い場合にソースからビルド）、`--force`（上書き）。

## ディレクトリ構成

```
<install>/
  config.xml                 provider / 推論レベル / パス
  app/tui|cli/               ランタイム
  tools/closeout/            収尾ツール + 宣言
  frozen/                    公開凍結コーパス
  whitebox/workspace/        クリーンなワークスペース（知識シード・初回ガイド・発見台帳）
  state/                     ストリーム / スナップショット / api_key（0600）
  run.sh / run.cmd
```

## セキュリティ

- キーは**インストール先の中**（`state/api_key`）だけ。ホームディレクトリにも `config.xml` にも書きません。
- setup はキーを表示しません。自動化では `--api-key-file` を使用。
- ローテーションは `setup` 再実行、または `state/api_key` を上書き。

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| `dotnet: command not found` | .NET 10 を入れ、ターミナルを開き直す |
| `知識ツリーの派生層が見つからない` | `dotnet wcm.dll setup` を再実行（索引を再構築） |
| `401 / unauthorized` | `state/api_key` を確認（1 行・引用符なし） |
| TUI の挙動が古い | **このインストール先**の TUI を起動しているか確認 |
| `doctor` が赤 | 不足項目が表示されるので直して再実行 |

## セルフテスト

```bash
dotnet wcm.dll selftest                           # コーパス自己テスト（17/17）
dotnet app/tui/AgentRuntime.Tui2.dll --selftest   # TUI ロジック自己テスト
```

## アンインストール

インストール先ディレクトリを削除するだけ。`state/` 内のキー以外は外部に書きません。

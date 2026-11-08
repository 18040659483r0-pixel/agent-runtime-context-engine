# DEPLOY — WhiteBox 1.1.0（日本語）

> 🌐 [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · **日本語**

ダウンロードから起動まで 5 分。これはデプロイ手順書であり、アーキテクチャ論文ではありません。

## 0. 必要なもの

| 項目 | 説明 |
|---|---|
| **.NET 10** | `dotnet --version` が `10.x`。入手: https://dotnet.microsoft.com/download/dotnet/10.0 |
| **プラットフォーム** | 2 パッケージ: **macOS arm64** と **Windows x64**。OS に合う方を利用（どちらも **.NET 10** が必要）。 |
| macOS / Windows / Linux | 各パッケージは自プラットフォーム向けにビルド・ラベル付け |
| provider キー | 既定は **DeepSeek 公式**（`https://api.deepseek.com`、モデル `deepseek-v4-flash`） |
| ネットワーク | モデル呼び出しのみ。setup 自体はオフライン |

## 1. 展開

```bash
tar -xzf deploy-manager-1.1.0-<platform>.tar.gz   # macos-arm64 / winx64
cd deploy-manager-1.1.0-<your-platform>   # 解凍後のディレクトリ
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
- `--dry-run` は計画だけ表示（ディレクトリ / provider / 推論レベル / キー元 / ペイロード）し、**書き込みません**。`--help` で全フラグ。
- 最終行は機械可読：`[setup] ok=1 target=…`。

## Provider・コンテキスト長・再設定

- **コンテキスト長**は provider ごとに設定。setup がモデル別の既定値を提示し、`--context-window <tokens>` で上書きできます。
  既定値は保守的：`deepseek-v4-flash` 1,000,000 / `gpt-5` 400,000 / `gpt-4.1` 1,000,000 / `claude-sonnet・opus-4` 200,000 / `gemini-2.5・3` 1,000,000 / `qwen3`・`kimi-k2` 262,144 / 不明 128,000。**provider の公式ドキュメントで確認**してください（上限ではなく既定値です）。
- **複数 provider**：`config.xml` に `Providers` リストと `ActiveProvider`。全件保存されますが、**使用されるのは active の 1 件だけ**です。
- **再インストールせず設定変更**：

```bash
wcm config --target ~/WhiteBox --list
wcm config --target ~/WhiteBox --provider-name qwen \
  --base-url https://dashscope.aliyuncs.com/compatible-mode/v1 \
  --model qwen3-max --context-window 262144 --think low --active
```

  `wcm config` は `config.xml` だけを編集し、workspace には触れません。`wcm setup --force` は完全再インストール（ディレクトリを消去）です。

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

## Windows 検証チェックリスト

本ツールに **Windows 専用コードはありません**（純粋な .NET API）。OS 分岐は 2 か所だけ（ファイル権限 `chmod` とランチャー `run.sh` / `run.cmd`）。Windows では次を確認してください。

- [ ] cmd / PowerShell で `dotnet wcm.dll setup` が動く。中国語/英語のプロンプトが文字化けしない（UTF-8 を強制）。
- [ ] 既定は `%USERPROFILE%\WhiteBox`。`run.cmd` が生成され TUI が起動する。
- [ ] キーは `<target>\state\api_key` に保存（ACL 継承、Windows では `chmod` をスキップ）。
- [ ] `dotnet wcm.dll doctor --target <dir>` の「平台」が Windows、「启动器」が `run.cmd` で緑。
- [ ] 既存ターゲットに読み取り専用ファイルがある場合、属性を解除してから削除する。
- [ ] AI 経路：`--yes --no-run --api-key-file` で完走し `[setup] ok=1` を出す。

> Windows **パッケージ**は Windows 上で（最低でも同一 commit から）ビルドし、`winx64` と明記してください。macOS 版を Windows で使わないこと。

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


## ダウンロード

- **ソース**：[https://github.com/18040659483r0-pixel/agent-runtime-context-engine](https://github.com/18040659483r0-pixel/agent-runtime-context-engine)
- **すべてのリリース**：[https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases)
- **macOS arm64**：[deploy-manager-1.1.0-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.0-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz.sha256)

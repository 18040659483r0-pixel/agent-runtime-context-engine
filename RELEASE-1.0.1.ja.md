# Agent Runtime / WhiteBox · **1.0.1**

> English: [RELEASE-1.0.1.md](RELEASE-1.0.1.md) · 中文: [RELEASE-1.0.1.zh-CN.md](RELEASE-1.0.1.zh-CN.md)

**2 つ目の 1.x beta。今回は Windows パッケージ付き。**

2 つのデプロイパッケージ（各々にデプロイマネージャ同梱）：

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.1-macos-arm64.tar.gz` | macOS arm64 | `28cb4ed68dfe7fdfea8d0c4ce195247e4e077da2557f4271a9e744f22526349b` |
| `deploy-manager-1.0.1-winx64.tar.gz` | Windows x64 | `378d9740599df9d988c1036c3760e5fbc871439211b7cc4e02e4baa382902a55` |

どちらもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.0.1-<platform>.tar.gz
cd deploy-manager-1.0.1-<platform>
dotnet wcm.dll setup
./run.sh        # Windows は run.cmd
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.0.0-beta.1 からの変更

### 1) Windows パッケージ + 堅牢化

- `win-x64` パッケージを追加（同じデプロイマネージャとランタイムを Windows 向けにビルド）。
- コンソールを UTF-8 に固定（cmd / PowerShell で文字化けしない）。
- 再帰削除の前に読み取り専用属性を解除。
- ホストが `ReadKey` 非対応なら、マスク入力は通常の行読みにフォールバック。
- `doctor` に**プラットフォーム**と**ランチャー**（`run.sh` / `run.cmd`）を追加。

### 2) 複数 provider（単一アクティブ）+ モデル別コンテキスト長

- `config.xml` に `Providers` リスト + `ActiveProvider`。使用されるのは active の 1 件だけ。旧来の単一 provider 設定はそのまま動作。
- setup がモデル別のコンテキスト長を提示し、`--context-window` で上書き可能。既定値は保守的で変更可（**provider の公式ドキュメントを確認**）。
- 新 `wcm config`：provider / 推論レベル / コンテキスト / キーを**再インストールせず**変更（workspace には触れない）。

### 3) 凍結コーパスのパスとデプロイ種

- 収尾は `config.xml` の `Frozen.Root` に従って `frozen/` を再構築/検証（以前はデプロイで誰も読まないディレクトリに書いていた）。
- デプロイ種に `MEMORY.md` / `MEMORYINDEX.md` / `DECISION-LIBRARY.md` を追加（初回の凍結構築が欠源で落ちない）。
- `setup` がインストール時に凍結コーパスを一度生成し、初回から自己整合。

### 4) 単一セッション設計：worker 退役

- worker / ディスパッチは退役。`worker.enabled=true` や明示的な `worker-dispatch` でも組み込まれない。
- CLI `worker` と `--worker-run` は「退役」と表示。
- master のみ。サブエージェントなし。

### 5) プロトコル v107 + セッション前置ロック

- 収尾は**要求されたとき**（またはランタイムが収尾ウィンドウを表示したとき）だけ実行。通常のタスク終了では、学んだことを生きたファイル（落とし穴 / 決定 / プロセス発見）に追記するだけ。
- 凍結前置の来源は**セッション内で 1 回バインド**。実行中の収尾/昇格は現在の prompt を変えない。`start`/`reset` で最新版に再バインド。

### 6) TUI2 入力

- **↑/↓ 履歴**：送信済みテキストを最大 100 件。最新より下へ行くと下書きを復元。
- **短い貼り付けはインライン**：100 文字以下は入力欄へ直接。それ以上は 1 つの貼り付けラベルに。

## パフォーマンス状況

**本 beta に専用の性能最適化は含まれていません。** 記載の数値はベースラインであり、チューニング結果ではありません。最適化は beta シリーズで順次行い、前後の数値を示します。

## 検証

| 項目 | 値 |
|---|---|
| ユニットテスト | **1,370 / 1,370** |
| TUI2 `--selftest` | **PASS** |
| デプロイマネージャ自己テスト | **17 / 17** |
| macOS パッケージ（新規ディレクトリ） | チェックサム OK → `setup` → `doctor` 緑 → デプロイ版 TUI `--selftest` PASS |
| Windows パッケージ | `win-x64` でビルド。初回は `DEPLOY` の Windows チェックリストで確認 |

## 既知の制限

- 両パッケージとも **.NET 10** が必要。
- Windows 初回は `DEPLOY` のチェックリスト（コンソール符号化 / キー経路 / `run.cmd`）を推奨。
- 新規デプロイでは知識/決定の L1 が空：最初の実収尾で L1 が育つまで、その 2 セクションはスキップ（報告のみ、ブロックしない）。

## 論文

[日本語](README.ja.md) · [English](README.md) · [中文](README.zh-CN.md)

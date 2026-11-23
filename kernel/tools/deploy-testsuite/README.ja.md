# デプロイテストスイート（deploy-testsuite）

> [English](README.md) | [中文](README.zh-CN.md) | 日本語

**何をテストするか**：実際の `deploy` 手順で**サンドボックス**に一度インストールし、**公式 DeepSeek** で 1 ターン
実走し、取得の重複排除・初期ライブラリの復元・ディレクトリ全体の移動を検証します。

**コーパス**：デプロイ版（`dist/alpha-0.1` の `templates/workspace` + `frozen`）。
**バイナリ**：最新ソース（`--refresh` が `dotnet publish` してパッケージの `app/` に入れる。既定で有効）。

## 実行

```bash
# 計画のみ
dotnet run --project tools/deploy-testsuite

# 実走（公式 DS を呼ぶ。ごく安価：D5 は 1 問だけ）
dotnet run --project tools/deploy-testsuite -- --run

# オプション
#   --package <dir>   パッケージ根（既定 dist/alpha-0.1）
#   --proj <dir>      ソースリポジトリ根（AgentRuntime.slnx から自動検出）
#   --out <dir>       証拠ディレクトリ（既定 .stage1/deploy-tests/<timestamp>）
#   --no-refresh      publish せず、パッケージ内の既存バイナリを使う
#   --keep            サンドボックスを残す（既定：pkg/moved を削除、report と target は残す）
```

## 判定（D1〜D8）

| # | シナリオ | ハード判定 |
|---|---|---|
| D1 | パッケージ完全性 | 必須 13 ファイルが揃う（バイナリ / テンプレート / 設定 / デプロイヤ / frozen / knowledge） |
| D2 | init サンドボックス | `deploy init` rc=0、対象に `config.xml` と自己完結 `app/` |
| D3 | provider = 公式 DS | `BaseUrl=https://api.deepseek.com` · `Model=deepseek-v4-flash` · 鍵は env/ファイルのみ |
| D4 | doctor + check | 両方 rc=0。知識ツリー 20 ノード、凍結コーパス版 82/2/33 |
| D5 | 実機 TUI | `knowledge.md` の最初の `<!--kn: L1-` を読む。出力に id と `[DONE]`。**rc=0・拒否 0** |
| D6 | 取得の重複排除 | 1 回目 `Kind=Knowledge`、2 回目は `kn-ref` のみ（`Kind=Hint`） |
| D7 | reset | 知識ファイルを汚した後の `reset --all` で初期 sha に戻り、`check` も緑 |
| D8 | ディレクトリ移動 | target を丸ごと別所へコピーしても `doctor` + `check` が緑（絶対パス依存なし） |

## 境界

- 課金があるのは **D5 のみ**（実モデル 1 回）。他はすべてオフラインコマンド。
- 鍵は成果物に一切残らない：`ApiKeyEnv` / `ApiKeyFile` の 2 項目だけを検証。
- 判定は `report.xml` の `ok="1"`。赤があれば exit 1（黙って通さない）。

## 既知の制限

- .NET 10 と有効な DeepSeek 鍵が必要（`~/.agentruntime/api_key` または `AGENTRUNTIME_API_KEY`）。
- D8 は**同一マシン内の移動**のみ。別マシンには .NET 10 が必要（`deploy doctor` が報告）。

# Agent Runtime / WhiteBox · **1.1.1**

> English: [RELEASE-1.1.1.md](RELEASE-1.1.1.md) · 中文: [RELEASE-1.1.1.zh-CN.md](RELEASE-1.1.1.zh-CN.md)

> ⬇️ **ソースとインストーラ**：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.1
> （本ページは説明のみ。開けない場合は末尾の直リンクを使用）

**1.x 系 5 番目のベータ。インストーラが「このフォルダーには既に WhiteBox がある」と認識し、TUI は即描画、初回の収尾も自己矛盾しません。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.1-macos-arm64.tar.gz` | macOS arm64 | `9a5222dc0af0e3c5e26b4190815a3298016b04b2aeefb70102a372d8926becf5` |
| `deploy-manager-1.1.1-winx64.tar.gz` | Windows x64 | `c7c65d7c5773c5044efd6a61fb3d109d39cbc7adcff2ed1bba9eb2110553c445` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.1-<platform>.tar.gz
cd deploy-manager-1.1.1-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows: run.cmd
# セットアップ後は  whitebox  と入力するだけでも起動
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.1.0 からの変更

### 1) 既存フォルダーへの再実行は「エラー終了」ではなく「選択」

```
[setup] found an existing WhiteBox deployment at ~/WhiteBox — choose:
  1) Overwrite: wipe that folder and install fresh
  2) Update: replace binaries only; keep workspace / provider / key
  3) Install to a different folder (keep the old one)
  4) Cancel
      choose [1/2/3/4] [2]:
```

- **更新**：`app/tui`、`app/cli`、`tools/closeout`（ランチャーと `whitebox` コマンドも）だけ差し替え。`whitebox/workspace`、`config.xml`（provider / モデル / 推論 / 言語 / キー経路）、`state/`、`frozen/` は**保持**。`doctor` に `deploy-manager: 1.1.1` を表示。
- **別フォルダー**：旧配置はそのまま残し、`whitebox` コマンドは常に最新の配置を指します。
- 非対話では `--force`（新規上書き）か `--update`（現地更新）を明示。

### 2) TUI は最初のフレームを即座に描画

Windows ではウィンドウ内をクリックするまで真っ白でした。入力ループに入る前に**最初のフレームを書く**ようにし、起動直後から表示されます（3 プラットフォーム共通、macOS の挙動は不変）。

### 3) 上部の詳細パネルは既定で表示

- 以前は Tab の後ろに隠れていましたが、**既定で表示**。Tab で従来どおり切替。
- 先頭行にヒント：
  `ⓘ AgentRuntime TUI2 - WhiteBox   （Tab でこの部分を隠す／もう一度 Tab で表示）`
  （英語・中文もそれぞれローカライズ）。

### 4) 初回収尾：最後のブロックも解消

新規配置で最初の実会話を行うと、ランタイムがプロセス発見を 1 件自動記録します。シード知識にはまだ L1 の鎖がないため `entry → L4 coverage` がブロックしていました。初回状態では緑で次を報告します：

`first run: N pending entries will be migrated by --apply (l4-sync); backlog N`

知識が育つと（version > 0 または L1 あり）厳格判定に戻ります。

実測（新規配置＋発見 1 件）：`block 赤 0（Core 0）· report 赤 6（既知の負債・非ブロック）`。

### 5) 小さな調整

- コーパス合計行の token を丸め（`≈ 793 tokens`）。
- API キー未設定のメッセージを英語に（既定は英語）。

### 6) 変わらない点

- プロトコルは **v107** のまま（プレフィックス変更なし＝キャッシュリセット不要）。
- 言語選択、その他の初回緩和、`whitebox` コマンドは 1.1.0 と同じ。
- 単体テスト **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS パッケージ D1–D7 PASS · Windows パッケージ D1+D9 PASS。

## 直接ダウンロード

リリースページが開けない場合は、以下の直リンクを使用してください：

- **macOS arm64**：[deploy-manager-1.1.1-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.1-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-winx64.tar.gz.sha256)

リリースページ（Assets は下部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.1

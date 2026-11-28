# Agent Runtime / WhiteBox · **1.2.0**

> English: [RELEASE-1.2.0.md](RELEASE-1.2.0.md) · 中文: [RELEASE-1.2.0.zh-CN.md](RELEASE-1.2.0.zh-CN.md)

> ⬇️ **ソースとダウンロード**：リリースページの下部 **Assets** にあります。
>
> - [macOS arm64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-macos-arm64.tar.gz.sha256)
> - [Windows x64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-winx64.tar.gz.sha256)

**すべての回答が「読める意思決定レポート」で締めくくられます。Windows でウィンドウをドラッグしても UI が固まりません。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.2.0-macos-arm64.tar.gz` | macOS arm64 | `9698075721c01cab458dfc9b00653385785b6488cb5c5f43844f2340fa225188` |
| `deploy-manager-1.2.0-winx64.tar.gz` | Windows x64 | `58e62c15b308fbcc5affed6f12ea579ad0c6375581d6c7d0a6c55a695a0b0267` |

> **⚠️ 以前の beta からアップグレードする場合**
> コーパス / 記憶（`whitebox/workspace`）を残すには **`--update`** でインストールしてください：
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> アップグレードで `--force`（上書き）を使わないでください。インストール先が丸ごと消えます。

## 1.1.9 → 1.2.0 の変更

### 1) すべての質問が意思決定レポートで締めくくられる

- **症状**：「そのパスを教えて」と一行だけ聞くと、モデルは `[RESULT]` 一行だけで停止。カードには意図/結果しか出ず、**原料**（読むべき原文）は書き換え・切り詰められ、開けるレポートもない。
- **原因**：レポート要求は「モデルが終局を宣言したとき」だけ。ツール呼び出しも `[DONE]` も無い返信は「次の一言待ち」の正常停止と見なされていた。
- **修正**：終局なしでチェーンが止まろうとしたら、ランタイムが一度だけ要求 —— *`[DONE]` / `[NO-SOLUTION]` / `[NEED-USER]` で締め、同じターンで `[REPORT]` を出す*。終局がある既存パスは不変。

### 2) ステータスバーは切り捨てず折り返す

- 狭い端末・長い文言でも「thinking … 00:42 ⠋⠙」が切れない。
- セグメント単位で詰め（`[状態] カウント` · summary · ヒント · アクティビティ）、表示幅で折り返し、最大 3 行（高さに応じて可変）。
- `Tab: modules` のヒントは削除、`Esc: 一時停止 · Esc 2回で終了` は残す。

### 3) Windows：ウィンドウのドラッグで UI が固まらない

- **原因**（winx64 実機）：コンソールは **リサイズ / フォーカス / メニュー / キーアップ** でも入力ハンドルをシグナル状態にするが、`ReadFile` はこれらの記録で**バイトを返さずブロック**する。⇒「ハンドルを待ってから ReadFile」のループはドラッグ中にデッドロックし、クリックするまで戻らない。
- **修正**：記録の種類を Peek し、キー押下／マウスだけを「読める」と判定。リサイズ等は **読み捨ててから**再待機（trace `drain type=N`）。メインループは固定テンポ（ビジー 33ms / アイドル 100ms）で、リサイズは約 100ms で再描画、アニメーションは入力に依存しない。
- POSIX 側は未変更（SIGWINCH）。

### 4) 診断：`--anim-probe`

`AgentRuntime.Tui2 --anim-probe 3` はアニメーションのフレームを出力し `frames / distinct / codes` を報告。「フレームが出ていない」のか「フォントに Braille 字形が無い」のかを切り分けられます。

## 出荷前の検証

- 単体テスト **1413 / 1413**（macOS）。TUI2 セルフテスト **PASS**。`wcm selftest` **17 / 17**。
- macOS パッケージ：D1–D7 + D9 **PASS**。Windows パッケージ：D1 + D9 **PASS**。
- `release-check`（ソースとのバイト比較）両方 **PASS**：mac `1e470179…`、win `b06fb57b…`。
- Windows 実機：ウィンドウをドラッグすると即座に再描画、`~/.agentruntime/last-tui2-input.txt` に `resize:` と `drain type=4` が出る。`--anim-probe` は複数の異なるフレームを報告。

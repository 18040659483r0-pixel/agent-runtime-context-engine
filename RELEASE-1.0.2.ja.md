# Agent Runtime / WhiteBox · **1.0.2**

> English: [RELEASE-1.0.2.md](RELEASE-1.0.2.md) · 中文: [RELEASE-1.0.2.zh-CN.md](RELEASE-1.0.2.zh-CN.md)

> ⬇️ **ソースとインストーラ**：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.0.2
> （本ページは説明のみ。開けない場合は末尾の直リンクを使用）

**1.x 系 3 番目のベータ。今度は Windows パッケージが本当に起動します。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.2-macos-arm64.tar.gz` | macOS arm64 | `4a358178365ecad7759e8f8fbc5a823daf1314158e49126e7ea359cfef69bbf7` |
| `deploy-manager-1.0.2-winx64.tar.gz` | Windows x64 | `23cebf57b610b572c5a1e3ab9243f1ea42585e75da3d14394263fdfa386f7639` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.0.2-<platform>.tar.gz
cd deploy-manager-1.0.2-<platform>
dotnet wcm.dll setup
./run.sh        # Windows: run.cmd
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.0.1 からの変更

### 1) 修正：Windows パッケージが起動直後に落ちる

- `run.cmd` → TUI2 が `DllNotFoundException: Unable to load DLL 'libc'` で即終了（ウィンドウが消える）。
- **原因**：`WINDOWS` シンボルを**ビルド元 OS** で決めていたため、macOS からの `win-x64` クロスビルドで POSIX の入力・画面サイズ・クリップボード経路が混入。
- **修正**：**ターゲット RID**（`win-x64` / `win-arm64`、または Windows ホスト）で決めるように変更。Windows 移植は本流に統合し、ソースは 1 本に。
- 同梱バイナリで確認：`GetConsoleScreenBufferInfo` / `SetConsoleMode` / `OpenClipboard` あり、`libc` / `termios` / `SIGWINCH` なし、`Tui2.exe` は PE32+ x86-64。

### 2) 終局行を言語パックへ（既定は英語）

- 画面は `[terminal] solved [DONE]: …`。`WB_TUI_LANG=zh` で `[終局] 得解[DONE]`、`ja` で日本語。
- 文言は言語パック、状態はプロトコル層が与えます。

### 3) 単なる確認のターンではレポートを出さない

- そのタスクで**ツールを一度も使っておらず**、決着済みで、**ランタイムがレポートを要求していない**場合、モデルが自発的に書いた `[REPORT]` は表示しません（ストリームには原文を保持）。
- 例：「実機で確認しました、OK です」→ カードは終局行で終わり、思考過程のような step / outcome を追加しません。

### 4) テスト

- 配置マネージャのスイートに **D9 クロスプラットフォーム記号ゲート**を追加：Windows パッケージは `libc` を含まず、コンソール API を含み、exe は PE32+ x86-64（macOS は逆方向に検査）。`--symbols` は D1+D9 のみ。
- 単体テスト **1373/1373** · TUI2 `--selftest` PASS · macOS パッケージ D1–D7 PASS · Windows パッケージ D1+D9 PASS。

### 5) 変わらない点

- プロトコルは **v107** のまま（プレフィックス変更なし＝キャッシュリセット不要）。
- 配置シード、複数プロバイダ、`wcm config`、凍結コーパスの挙動は 1.0.1 と同じ。

## 1.0.1 からのアップグレード（Windows）

1.0.1 の Windows 版は起動しません。1.0.2 をお使いください。新しい配置は `dotnet wcm.dll setup --target <dir>` で並べて作れます（既存ディレクトリは変更しません）。

## 直接ダウンロード

リリースページが開けない場合は、以下の直リンクを使用してください：

- **macOS arm64**：[deploy-manager-1.0.2-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.2/deploy-manager-1.0.2-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.0.2-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.2/deploy-manager-1.0.2-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.2/deploy-manager-1.0.2-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.2/deploy-manager-1.0.2-winx64.tar.gz.sha256)

リリースページ（Assets は下部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.0.2

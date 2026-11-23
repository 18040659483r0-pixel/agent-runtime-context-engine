# Agent Runtime / WhiteBox · **1.1.3**

> English: [RELEASE-1.1.3.md](RELEASE-1.1.3.md) · 中文: [RELEASE-1.1.3.zh-CN.md](RELEASE-1.1.3.zh-CN.md)

> ⬇️ **ソースとインストーラ**：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.3
> （本ページは説明のみ。開けない場合は末尾の直リンクを使用）

**1.x 系 7 番目のベータ。配置版が「実際に必要な作業ルール」を同梱し、意思決定は人が書く形のまま取り込まれます。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.3-macos-arm64.tar.gz` | macOS arm64 | `358f2a2c92a54cf9f1e6cae369005dc2179edf7106e58aa5ec6a24e21abbf764` |
| `deploy-manager-1.1.3-winx64.tar.gz` | Windows x64 | `39edd8c8fa7e92d29e2cb81bfefa9f1020b0ba74aa3670ae0c845f9c7006eef3` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.3-<platform>.tar.gz
cd deploy-manager-1.1.3-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows: run.cmd
# セットアップ後は  whitebox  と入力するだけでも起動
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.1.2 からの変更

### 1) 配置版 `AGENTS.md` が「作業ルール」を同梱

`AGENTS.md` は凍結 `rules/global.md` の源で、毎ターンのプレフィックスに入ります。これまでは歓迎ページだけでしたが、次を追加：

- **アーキテクチャ原則**：まず知識とツールを探す（Tool First）；四層知識（L1 法則 → L2 問題地図 → L3 処理単位 → L4 逐語証拠）；
  決定論的な作業はプログラム、判断はモデル；説明だけの質問は答えて止める；成否をパイプで判定しない（`| tail` は真の終了コードを隠す）。
- **収尾ルール**：固定パイプライン（`--check` → 本文 → `--apply` → `--promote` → `--close` → `--check`）；
  水位は `--close` だけが書く；絶対パスで書き、読み戻す；**収尾はユーザーが求めたときだけ**。
- **記憶**：日次・長期・索引・水位。
- **安全**：私有データを漏らさない、破壊的操作は先に確認、設定変更は現状確認、外部動作は先に確認、鍵は `state/api_key` のみ。
- **索引ポインタ**：知識、意思決定エントリ／ライブラリ、プロセス発見、用語、引き継ぎ、初回案内。

私有情報（リポジトリパス、分業、リリース手順、ローカルパス、内部コード名）は削除。**起動ルールは同梱しません**（開発側のもの）。

### 2) 意思決定エントリを実際の書き方で認識

次の 2 形式を受け付けます：

```md
### D-0001 一行タイトル
- **状態**：`已收`（受領）
- **推断取向**：次回どうするか
- **原話**：「ユーザーの言葉」
```

または従来の `- **[D-0001] タイトル**` 形式。`主题 / 判定 / 记录` のフィールドも可。

実バグも 2 つ修正：`### D-####` が**全く一致しなかった**（`##` のみ対応）こと、テンプレートの HTML コメント内の**書式例が実エントリとして解析されていた**こと（現在はコメントを除去してから解析、同一 id は最初の 1 件のみ）。育成器も「変更しながら挿入」をやめました（添字がずれて `## L4` を見出しごとエントリ化していました）。

### 3) 隔離環境での実測

新規 macOS 配置に、プロセス発見 1 件＋**意思決定 2 件**（`###` 形式と旧形式）を投入 → `--apply` → `--promote` → `--close` → `--check`：

```
block 赤 0（Core 0）· 意思決定 2/2 収束（L1/L2/L3/L4 各 2）· 知識 6/6/6/6
残り：アーカイブ宣言のみ（SVN 作業コピーでない環境要因）
```

単体テスト **1379/1379** · TUI2 selftest PASS · wcm 17/17 · macOS パッケージ D1–D7 · Windows パッケージ D1+D9。

### 4) 変わらない点

プロトコルは **v108** のまま。言語選択・初回緩和・`whitebox` コマンドは 1.1.2 と同じ。

## 直接ダウンロード

リリースページが開けない場合は、以下の直リンクを使用してください：

- **macOS arm64**：[deploy-manager-1.1.3-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.3-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-winx64.tar.gz.sha256)

リリースページ（Assets は下部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.3

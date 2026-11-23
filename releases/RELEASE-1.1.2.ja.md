# Agent Runtime / WhiteBox · **1.1.2**

> English: [RELEASE-1.1.2.md](RELEASE-1.1.2.md) · 中文: [RELEASE-1.1.2.zh-CN.md](RELEASE-1.1.2.zh-CN.md)

> ⬇️ **ソースとインストーラ**：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.2
> （本ページは説明のみ。開けない場合は末尾の直リンクを使用）

**1.x 系 6 番目のベータ。5 件のシードから知識の四層ツリーが自分で育ち、初回の収尾がついに最後まで通ります。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.2-macos-arm64.tar.gz` | macOS arm64 | `78c9b37ef01bdb9b4da567fc4d82145c2aa50276b1eea7beb6d9578a0ddbecae` |
| `deploy-manager-1.1.2-winx64.tar.gz` | Windows x64 | `f2448bce25715f2f9d25fbbc23786f66b66ad1729fb2e07e9caefb80e13e9ec6` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.2-<platform>.tar.gz
cd deploy-manager-1.1.2-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows: run.cmd
# セットアップ後は  whitebox  と入力するだけでも起動
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.1.1 からの変更

### 1) 四層ツリーが自分で育つ（手本不要）

配置には **L4 の証拠が 5 件だけ**。上の層（L3 → L2 → L1）はこれまで「空白から形式を発明する」ことを遠隔モデルに期待していましたが、ほぼ機能せず、鎖のゲートは赤のまま、初回収尾は壊れて見えました。意思決定ライブラリはさらに厳しく、**最初の 1 件でユーザーが止まる**状態でした。

現在は `closeout --apply` が決定論的な育成器を実行します：

- **知識**：各 L4 は短名ファミリー（`ver` / `pfd` / …）に属し、欠けた `L3 → L2 → L1` を生成してリンク（`<!--kn: … next=… -->` の実形式）。
- **意思決定**：`D-####` を持つ各 `L4-####` に `L1 → L2 → L3 → L4` の鎖を生成（ゲートが認める行形式）。
- 親ノードの本文は **「自動成長・抽象待ち」**。遠隔モデルがいつでも書き換え可能。**構造は決定論的、文言はそうではない**。冪等（欠けた層だけ補う）。

実測（新規配置＋発見 1 件＋意思決定 1 件）：知識 **L1 6 / L2 6 / L3 6 / L4 6**、意思決定 **1/1 収束**、鎖ゲート緑。

### 2) 初回の収尾が最後まで通る

配置ツリーだけで踏む 4 つの欠落を修正：

- `--apply` が CLI を見つけられない（ソース倉のパス）⇒ 配置版は `app/cli/…` を使用；
- 禁止語リストが無い ⇒ 公開用変種の工程をスキップ；
- `--promote` に知識台帳が無い ⇒ 自動で v0→v1 を作成；
- `MEMORYINDEX.md` にタイムライン節が無い ⇒ 追加；
- 移行した L4 本文の空行を除去 ⇒ 「未マーク本文」の誤検知を解消。

### 3) ヘッドファイルが初日から存在

配置パッケージに `SOUL.md` / `IDENTITY.md` と 6 つの `*-changeOrder.md` を同梱。初回収尾で欠けているものだけ生成（**中立テンプレート、人格は押し付けない**）し、`## 請求` 以降の本文から水位線フィンガープリントを再計算。`[open]` が残る間は水位を上げません（収束したふりをしない）。

配置版 `AGENTS.md`（凍結 `rules/global.md` の源）に**六ファイルの地図**を追加：それぞれ何か、いつ使うか、初回の答えをどこへ書くか。

### 4) プロトコル v108

- 第 15 条：6 つのヘッドファイルの目的と、初回 3 設定（呼び名・協力のしかた・仮想の役割）の落とし先を明記；
- 第 21/22 条：**「平易な言葉」は文言だけを縛る**。ヘッドファイルが定める人格・口調、および **R2（当該ターン）** の口調指示は**同時に成立**し、上書きされません；
- 予算 **5,600 → 6,200 token**（実測 23,577 文字 / 5,895 token / 26 行）。プロトコル変更のため次回起動時にキャッシュが一度リセットされます（正常）。

### 5) 検証

単体テスト **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS パッケージ D1–D7 PASS · Windows パッケージ D1+D9 PASS。隔離した新規配置：初回収尾 `block 赤 0（Core 0）`、知識 6/6/6/6、意思決定 1/1、残るは環境依存の report のみ。

## 直接ダウンロード

リリースページが開けない場合は、以下の直リンクを使用してください：

- **macOS arm64**：[deploy-manager-1.1.2-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.2-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-winx64.tar.gz.sha256)

リリースページ（Assets は下部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.2

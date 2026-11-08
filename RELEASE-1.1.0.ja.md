# Agent Runtime / WhiteBox · **1.1.0**

> English: [RELEASE-1.1.0.md](RELEASE-1.1.0.md) · 中文: [RELEASE-1.1.0.zh-CN.md](RELEASE-1.1.0.zh-CN.md)

> ⬇️ **ソースとインストーラ**：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.0
> （本ページは説明のみ。開けない場合は末尾の直リンクを使用）

**1.x 系 4 番目のベータ。言語は一度だけ選べば、セットアップも実行時も同じものを使います。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.0-macos-arm64.tar.gz` | macOS arm64 | `32a5c402da830befa605cf2a3a21fd2e2364f0e47575570de8e89f29d2692224` |
| `deploy-manager-1.1.0-winx64.tar.gz` | Windows x64 | `deb6eaef3c1259c069f0fe1f7d718a22bfa5270c760953e4b2ee26e15cc3f75a` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.0-<platform>.tar.gz
cd deploy-manager-1.1.0-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows: run.cmd
# セットアップ後は  whitebox  と入力するだけでも起動できます
```

手順書：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.0.2 からの変更

### 1) 言語は一度だけ — 全フローが追随

- `wcm setup` の**最初の画面が言語メニュー**：English / 中文 / 日本語。選ぶと以後のプロンプト・点検・完了表示が**即座に切り替わります**。
- 選択は配置の `config.xml`（`<UiLanguage>`）に書かれ、**TUI2 が起動時に継承**：インストーラから実行時（初回案内・キー表示・状態行）まで再選択は不要。
- `--lang en|zh|ja`（または `WB_LANG`）で質問を省略可、`wcm config --lang` で後から変更、`WB_TUI_LANG` は実行時上書き。
- **既定は英語**：配置マネージャ、実行時 CLI のセットアップ出力、終局行（`[terminal] solved [DONE]: …`）。

### 2) 初回起動が「壊れて見える」問題を解消

新規配置の `closeout --check` は、まだ存在しないものを理由に失敗していました。現在は空・シード状態を**理由付きで緑**にします：

- 意思決定ライブラリ 0 件 → 収束 / ツリー鎖 / 版管理の 3 項をスキップ緑；
- 知識がシードのみ（L1 未生成）→ ツリー鎖と成長の 2 項をスキップ緑；
- 日記未作成・水位に収束点なし → 初回は緑。

実測（新規インストール）：`block 赤 0 件（Core 0）· report 赤 6（既知の負債・非ブロック）`。

### 3) `whitebox` コマンド（両プラットフォーム）

- セットアップ後、端末で **`whitebox`** と入力すれば**その配置**が起動します。
- POSIX：`/usr/local/bin/whitebox`（書き込み不可なら `~/.local/bin/whitebox`）。
- Windows：`%USERPROFILE%\bin\whitebox.cmd`。
- 冪等；`doctor` に `whitebox command` を追加；`PATH` に無い場合は追記用の行を表示。

### 4) 初回案内は「3 つの設定」から

ようこそ案内は順に尋ねます：**呼び名**（→ IDENTITY）、**協力のしかた**（→ USER）、**仮想の役割**（→ rules）。落とし穴は実作業の中で貯まるもので、初日に要求しません。`ONBOARDING.md` と一致。

### 5) 内部

- 配置マネージャの表示文言はすべて言語パック化；実行時 CLI のセットアップ出力もローカライズ。
- UI コードに残る中国語リテラルを数えるテストゲートを追加（**減少のみ許可**、`ui-cjk-baseline.txt`）。
- 単体テスト **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS パッケージ D1–D7 PASS · Windows パッケージ D1+D9 PASS。
- プロトコルは **v107** のまま（プレフィックス変更なし＝キャッシュリセット不要）。

## 1.0.1 以前からアップグレード（Windows）

1.0.1 の Windows 版は起動しません。1.0.2 と 1.1.0 の両方に修正が入っています（`WINDOWS` シンボルをターゲット RID で判定）。1.1.0 はさらに言語選択と初回修正を含みます。

## 直接ダウンロード

リリースページが開けない場合は、以下の直リンクを使用してください：

- **macOS arm64**：[deploy-manager-1.1.0-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.0-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz)
- **チェックサム**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz.sha256)

リリースページ（Assets は下部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.0

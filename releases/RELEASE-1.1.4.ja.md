# Agent Runtime / WhiteBox · **1.1.4**

> English: [RELEASE-1.1.4.md](RELEASE-1.1.4.md) · 中文: [RELEASE-1.1.4.zh-CN.md](RELEASE-1.1.4.zh-CN.md)

> ⬇️ **ソースとインストーラ**：**英語リリースページ下部の Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.4
> 本ページは説明のみです。開けない場合は末尾の直リンクを使ってください。

**8 番目の 1.x beta。CLI は英語が既定、決定レポートは端末での読み順に再構成、アップグレードで語料を黙って失わない。**

> **⚠️ v1.1.3 beta（以降の beta）からデプロイした方へ：**
> 既存の語料・記憶（`whitebox/workspace`）を引き継ぐには、新バージョンを**必ず `--update`** で入れてください。
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> **`--force`／「1) 上書き」でアップグレードしないでください** — インストール先を丸ごと消去します。
> 本版は `--force` の前に旧 workspace を `<インストール先>-backup-<タイムスタンプ>.zip` へ自動バックアップし、
> 失敗した場合は消去を拒否します。それでも**自分でもバックアップ**してください。

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.4-macos-arm64.tar.gz` | macOS arm64 | `60f5826db89b92c642027441c52d3b0412cc5fbf56b3aa1a810c95dfbbf3ca54` |
| `deploy-manager-1.1.4-winx64.tar.gz` | Windows x64 | `18f9536a4575981017e1e1545f050afc3e540919dbf005c759995d5d7baa6fee` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.4-<platform>.tar.gz
cd deploy-manager-1.1.4-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows: run.cmd
# セットアップ後は  whitebox  でも起動できます
```

ガイド：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.1.3 からの変更

### 1）CLI 文言を言語パック化（既定は英語）

メイン CLI、`tools/closeout`、benchmark、TUI2 の help／診断行が同じ言語表を使うようになりました。

- 既定は**英語**。`--lang zh|ja` で切替。`WB_CLI_LANG` / `WB_TUI_LANG` / `config.xml` の `UiLanguage` も認識
  （明示引数 > 環境変数 > 設定 > 英語）。
- 未訳は中国語の原文にフォールバック（空欄にならない）。新しい文言に訳がないとゲートが赤になります。

### 2）決定レポートを TUI2 の読み方に合わせて再構成

TUI2 は常に最新が下に来るため、「先に読むもの」を上に置きました。

- **用語**と**原資料**を上部にまとめ、**番号なし**（原資料は「第 4 章」ではなくなりました）。
- 番号は `1 need · 2.x ステップ · 3 結果 · 4.x 発見 · 5.x 次に · 6 成果物`。
- `next:` の各行は**具体的な行動**（誰が何をし、選ぶと何が変わるか）が必須。`rec` は印だけであり内容ではありません。
  空の `next [rec]:` は破棄され、「1. （おすすめ）」だけの行は出ません。
- プロトコル領域は **v110**：26 行 / 24,586 文字 / 6,147 token（予算 ≤6,200）。

### 3）知識・決定・記憶のマーカーをカードで強調

- **知識 / 決定**（`知識ツリー / 知識ベース / 決定ライブラリ / ノード id L1~L4-xxx-nnn` など）は**意図色 + 太字**。
- **記憶**（`記憶 / 記憶索引 / 当日の記憶 / MEMORY.md / memory / 日記`）は**結果色 + 太字**。
- ANSI 装飾のみを追加し、プレーンテキストはバイト単位で不変です。

### 4）アップグレードで語料を黙って失わない

- クローズアウト宣言を 2 箇所に分離：`tools/closeout/requirements/` = **システム内蔵**（`--update` で置換）、
  `tools/closeout/requirements-user/` = **ユーザー拡張**（保持。同じ id は内蔵を上書き）。
- `--update` は旧 `requirements/` の「変更した / 追加した」宣言を `requirements-user/` に移してから内蔵を置換します。
- `--force`／上書きで旧 `whitebox/workspace` を検出すると、タイムスタンプ付き zip（workspace + `config.xml` + `requirements-user`）へ
  自動バックアップし、失敗時は消去を中止します。
- `--update` は `whitebox/workspace`、`config.xml`、`state/api_key`、`frozen/` を変更しません。

## 検証

- 単体テスト **1388 / 1388**
- TUI2 セルフテスト **PASS**（新しい強調表示の断言を含む）
- デプロイマネージャ **17 / 17**
- パッケージゲート：macOS **D1–D7 PASS**／Windows **D1 + D9 PASS**（win パッケージに `libc` なし）
- 分離アップグレード実測：v1.1.3 → v1.1.4 `--update`。ユーザー宣言は移行後も有効、workspace / config / key はバイト一致
- 分離 force 実測：消去前に zip を生成し、workspace マーカー・`config.xml`・`requirements-user` を含むことを確認

## 注意

- Windows では更新前に WhiteBox/TUI2 を完全に終了してください（バイナリがロックされます）。
- 同一ディレクトリへの `--update` は `whitebox` ランチャーをそのディレクトリへ書き換えます（想定動作）。
- バージョンは **1.1.4（beta）** のまま。`deployment-template/` と `frozen/` は 1.1.3 と同一のため、workspace 移行は不要です。

## 直接ダウンロード

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-winx64.tar.gz
- チェックサム：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-winx64.tar.gz.sha256
- リリースページ：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.4

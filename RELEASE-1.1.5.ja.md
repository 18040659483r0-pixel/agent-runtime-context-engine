# Agent Runtime / WhiteBox · **1.1.5**

> English: [RELEASE-1.1.5.md](RELEASE-1.1.5.md) · 中文: [RELEASE-1.1.5.zh-CN.md](RELEASE-1.1.5.zh-CN.md)

> ⬇️ **ソースとインストーラ**：**英語リリースページ下部の Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.5

**9 番目の 1.x beta。毎ターンの性能内訳を自動記録し、同一ツールの繰り返しを止め、リポジトリは C# + XML のみ。**

> **⚠️ v1.1.3 beta 以降から更新する場合：** 既存の語料（`whitebox/workspace`）を残すには **`--update`** を使ってください。
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> `--force` はインストール先を消去します（本版も旧 workspace を zip に自動バックアップし、失敗時は消去を拒否しますが、自分でもバックアップを）。

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.5-macos-arm64.tar.gz` | macOS arm64 | `79ec199cfd9573bef89a62fd5d29c863e6ac9751f8dcd00c6ebde32bad887be2` |
| `deploy-manager-1.1.5-winx64.tar.gz` | Windows x64 | `b91f63794c01f70a04aaac5c75d6bf1fc5363d2f7352bc0f18a91ba8527ad20f` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 1.1.4 からの変更

1. **毎ターンの性能記録**：`ProviderMs / ToolMs / EngineMs / AllocBytes` + token / メッセージ数を `<stream>.usage.xml` に自動記録。旧台帳は欠損を 0 として読込。
2. **繰り返し呼び出しガード**：同一ツール + 同一引数で同一結果が 3 回連続 ⇒ `tool-repeat-guard` ヒント。
3. **プロトコル v111**：小さな変更は 1 回だけ再ビルド、影響する手順だけ再実行。検収は最後に 1 回。失敗した手順だけ個別に再実行。26 行 / 24,768 文字 / 6,192 token。
4. **クローズアウト通知は非強制**：60/70% は計画の促しのみ、80% は `[收尾建议]` で続行可。
5. **Python / JSON ゼロ**：`deploy-template.xml` へ移行。benchmark 結果も XML。フレームワーク必須の `*.deps.json` / `*.runtimeconfig.json` のみ例外。
6. **知識/決定/記憶の強調**：知識・決定（裸の `L1~L4` を含む）は意図色 + 太字、記憶は結果色 + 太字。中国語・英語・日本語の表記を認識。

## 検証

- 単体テスト **1395 / 1395**
- TUI2 セルフテスト **PASS**
- デプロイマネージャ **17 / 17**
- パッケージ：macOS **D1–D7 PASS** / Windows **D1 + D9 PASS**
- v1.1.4 → v1.1.5 の `--update` 検証：workspace / config / key / ユーザー宣言を保持、`deploy-template.xml` を同梱

## 直接ダウンロード

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz
- チェックサム：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz.sha256
- リリースページ：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.5

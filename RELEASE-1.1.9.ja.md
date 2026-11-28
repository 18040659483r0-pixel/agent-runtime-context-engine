# Agent Runtime / WhiteBox · **1.1.9**

> English: [RELEASE-1.1.9.md](RELEASE-1.1.9.md) · 中文: [RELEASE-1.1.9.zh-CN.md](RELEASE-1.1.9.zh-CN.md)

> ⬇️ **ソースとダウンロード**：リリースページの下部 **Assets** にあります。
>
> - [macOS arm64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-macos-arm64.tar.gz.sha256)
> - [Windows x64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-winx64.tar.gz.sha256)

**緊急修正：モデルが正しく書いたツール呼び出しが、痕跡なく消えることはもうありません。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.9-macos-arm64.tar.gz` | macOS arm64 | `f9c8e2c2134de501d74854fa3b12759443a54d284b9a52c3f3c9d3e02a968ede` |
| `deploy-manager-1.1.9-winx64.tar.gz` | Windows x64 | `cd4c2529e073f7764abf84909431b3143df1e77c6ec216b826e29a3828043f08` |

> **⚠️ 以前の beta からアップグレードする場合**
> コーパス / 記憶（`whitebox/workspace`）を残すには **`--update`** でインストールしてください：
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> アップグレードで `--force`（上書き）を使わないでください。インストール先が丸ごと消えます。

## 1.1.8 → 1.1.9 の変更

### 1) 修正：ツールブロックが無言で捨てられることがあった

- **症状**（winx64 実機）：`write` を 41 回呼び、結果が返ったのは 8 回。残り 33 回は**イベントが一切なし**（結果・拒否・承認のどれもなし）。モデルは「write が壊れた」と判断して迂回した。
- **原因**：ブロックヘッダの走査が、**コードフェンス / 引用 / 4 スペースインデント**の中の行を「引用例」として丸ごと飛ばしていた。そのブロックしかない返信では解析結果が「何もなし」になり、モデルにも何も見えない。（行形式そのものは常に有効な入力だった：`key: value` / `key<< … >>`。）
- **修正**：これらのブロックはもう黙って捨てない。
  - 包まれたブロックだけ ⇒ **返信全体を拒否**し、原因と直し方（「`[TOOL]` は行頭に単独で」）を明示；
  - 本物のブロックと混在 ⇒ 本物は実行し、`tool-fence` ヒントでスキップ数を報告；
  - ヘッダが本文に貼り付いている場合（`了解、書きます：[TOOL] write …`）も同様に拒否。
- **安全性は不変**：フェンス / 引用 / インデント内のブロックは**決して実行しない**。

### 2) プロトコル v121：形の規約を契約に明記

第 5 条に明記：`[TOOL]` ヘッダは行頭に単独で置くこと。コードフェンス・引用・4 スペースインデント内のブロックは**例**として扱い、実行しない。また、値に `<` / `&` を含む場合の**ゼロエスケープ行形式**を合法な形として復活（従来の「行形式は廃止」という記述はツール面の説明と矛盾していたため解消）。プロトコル規模：27 行 / 28,384 文字 / ≈7,096 token（予算 ≤7,200）。

## 出荷前の検証

- 単体テスト **1413 / 1413**（macOS）。TUI2 セルフテスト **PASS**。`wcm selftest` **17 / 17**。
- Windows 側の再検証：解析 6 ケース **6/6**、実機 E2E 1 ターン（行形式 `write` + XML 本文 → ファイルがバイト単位で一致）。
- macOS パッケージ：D1–D7 + D9 **PASS**。Windows パッケージ：D1 + D9 **PASS**。
- `release-check`（ソースとのバイト比較）両方 **PASS**：mac `db64116f…`、win `93135d11…`。

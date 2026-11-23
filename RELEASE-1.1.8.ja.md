# Agent Runtime / WhiteBox · **1.1.8**

> English: [RELEASE-1.1.8.md](RELEASE-1.1.8.md) · 中文: [RELEASE-1.1.8.zh-CN.md](RELEASE-1.1.8.zh-CN.md)

> ⬇️ **ソースとダウンロード**：リリースページの下部 **Assets** にあります。
>
> - [macOS arm64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-macos-arm64.tar.gz.sha256)
> - [Windows x64 パッケージ](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-winx64.tar.gz.sha256)

**12 番目の 1.x beta。既定でトゥルーカラー、パスのハイライトを控えめに、インストール後の自己診断が誤報しなくなりました。**

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.8-macos-arm64.tar.gz` | macOS arm64 | `c96e177c30dd1d2220a049e2592c043f520d04437f23f5d3c26c6b417e973b45` |
| `deploy-manager-1.1.8-winx64.tar.gz` | Windows x64 | `8d2a51841c817ec2e9b924ca00fbf6952805eee2f3b37e305e91f7dad69180f3` |

> **⚠️ 以前の beta からアップグレードする場合**
> コーパス / 記憶（`whitebox/workspace`）を残すには **`--update`** でインストールしてください：
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> アップグレードで `--force`（上書き）を使わないでください。インストール先が丸ごと消えます。事前にバックアップを。

## 1.1.7 → 1.1.8 の変更

### 1) 既定がトゥルーカラーに（金色が「ミルク色」に見えた原因）

- **症状**：トゥルーカラー対応の端末でアクセントの金色が淡く、パスの色がほぼ見えない。
- **原因**：色深度の判定が「`COLORTERM` が無い＝ 24bit 非対応」と解釈していた。Windows Terminal などでは `TERM` も `COLORTERM` も空なので、16 色に静かに落ち、金色とパスが同じ色コードになっていた。
- **修正**：まず**ホスト自身**、次に環境変数で判定：
  1. `--color-depth` 指定 → それを使う；
  2. `NO_COLOR` / 非 TTY / `TERM=dumb` → 色なし；
  3. Windows Terminal（`WT_SESSION`）→ トゥルーカラー；
  4. macOS → トゥルーカラー；
  5. Windows → トゥルーカラー（実行時に VT を有効化しており、16 色が出ている時点で VT は有効）；
  6. `COLORTERM` が truecolor/24bit（`TERM` も同様）→ トゥルーカラー；
  7. `COLORTERM` あり、または `TERM` に `256color` → 256 色；
  8. それ以外 → 16 色。
- **結果**：金色は `#F6C453`、パスは `#F0C987` に戻ります。`--color-depth 256` / `16` でいつでも降格可能。

### 2) 数字をパスとして扱わない

- **症状**：`モジュール | 8/12 | …` の `8/12` がファイルパスとして着色される。
- **原因**：自動注釈が任意の `x/y` 形状を受け入れていた。
- **修正**：相対パスは ASCII 英字を 1 文字以上含むことを条件に。`8/12`・`4/12`・`1/2`・`3/8` は無着色。実際のパス（`docs/PITFALLS.md` など）と `./a/b` は従来どおり。

### 3) `wcm setup` の誤った失敗報告を修正

- グローバルコマンドが「明示的な `--register-launcher` のときだけ」になって以降も、健康チェックは未登録を失敗として数え、正常なインストールが `ok=0` で終わっていた。
- 現在は情報行（`ℹ️ 未登録`）になり、`ok=1` で終わります。

### 4) リリース衛生

- 同梱のベンチマークコーパスに残っていた非公開プロジェクト名を中立的な表現に置換。コーパスロックを再計算し、サイズ予算内を維持。
- 内部：リリースゲートは、パッケージ内の `AgentRuntime.Tui2.dll` が同一ソースの `dotnet publish` と**バイト単位で一致**することを両プラットフォームで要求します。

## 出荷前の検証

- 単体テスト **1406 / 1406**（macOS）。TUI2 セルフテスト **PASS**。`wcm selftest` **17 / 17**。
- macOS パッケージ：D1–D7 + D9 **PASS**。Windows パッケージ：D1 + D9 **PASS**（Windows 分岐、`libc` なし）。
- `release-check`（ソースとのバイト比較）両方 **PASS**：mac `d3c31882…`、win `c59bb991…`。

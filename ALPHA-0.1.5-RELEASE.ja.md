# Agent Runtime · v9.0（Alpha 0.1.5）· **リリース候補（Release Candidate）**

> English: [`ALPHA-0.1.5-RELEASE.md`](ALPHA-0.1.5-RELEASE.md) · 中文: [`ALPHA-0.1.5-RELEASE.zh-CN.md`](ALPHA-0.1.5-RELEASE.zh-CN.md) · テストレポート: [`ALPHA-0.1.5-TEST-REPORT.ja.md`](ALPHA-0.1.5-TEST-REPORT.ja.md)

**状態：ほぼ正式版として使えます。** 欠陥密度は十分に低く、ランタイムの画面は一つだけになり、
それが実機で連日動いています。UI は**3 言語対応（既定は英語）**です。
本版が `pre-release` のままなのは二点だけ：**スループット／遅延の体系的な最適化が未実施**であること、
そして対外インターフェースがカーネルの版とともに微調整され得ることです。

プロトコル領域：**v102**（25 行 / 20,933 文字 / 約 5,234 token、予算 ≤5,600）。

## 本版の変更

### 1) TUI1 を退役、TUI2 が唯一の画面に（**破壊的変更**）

- `kernel/src/AgentRuntime.Tui/**` プロジェクト全体を削除。専用だったものも一緒に廃止：
  `tools/demo-doc`、`docs/TUI-DEMO-SAMPLE.md`、`wb-cli` の `verify-demo` / `demo` / `ctrl-c-e2e`。
- まだ価値のある部分は**正しい層へ移動**し、置き換えられた画面と一緒に消えません：

| 旧 TUI1 | 現在 |
|---|---|
| `PanelRouter` · `AblationService` | `src/AgentRuntime.Hosting/Commands/`（TUI2 と CLI が同じコマンド経路を共有） |
| `CrashDump`（未処理例外の診断記録） | `src/AgentRuntime.Hosting/Diagnostics/`、**TUI2 の致命的例外でも書き出し**（`last-crash.txt` + `crash-log.txt`） |

- 起動：`whitebox`（PATH コマンド）で直接 TUI2。旧フラグ `--tui1` / `--legacy` / `--tui-old` は除去。
- **ロールバック先ではなくなりました**：戻す場合は前回のリリースタグ（`v8.1`）を使用してください。

### 2) UI 言語パック（強型キー · 3 言語 · **既定は英語**）

- キーは **`enum TuiKey`（89 個）**：綴り間違いはコンパイルエラー（「訳漏れ／キー間違い」を排除）。
- 言語：`WB_TUI_LANG=en|zh|ja`、**既定 `en`**。モデルと話す言語とは無関係です。
- 対象：トップバンド（セッション／モジュール三区分／スタック／ホワイトボード／下書き／フォーカス）、
  ステータス表示、選択・コピーのヒント、`[session]` / `[start]` / `[paste]` / `[resume]` / `[interrupt]` / `[error]` の接頭辞、
  カードのツール動詞、起動時の告知（セッション番号／ストリーム／末態／前の巻／worker）、意思決定レポートのラベル。
- ゲート：全キー × 3 言語が非空であること。英語選択時に**画面と起動告知に漢字が一文字でも出たら赤**。

### 3) 知識ノード id：生成側で正規化、解析側で fail-closed

- 生成：`--l4-sync` は短名を正規化（`[a-z0-9]` のみ、16 文字以内）し、連番を 3 桁にゼロ埋めしたうえで、
  **書き込み前に自己検査。不正なら例外で書かない**。
- 解析：ノードマーカーで始まるのに正規形に一致しない行は**行番号付きでエラー**にします。
  従来はノード全体を黙って飛ばしていました（444 個の L4 のうち 428 個しか読み込まれず、
  一見「next リストの末尾が落ちた」ように見えていました）。

### 4) モジュール行、クラッシュ証拠、セルフテスト緑化

- トップバンドの `Modules` 行は**そのターンに実際に寄与したバイト数**で表示：
  `(Cached)` コンテキストに組み込まれた／`(Active)` 活版から必要時に取得／`(Runtime)` それ以外。
- TUI2 `--selftest` が赤から緑に（10 件のアサーションが中国語ラベルを直書きしており、
  既定英語では必ず不一致になっていました）。

## 検証（スライス内で実施。ビルドディレクトリでは行わない）

- `dotnet build AgentRuntime.slnx -c Release`：**エラー 0**。
- `dotnet test tests/AgentRuntime.Tests -c Release`：**1347 / 1349**。2 件は**内部専用**
  （`PitfallsDerivedTests` は内部の落とし穴コーパスの派生物が必要で、公開スライスには含まれません）。
- 公開前スキャン：**ハード違反 0**（ソフトは敬称・人名のみで、人が判断）。
- TUI2 `--selftest`：**PASS**。

## 境界

プライベート作業領域・内部の落とし穴コーパス・学習データは公開スライスに含まれません。
コーパスのライセンス：`kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`。

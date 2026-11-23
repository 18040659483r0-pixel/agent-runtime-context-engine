# Agent Runtime

> [English](README.md) | [中文](README.zh-CN.md) | 日本語

> **バージョン 0.1.0（カーネル凍結、2026-09-14）** —— 凍結基準・凍結項目・実測証拠は `docs/FREEZE-0.1.0.md`。
> 変更履歴：`CHANGELOG.md`

> **ゼロから自作した Agent Runtime / Context Engine カーネル**（第 II 論文の実装物）。
> 現在の段階：**V0 最小ループ + V1 モジュールパイプライン**。

## V0 がやることは四つだけ

```
User → Runtime → Model API → Response → Runtime → User
```

1. 一文を受け取る
2. 最小のリクエストを組み立てる
3. OpenAI 互換 API を呼ぶ
4. モデルの結果をそのまま返す

**やらないこと**（明示的に除外）：Knowledge / Memory / Router / Worker / キャッシュ最適化 / データベース /
永続化 / Web / UI / DI コンテナ。これらは下のロードマップで層ごとに追加し、各層を独立にテストします。

## モジュールパイプライン（ホットスワップ可能。ベアチャットは常に利用可）

```
ユーザー入力
   │
   ├─ モジュール 1（設定順）→ メッセージを寄与（system / 履歴 / 記憶 / 知識 …）
   ├─ モジュール 2
   │
   └─ エンジンが「今回のユーザーメッセージ」を追加（常に最後）
        │
        └─ IModelClient → 上流 API → 応答
   └─ 各モジュールが Observe（例：会話履歴を書き込む）
```

**三つのハード制約**（テストで固定）：

1. **ベアチャットは常に存在**：`--bare`（または `modules: []`）では V0 と**バイト同一**。
2. **モジュールは追加のみ**：エンジン動作を書き換えない。どれを外しても他は**バイト同一**（アブレーションの前提）。
3. **スイッチは設定だけ**：`config.xml` の `modules` 配列。順序＝プロンプトへの寄与順。`--modules a,b` で上書き。

| モジュール | 役割 |
|---|---|
| `session` | マルチターン：過去ターンをプロンプトへ、今回を履歴へ（V1、メモリ内） |
| `append-stream` | **セッションイベントストリーム**（V3）：追記専用（JSONL 永続化）、プロセス終了を越えて**再開**、L3（スキル／落とし穴）を**1 件ずつ**読み込む家 |
| `system-rules` | 先頭の鉄則／人格：固定 system テキストを注入（最小のホットスワップ例） |
| `rules` | **鉄則区**（V2）：Global → Expert[選択ドメイン] → Project |
| `knowledge` | **知識区**（V2）：同じ三層 |
| `memory-index` | **記憶索引区**（V2）：単層（Global）、全文ではなく索引 |
| `focus` | **セマンティックフォーカス**（V4.1、R3）： `[FOCUS] E208 E216` の一行。履歴は変えない。`append-stream` の**後**（第三ゲート） |
| `current-tail` | **現在の末尾**（V4.2、R4）：`[TAIL]` のホワイトボードを**ローカルに永続化**。`append-stream` の後、`dynamic-draft` の前 |
| `dynamic-draft` | **動的ドラフト区**（V4.3、R5）：検討中の構想／未確定要件の**唯一の家**。**全体上書きのみ**。`current-tail` の後、`focus` の前 |
| `tool` | **ツール面**（V7）：`[TOOL]` → 解析 → **承認ゲート** → 実行 → 結果は**イベント**。読み取りは無承認、他は承認、判定不能は拒否（fail-closed）、モデルは自己承認不可 |
| `protocol` | **プロトコル区**（R1-P）：遠隔 AI とローカル Runtime の最小契約。凍結区の**先頭**、ドメインで絞られず、**クローズで変更不可**、**ユーザーが外せない** |

新しいモジュールで触るのは二箇所だけ：`AgentRuntime.Modules/ModuleRegistry.cs` + `RuntimeConfiguration.KnownModules`。

### 凍結区（V2）—— 区 / 層 / ドメインで安定接頭辞を組織する

```
① rules.global     → rules.expert[ドメイン id 順]     → rules.project      ← 絶対トップ（制約）
② knowledge.global → knowledge.expert[ドメイン id 順] → knowledge.project  ← 並列の内容層
③ memoryindex.global                                                      ← 並列の内容層
```

- **内容の出所**：`config.frozen.root`（相対パスは**設定ファイルのディレクトリ**基準）。`rules/global.md`、
  `rules/expert/<domain>.md`、`knowledge/global.md`、`memory/index.md` など。版はヘッダ
  `<!-- frozen: version=N -->`（**プロンプトには入らない**）。**ファイルが無い＝その区が無い**。
- **二つのコーパス**：製品用 `frozen/`（クリーン、ソースと一緒に配布）と、実測用
  `benchmark/AgentRuntime.Benchmark/corpus/`（匿名化済み、公開可）。
- **ドメインは起動前に決める**（`--domains a,b`、空＝全部）。関係ない知識をプロンプトから外せます。
- **決定性ゲート**：凍結区は**丸ごと前置**。動的モジュールを前に置くとキャッシュが全滅（実測：ヒット 0、成本 4.7×）。
- **全体指紋＋版台帳**：`FrozenPrefix.Assemble` が接頭辞全体のバイト指紋を出し、`FrozenManifest` が「どの区が何版か」を
  記録。指紋が変われば**どの区が変わったか**を diff できます（台帳はプロンプトに入らない）。
- **クローズと水位線**：`--closeout` が接頭辞を検証 → 未整理の misc を報告 → 水位線を書く（Runtime 層）。

## セッションイベントストリーム（V3）—— 追記専用・再開可能・L3 を 1 件ずつ

```
ユーザー入力 → イベントストリーム（追記専用） → 各イベントに連番（1,2,3,…）、型はタグのみ
```

- **API が制約**：`SessionAppendStream` は `Append` のみ公開。読み取りは `IReadOnlyList`。イベントは get-only record。
  削除 / 挿入 / 並べ替え / 上書きの入口は**無い**（テストで固定）。
- **順序が身元**：連番は 1..N の厳密連続。`ValidateInvariants()`（組立時）と `SessionStreamStore.Load()`（再生時）の
  二つのゲート。壊れたファイルは**大声で失敗**（静かに持病を持ち越さない）。
- **永続化＝JSONL 追記のみ**：1 行 1 イベント。中国語はエスケープせず、列挙は名前で書く（人が読め、diff できる）。
  **タイムスタンプ無し**（本文と台帳の分離）。
- **プロセスを越えた再開**：終了しても失われず、次回**ファイルから再生**（実測：第 2 ラウンド
  `cached 29,312 / 29,542 = 99.2%`。イベントは**末尾にしか**追記されないため）。
- **L3 を 1 件ずつ読み込む**：`--append <file>` で 1 文書を流に追加し、`append-stream` モジュールでプロンプトに入ります。

- ⚠️ `session` と `append-stream` は**排他**（どちらも会話履歴の家。同時に付けると重複）。
- ⚠️ イベントストリームは**常に凍結区の後**（決定性ゲート）。
- ⚠️ `focus` は**すべての動的区の後**（第三ゲート。前に置くと**修正せずエラー**）。
- ⚠️ `current-tail` → `dynamic-draft` → `focus` の順序は固定（規範順 `R2 → R4 → R5 → R3`）。
- ⚠️ `tool` は**注入ゼロだが位置を占める**（R2 類）。イベントの落とし口が必須（`append-stream` 無しは組立エラー）。

## 技術ベースライン（2026-09-14 固定）

| 項目 | 値 |
|---|---|
| Language | C# 14 |
| Framework | .NET 10（LTS、2028-11-14 まで） |
| Target | `net10.0` |
| プラットフォーム | macOS / Windows / Linux（同一 Core） |
| 入口 | Console / CLI（V0 は UI も Web も無し） |
| HTTP | `System.Net.Http.HttpClient` |
| JSON | `System.Text.Json` |
| テスト | xUnit **v3**（`xunit.v3` 3.2.2） |
| Benchmark | **独立ディレクトリ／プロジェクト** |

## ディレクトリ構成

```
AgentRuntime/
├── AgentRuntime.slnx              # ソリューション（.slnx）
├── (launcher moved into tools/wb-cli)
├── src/
│   ├── AgentRuntime.Models/       # 純データ：ChatRequest/ChatResponse/Message/TokenUsage
│   ├── AgentRuntime.Core/         # カーネル：IModelClient / IRuntimeModule / エンジン / 計時 / 設定
│   ├── AgentRuntime.Modules/      # 差し替え可能なモジュール（DLL を消してもベアチャットは動く）
│   ├── AgentRuntime.Providers/    # HTTP と OpenAI 互換プロトコルを知る唯一の層
│   └── AgentRuntime.Cli/          # 合成ルート：config.xml + Program.cs
├── tests/AgentRuntime.Tests/      # xUnit v3（完全オフライン、ネットワーク無し）
├── benchmark/                     # 物差し（被測定物から独立）
└── tools/mock-provider/           # オフライン mock provider（開発用、Runtime の構成物ではない）
```

### 依存方向（境界は人が決め、循環は禁止）

```
        Cli ──→ Modules ──→ Core ──→ Models
         │        │          ↑          ↑
         └────────┴──────────┴──────────┘
                     Tests ──→ (all)

Benchmark ──→ Runtime      (Runtime never references Benchmark)
```

- **Models**：依存ゼロ、DTO と `[JsonPropertyName]` のみ。
- **Core**：Models のみに依存。DeepInfra / DeepSeek / OpenAI が何かを**知らない**。
- **Providers**：HTTP とワイヤ形式に依存する唯一の層。
- **Cli**：具体実装を `new` する**唯一**の場所（合成ルート）。

## 実行（新しい端末で三ステップ）

```bash
dotnet --version        # 10.0.x を期待

cd ~/Documents/AgentWorkFlow/software-company/projects/AgentRuntime

wb-cli run "こんにちは"                                  # 一言 → 一言（実モデル）
wb-cli run --chat ""                                    # マルチターン
wb-cli run --bare --verbose "こんにちは"                 # ベアチャット（アブレーションの対照群）
wb-cli run --modules system-rules,session --verbose "こんにちは"
wb-cli run --verbose "こんにちは"                        # 診断：token / キャッシュ / 遅延 / Runtime 开销

dotnet build AgentRuntime.slnx
dotnet test  AgentRuntime.slnx                          # xUnit v3（完全オフライン）
```

## 検証済み（2026-09-14、実端点）

- **単一ターン**：prompt 36 · completion 234 · total 270 · cached 0。
- **セッション・アブレーション**（同じ 3 ターン台本）：+Session は 2 ターン目で名前を**正答**、ベアは**不正解**。
  ⇒ Session が買うのは「能力」、代価は毎ターンの prompt 増。
- **上流接頭辞キャッシュ**：同一 1,810 トークン接頭辞の 2 回目で `cache_hit 1664 / miss 146`（92%、1664 = 64×26）。
- **凍結コンテキスト階段**：L1（1,000）で 89% ヒット・約 67% 節約、L2（9,974）で **98.4%** ヒット・**78.5%** 節約。
  「毎ターン変わる」内容を先頭に置くと **0%**（**4.7×**）。55 回の実呼び出しで Runtime 开销は最大 1.3 ms / 平均 0.1 ms。

## 鍵の扱い

- `config.xml` は**鍵を書かず**、「どこから取るか」だけを書く：`apiKeyEnv`（環境変数、優先）または `apiKeyFile`（`~` 可）。
- 鍵は**ログ・例外・`--verbose` 出力に一切出ない**。
- `apiKeyFile` が指すファイルは**SVN にコミットしない**。

## ロードマップ（各層は独立にテスト可能）

| 版 | 内容 |
|---|---|
| **V0** | 一文 → モデル → 一文 ✅ |
| **V1** | Session（マルチターン、差し替え可能）✅ |
| **V2** | Frozen Context（安定接頭辞／キャッシュ境界）✅ |
| **V3** | Append Stream（追記専用イベントストリーム）✅ |
| **V4** | Snapshot（決定論的復元）✅ |
| **V4.1** | Semantic Focus ✅ |
| **V4.2** | Current Tail（R4）✅ |
| **V4.3** | Dynamic Draft（R5）✅ |
| V5 | Knowledge / Memory |
| V6 | Router（マルチモデル） |
| V7 | Worker（Context Funnel） |

## 設計原則

1. **境界は人が決め、コードは AI が書き、結論は Benchmark が検証する。** 依存は一方向、循環禁止。
2. **物差しは被測定物から独立**（`benchmark/` は Core に入らない）。
3. **データ安全**：鍵は保存しない、ログに出さない。例外は診断可能だが漏洩しない。
4. **差し替え可能性**：provider もモデルも交換可能。Core はベンダーを知らない。
5. **早すぎる最適化をしない**：V0 は数百行。DI コンテナも企業向け Web 階層も無し。

## ライセンス（MIT）

- **カーネル**（`src/AgentRuntime.Core` · `Modules` · `Providers` · `Hosting` · `Cli`）と **TUI**（`src/AgentRuntime.Tui2`）は
  **MIT** —— リポジトリ根の `LICENSE` を参照（著作権と許諾表示を保持）。
- **オープンソース対象外**：`frozen-private*`（私有コーパス）· 社内知識ベース · 実験記録（`benchmark/runs/`）·
  社内エンジニアリング文書。公開前に必ず `wb-cli scan`（私有コーパス / ホストパス / イントラネット / 認証情報 /
  クラウドドライブ = **ハードレッドライン**）。

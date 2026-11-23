# frozen-build — 決定論的コーパス移行ツール

> [English](README.md) | [中文](README.zh-CN.md) | 日本語

**openclaw ワークスペース**の価値ある内容を、AgentRuntime の**区 / 層 / ドメイン**形式で**凍結コーパス**にまとめます
（出力は既定で `../../frozen-private/`。私有：**社内 SVN にはコミット、GitHub には絶対公開しない**）。

## ワンライナー

```bash
cd projects/AgentRuntime
CLI=src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll   # 先に dotnet build -c Release

dotnet $CLI --frozen-corpus whitebox/workspace --frozen-tier A                                 # A 段
dotnet $CLI --frozen-corpus whitebox/workspace --frozen-out frozen-private-b --frozen-tier B  # B 段（A＋プロジェクト索引）
dotnet $CLI --frozen-corpus-check whitebox/workspace                                          # 冪等検査（CI 用）
dotnet $CLI --bump all                                                                        # bump 宣言：版 +1 ＋ 源アンカー更新 ＋ 再生成
dotnet $CLI --frozen-spec                                                                     # セクション一覧（唯一の宣言箇所）
```

## 五つの鉄則（METHODOLOGY §10.7〜10 と一致）

1. **唯一の宣言箇所**：セクション一覧は `src/AgentRuntime.Cli/FrozenCorpusCli.cs` の `Spec`（`--frozen-spec` で表示）。
   他で構造を再定義してはならない。
2. **バイト決定性**：LF・BOM なし・タイムスタンプなし・索引は id（NFC）順 —— 再実行で同一バイト。
3. **本文と台帳の分離**：版ヘッダ `<!-- frozen: version=N -->` と `_manifest.json` は台帳専用で**プロンプトに入らない**。
   指紋はヘッダを**除いた**本文で計算。
4. **版ゲートは二段**：① 成果物 ≠ 再計算 ⇒「源が変わったのに再実行していない」；② **源アンカー** ——
   `versions.json` の `source_sha256` が最後の bump 時の本文 sha を固定するため、内容が変われば `--bump` が必須
   （**再実行だけでは緑にならない**）。⇒ **内容変更＝版変更**。唯一の操作は `--bump <key>|all`。
5. **L3 は索引だけ**：スキル / 落とし穴 / 引き継ぎ / プロジェクト文書 / ツールの本文は凍結区に入れず、索引のみ。
   本文は後で Append Stream に**1 件ずつ**入れる（V3 依存）。

## 段

| 段 | 内容 | 用途 |
|---|---|---|
| **A** | 鉄則＋方法論＋索引＋記憶索引 | 日常 |
| **B** | A **に**「プロジェクト文書索引」を追加 | プロジェクト全体像が必要なとき |

> A → B は**末尾以外**への追加（B の索引は知識区の末尾）。**純粋な追記ではない**ため切替にキャッシュ代价あり。
> 各段はそれ自体が完全な接頭辞。

## 第 2 領域：他端の記憶は凍結区に入れない

- 第 1 領域（`rules` / `knowledge` / `memory-index`）は**本端のみ**。他端の個人記憶（例 `workspace/agents/001/`）は
  **SPEC にも凍結区にも入らない**。
- 必要なら**第 2 領域**として索引だけを持ち、本文は**1 件ずつ `--append`** でストリームへ（Append Stream, V3）。
  第 1 領域とは物理分離 ⇒ 他端の変更が**本端の接頭辞バイトを動かさず**、bump も再実行も不要。

## 成果物（派生・手編集禁止）と入库

`frozen-private/`（A）、`frozen-private-b/`（B）——**本ツールが生成、手編集禁止**。内容を変えるには**源**を変え、
`versions.json` を更新し、再実行します。

| 行き先 | 口径 |
|---|---|
| **SVN（社内 / 家庭 LAN）** | ✅ **コミット**（主人 2026-09-14：社内サーバー、漏洩リスクなし） |
| **GitHub リリース** | ❌ **絶対不可**（人名 / ホストパス / 商業情報を含む） |

公開前に**必ず**公開スキャンを実行（ハードレッドライン：私有コーパス / ホストパス / イントラネット / 認証情報 / クラウドドライブ）：

```bash
cd projects/AgentRuntime
wb-cli scan <ステージング先>     # exit 0 = 公開可、1 = 公開禁止
```

## 源を変えたときの標準手順（順序を守る）

```bash
CLI=src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll
# 1) まず検査：源が変わって再実行していない → 必ず失敗
dotnet $CLI --frozen-corpus-check whitebox/workspace
# 2) 失敗したら versions.json の該当 version_key を bump
dotnet $CLI --bump knowledge.global
# 3) 再生成
dotnet $CLI --frozen-corpus whitebox/workspace --frozen-tier A
dotnet $CLI --frozen-corpus whitebox/workspace --frozen-out frozen-private-b --frozen-tier B
# 4) もう一度検査（合格必須）
dotnet $CLI --frozen-corpus-check whitebox/workspace
dotnet $CLI --frozen-corpus-check whitebox/workspace --frozen-out frozen-private-b --frozen-tier B
```

⚠️ **先に再実行してから検査しない**：再実行は `_manifest.json` を上書きし、「源が変わったのに bump していない」
警告を消してしまいます（実測で踏みました）。
⚠️ 源は自動化で変わり得るため、`--check` は入場儀式として扱う。

## なぜ移行ツールか（手組みではなく）

- 手組みコーパスは**毎回違う** → 全体指紋が変わる → 接頭辞キャッシュが全滅（実測：ヒット 0、成本 4.6×）。
- 源は自然に変わる（方法論の圧縮、記憶索引化、索引統合）→ 「決定論的再生成＋版ゲート」だけが**どの区が変わったか**を言える。

## ワンショット起動

```bash
cd projects/AgentRuntime
wb-cli run --config src/AgentRuntime.Cli/config.private.xml --verbose "こんにちは"     # A 段
wb-cli run --config src/AgentRuntime.Cli/config.private-b.xml --verbose "こんにちは"   # B 段
```

実測（2026-09-14、A 段 / 実端点）：1 回目 `prompt 24,569 · cached 0`（コールドスタート自己検査）；
2 回目 `cached 24,320 = 64×380（98.99%）· uncached 248`；Runtime オーバーヘッド 1.1 / 1.3 ms。

## ⚠️ 段運用の二つの規律（2026-09-16 実測）

1. **`--out` は必ず明示**：`--tier B` は出力先を変えない（既定は A 段のディレクトリ）⇒ `--out` なしだと
   **A 段を B 段の内容で上書き**します。
2. **源が変わったら：`--check`（失敗するはず）→ 本当に変わった区だけ bump（先に再計算）→ 両段を再生成 → `--check`（緑）**。

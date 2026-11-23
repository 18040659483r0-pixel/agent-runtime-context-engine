# Agent Runtime / WhiteBox · **1.1.6**

> English: [RELEASE-1.1.6.md](RELEASE-1.1.6.md) · 中文: [RELEASE-1.1.6.zh-CN.md](RELEASE-1.1.6.zh-CN.md)

> ⬇️ ソースとインストーラ：英語リリースページ下部の **Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.6
> このページは説明のみ。開けない場合は末尾の直リンクを使用してください。

**10 番目の 1.x ベータ。知識の書き込みは「約束」ではなく、ランタイムが**実測**してカードに表示する事実になりました。**

> **⚠️ 以前のベータからのアップグレード**
> コーパス / 記憶（`whitebox/workspace`）を残すには **`--update`** で導入してください：
>
> ```bash
> dotnet wcm.dll setup --target <インストール先> --update --yes
> ```
>
> アップグレードで `--force` /「上書き」は使わないでください（インストール先を丸ごと消します）。
> 本版も強制導入前に旧 workspace を `<インストール先>-backup-<時刻>.zip` へ自動バックアップし、
> 失敗時は消去を拒否しますが、**念のためご自身でもバックアップを**。

| アセット | プラットフォーム | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.6-macos-arm64.tar.gz` | macOS arm64 | `76104f57f8f2dae5c58a7c332fa91c53a2ad1c69353fda66ed7e9961d114b79d` |
| `deploy-manager-1.1.6-winx64.tar.gz` | Windows x64 | `1ab395d3f12e4dbb736274c48df5f78cf3afe9725a580dc8e28548ba27374341` |

いずれもフレームワーク依存：**.NET 10** が必要です。

## 実行

```bash
tar -xzf deploy-manager-1.1.6-<platform>.tar.gz
cd deploy-manager-1.1.6-<platform>
dotnet wcm.dll setup      # 最初の画面で言語を選択
./run.sh                  # Windows は run.cmd
# セットアップ後は  whitebox  だけで起動できます
```

ガイド：[DEPLOY.ja.md](DEPLOY.ja.md) · [English](DEPLOY.md) · [中文](DEPLOY.zh-CN.md)

## 1.1.5 からの変更

### 1）知識の書き込みを実測して表示

ツールが成功するたび、ランタイムは 3 つの生きたファイルの**行数差**を測ります —
`docs/PITFALLS.md` / `docs/DECISIONS.md` / `docs/PROCESS-FINDINGS.md` — そして `[LEDGERWRITE]` イベントを出します。
カードの結果行に `＋坑集 +9` / `＋决策 +2` として表示されます。

`exec` + heredoc の追記も対象です（旧マーカーは write/edit の受領だけを見ており、それが「見えない」原因でした）。
**マーカーが無い＝本当に書いていない**。 「後で追加」は仕事の完了になりません。

### 2）プロトコル v113：同じターンで記録し、報告は項目を指す

- 第 7 条： pitfall やユーザーの決定は、学んだ**同じターン**でプロジェクトの生きたファイルへ書く。
  「TODO として保留」は納品ではない。何も無ければ `no new entries` と書く。
- 第 12 条： 生きたファイルに書いた項目の `found` 行は、その項目のアンカーで終える
  （`-> pitfall #70` / `-> D-0047`）。報告が約束ではなく項目を指す。
- 予算：6,200 → **6,900 token**（実測 6,325。場所を作るために既存条文を削っていません）。

### 3）テストスイートが `whitebox` コマンドを書き換えなくなりました

展開マネージャ付属のテストスイートは実際の `setup` を実行し、マシン全体の `whitebox` ランチャーを
一時サンドボックスへ向けていました。サンドボックス削除後にコマンドは静かに死にます。
現在は**隔離した HOME** で setup を実行し、実ランチャーをスナップショット/復元します
（`/usr/local/bin/whitebox`、`~/.local/bin/whitebox`、`%USERPROFILE%\\bin\\whitebox.cmd`）。
判定：1 回の実行前後でランチャーの sha256 が一致。

### 4）修正と整備

- 1.1.5 再出時のプロトコル第 19 条修正は維持（クローズアウト / リセット / スタート：
  人からの依頼、**または** ≥80% のウィンドウ行）。
- 落とし穴ログ：#400（プロトコル二条の矛盾）、#401（派生索引の再計算漏れ）、
  #402（テストが全体ランチャーを上書き）、#403（`exec`+heredoc を見ない知識マーカー）。
- リポジトリ衛生は不変：Python なし、JSON ソースなし。ルールは `deploy-template.xml`。

## 検証

- 単体テスト：**1399 / 1399**
- TUI2 セルフテスト：**PASS**
- 展開マネージャ：**17 / 17**
- パッケージ闸門：macOS **D1–D7 PASS**、Windows **D1 + D9 PASS**（`libc` なし）
- 独立 Windows E2E：**T1–T6 = 10 / 10 PASS**
- 隔離アップグレード：`--update` 後も `workspace` / `config.xml` / キー / ユーザー宣言はバイト単位で不変

## 注意

- Windows では更新前に WhiteBox / TUI2 を終了してください（バイナリがロックされます）。
- その場での `--update` は `whitebox` ランチャーをそのインストール先へ向け直します（想定動作）。
- バージョン **1.1.6（beta）**、プロトコル **v113**。

## 直接ダウンロード

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-winx64.tar.gz
- チェックサム：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-winx64.tar.gz.sha256
- リリースページ：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.6

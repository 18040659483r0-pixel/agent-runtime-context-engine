# Workspace コーパスマネージャ（Wcm · macOS / POSIX）

> [English](README.md) | [中文](README.zh-CN.md) | 日本語

Windows 版と**同一インターフェース・同一ルールファイル**（`deploy-template.json` はバイト一致、`rule-check` で機械比較）。
「デプロイ用コーパスの生成 / 現行コーパスのバックアップ / 復元」を再利用可能なコマンドにし、常にクリーンで統一された
WB ワークスペースを一発で用意します。

## 三つの機能

| コマンド | 内容 |
|---|---|
| `wcm new --target <ws> [--template <dir>] [--reset]` | **デプロイ用コーパス生成**：`deployment-template/` の導入コーパス（AGENTS / BULLETIN / GLOSSARY / USER / ONBOARDING）をコピーし、`knowledge / docs / memory / handoff` を作成。`--reset` は対象を先に空にする |
| `wcm backup --workspace <ws> --backup <dir>` | **現行コーパスをバックアップ**（1 フォルダ）。`state/` `.svn/` `bin/` `obj/` `.git/`、`.tui2.lock`、`deepseek apikey.txt` を除外 |
| `wcm restore --from <dir> --to <ws>` | **復元**（マージ。対象に既にあるファイルは削除しない） |

自己検査コマンド：

- `wcm selftest` —— サンドボックスで「生成 → 蓄積模擬 → バックアップ → 破壊 → 復元 → リセット」を実行し、各項目 PASS/FAIL；
- `wcm rule-check [--windows <json>]` —— Windows 版 `deploy-template.json` とバイト比較（ルールの分岐禁止）。

## 使い方

```bash
cd whitebox/tools/workspace-corpus-manager
dotnet build -c Release

# 1) クリーンなデプロイ用ワークスペース（新規ユーザーの初回）
dotnet bin/Release/net10.0/wcm.dll new --target /tmp/ws-new --reset

# 2) 現行 WB コーパスのバックアップ
dotnet bin/Release/net10.0/wcm.dll backup \
  --workspace ../../whitebox/workspace --backup ~/Backups/wb-corpus-2026-10-31

# 3) 復元
dotnet bin/Release/net10.0/wcm.dll restore --from ~/Backups/wb-corpus-2026-10-31 --to /tmp/ws-restored

# 4) 自己検査 / ルール整合
dotnet bin/Release/net10.0/wcm.dll selftest
dotnet bin/Release/net10.0/wcm.dll rule-check
```

## Windows 版との違い（実装手段のみ）

| 項目 | Windows | macOS |
|---|---|---|
| コピー | `robocopy /E /XD /XF` | `Directory.EnumerateFileSystemEntries` + `File.Copy`（同じ除外リスト） |
| リセット | `Remove-Item -Recurse` | `Directory.Delete(recursive)`（`/` とホームは**拒否**） |
| ルール | `deploy-template.json` | 同名同内容（`rule-check` で比較） |

## 実測

```
wcm selftest  →  15/15 PASS
wcm rule-check →  ルール一致：deploy-template.json
```

## 構成

```
workspace-corpus-manager/
  Program.cs / Wcm.csproj      # 本体（net10.0 コンソール。コマンド：new/backup/restore/selftest/rule-check）
  deploy-template.json         # Windows 版とバイト一致
  deployment-template/         # デプロイ用コーパス（導入 5 ファイル）
  test-sandbox/                # selftest の一時ディレクトリ（終了後削除）
```

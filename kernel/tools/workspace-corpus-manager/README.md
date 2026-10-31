# Workspace 语料管理器（Wcm · Mac / POSIX 版）

与 `[001]` 的 Windows 版 **同接口、同规则文件**（`deploy-template.json` 逐字一致，`rule-check` 可机器比对）。
把「生成部署版语料 / 备份当前语料 / 还原语料」做成可复用命令，随时一键产出干净统一的 WB workspace。

## 三个能力

| 命令 | 作用 |
|---|---|
| `wcm new --target <ws> [--template <dir>] [--reset]` | **生成部署版语料**：拷 `deployment-template/` 的引导语料（AGENTS/BULLETIN/GLOSSARY/USER/ONBOARDING）并建 `knowledge/docs/memory/handoff` 结构目录；`--reset` 先清空目标 |
| `wcm backup --workspace <ws> --backup <dir>` | **备份当前语料**到单个文件夹；自动排除 `state/` `.svn/` `bin/` `obj/` `.git/`、`.tui2.lock`、`deepseek apikey.txt` |
| `wcm restore --from <dir> --to <ws>` | **还原语料**（合并，不删目标已有文件） |

另有两个自检命令：

- `wcm selftest` —— 沙盒跑「生成 → 模拟积累 → 备份 → 破坏 → 还原 → 重置」，逐条 PASS/FAIL；
- `wcm rule-check [--windows <json>]` —— 与 Windows 版 `deploy-template.json` 逐字比对（规则不许分叉）。

## 用法

```bash
cd whitebox/tools/workspace-corpus-manager
dotnet build -c Release

# 1) 生成一个干净的部署版 workspace（新用户首访）
dotnet bin/Release/net10.0/wcm.dll new --target /tmp/ws-new --reset

# 2) 备份当前 WB 语料
dotnet bin/Release/net10.0/wcm.dll backup \
  --workspace ../../whitebox/workspace --backup ~/Backups/wb-corpus-2026-10-31

# 3) 还原
dotnet bin/Release/net10.0/wcm.dll restore --from ~/Backups/wb-corpus-2026-10-31 --to /tmp/ws-restored

# 4) 自检 / 规则对齐
dotnet bin/Release/net10.0/wcm.dll selftest
dotnet bin/Release/net10.0/wcm.dll rule-check
```

## 与 Windows 版的差异（只差在实现手段）

| 项 | Windows | Mac |
|---|---|---|
| 复制 | `robocopy /E /XD /XF` | `Directory.EnumerateFileSystemEntries` + `File.Copy`（同一份排除清单） |
| 重置 | `Remove-Item -Recurse` | `Directory.Delete(recursive)`（**拒绝** `/`、家目录） |
| 规则 | `deploy-template.json` | 同名同内容（`rule-check` 比对） |

## 实测

```
wcm selftest  →  15/15 PASS
wcm rule-check →  规则一致：deploy-template.json
```

## 结构

```
workspace-corpus-manager/
  Program.cs / Wcm.csproj      # 工具本体（net10.0 控制台，命令：new/backup/restore/selftest/rule-check）
  deploy-template.json         # 与 Windows 版逐字一致
  deployment-template/         # 部署版语料（5 份引导语料）
  test-sandbox/                # selftest 临时目录（跑完自删）
```

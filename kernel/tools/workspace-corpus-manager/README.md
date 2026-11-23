# Workspace corpus manager (Wcm · macOS / POSIX)

> English | [中文](README.zh-CN.md) | [日本語](README.ja.md)

Same interface and the **same rules file** as the Windows edition (`deploy-template.json` is byte-identical;
`rule-check` compares them mechanically). Turns “generate deployment corpus / back up the current corpus / restore the
corpus” into reusable commands, so a clean, uniform WB workspace is one command away.

## Three capabilities

| Command | What it does |
|---|---|
| `wcm new --target <ws> [--template <dir>] [--reset]` | **generate the deployment corpus**: copy the onboarding corpus from `deployment-template/` (AGENTS / BULLETIN / GLOSSARY / USER / ONBOARDING) and create `knowledge / docs / memory / handoff`; `--reset` clears the target first |
| `wcm backup --workspace <ws> --backup <dir>` | **back up the current corpus** into one folder; excludes `state/` `.svn/` `bin/` `obj/` `.git/`, `.tui2.lock`, `deepseek apikey.txt` |
| `wcm restore --from <dir> --to <ws>` | **restore** (merge; never deletes files already in the target) |

Two self-check commands:

- `wcm selftest` — runs generate → simulate accumulation → backup → damage → restore → reset in a sandbox, PASS/FAIL per step;
- `wcm rule-check [--windows <json>]` — byte-compares with the Windows `deploy-template.json` (rules must not fork).

## Usage

```bash
cd whitebox/tools/workspace-corpus-manager
dotnet build -c Release

# 1) a clean deployment workspace (first visit of a new user)
dotnet bin/Release/net10.0/wcm.dll new --target /tmp/ws-new --reset

# 2) back up the current WB corpus
dotnet bin/Release/net10.0/wcm.dll backup \
  --workspace ../../whitebox/workspace --backup ~/Backups/wb-corpus-2026-10-31

# 3) restore
dotnet bin/Release/net10.0/wcm.dll restore --from ~/Backups/wb-corpus-2026-10-31 --to /tmp/ws-restored

# 4) self-test / rule alignment
dotnet bin/Release/net10.0/wcm.dll selftest
dotnet bin/Release/net10.0/wcm.dll rule-check
```

## Differences from the Windows edition (implementation only)

| Item | Windows | macOS |
|---|---|---|
| Copy | `robocopy /E /XD /XF` | `Directory.EnumerateFileSystemEntries` + `File.Copy` (same exclusion list) |
| Reset | `Remove-Item -Recurse` | `Directory.Delete(recursive)` (**refuses** `/` and the home directory) |
| Rules | `deploy-template.json` | same name and content (`rule-check` compares) |

## Measured

```
wcm selftest  →  15/15 PASS
wcm rule-check →  rules identical: deploy-template.json
```

## Layout

```
workspace-corpus-manager/
  Program.cs / Wcm.csproj      # the tool (net10.0 console; commands: new/backup/restore/selftest/rule-check)
  deploy-template.json         # byte-identical to the Windows edition
  deployment-template/         # deployment corpus (5 onboarding files)
  test-sandbox/                # selftest scratch directory (removed afterwards)
```

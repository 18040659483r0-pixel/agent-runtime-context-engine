# Agent Runtime / WhiteBox · **1.0.1**

> 中文: [RELEASE-1.0.1.zh-CN.md](RELEASE-1.0.1.zh-CN.md) · 日本語: [RELEASE-1.0.1.ja.md](RELEASE-1.0.1.ja.md)

> ⬇️ **Download**: the installer assets are listed under **Assets** at the bottom of this release page. (中文/日本語の説明ページには Assets は出ません。)

**Second 1.x beta. Now with a Windows package.**

Two deployment packages, each with its own deployment manager:

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.1-macos-arm64.tar.gz` | macOS arm64 | `28cb4ed68dfe7fdfea8d0c4ce195247e4e077da2557f4271a9e744f22526349b` |
| `deploy-manager-1.0.1-winx64.tar.gz` | Windows x64 | `378d9740599df9d988c1036c3760e5fbc871439211b7cc4e02e4baa382902a55` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.0.1-<platform>.tar.gz
cd deploy-manager-1.0.1-<platform>
dotnet wcm.dll setup
./run.sh        # Windows: run.cmd
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.0.0-beta.1

### 1) Windows package + hardening

- `win-x64` package ships the same deployment manager and runtime, built for Windows.
- Console is forced to UTF-8 (no mojibake in cmd/PowerShell).
- Recursive delete clears read-only attributes first.
- Masked key input falls back to a normal read when the host does not support `ReadKey`.
- `doctor` now reports the **platform** and the **launcher** (`run.sh` / `run.cmd`).

### 2) Multiple providers (one active), per-model context window

- `config.xml` holds a `Providers` list + `ActiveProvider`; only the active entry is used. Existing single-provider configs keep working unchanged.
- Setup suggests a context window per model and lets you override it (`--context-window`). Defaults are conservative and editable — verify against your provider's docs.
- New `wcm config` changes provider / reasoning / context / key **in place**, without reinstalling or touching the workspace.

### 3) Frozen corpus path and deployment seeds

- Closeout now rebuilds/checks `frozen/` according to `config.xml`'s `Frozen.Root` (previously it wrote to a directory nobody read in a deployment).
- Deployment seeds now include `MEMORY.md`, `MEMORYINDEX.md`, `DECISION-LIBRARY.md`, so the first frozen build cannot fail on a missing source.
- `setup` builds the deployment's frozen corpus once, so a fresh install is self-consistent.

### 4) Single-session design: worker retired

- Worker / dispatch is retired: even with `worker.enabled=true` or an explicit `worker-dispatch` module, it is not assembled.
- CLI `worker` and `--worker-run` report "retired".
- Only the master runs; no sub-agents.

### 5) Protocol v107 + session prefix lock

- Closeout is executed **only** when requested (or when the runtime prints a closeout-window line). Ordinary task end just appends what was learned to the live files (pitfalls / decisions / process findings).
- Frozen prefix sources are **bound once per session**; mid-session closeout/promotion cannot change the running prompt. `start`/`reset` rebinds the latest version.

### 6) TUI2 input

- **Up/Down history** of submitted text (up to 100 entries; ↓ past the newest restores the draft).
- **Short paste inline**: pastes of ≤100 characters go straight into the input box; longer text becomes one atomic paste label.

## Performance status

**No dedicated performance optimization is included in this beta.** The reported measurements are baseline
numbers, not tuned results. Optimization will land across the beta series with before/after readings.

## Verification

| Item | Reading |
|---|---|
| Unit tests | **1,370 / 1,370** |
| TUI2 `--selftest` | **PASS** |
| Deployment manager self-test | **17 / 17** |
| macOS package (fresh directory) | checksum OK → `setup` → `doctor` green → deployed TUI `--selftest` PASS |
| Windows package | built with `win-x64`; run the Windows checklist in `DEPLOY` on first use |

## Known limitations

- Both packages require **.NET 10**.
- Windows: first-run verification should follow the checklist in `DEPLOY` (console encoding, key path, `run.cmd`).
- A fresh deployment's knowledge/decision L1 is empty: those frozen sections are skipped until the first real closeout grows them (reported, not blocking).

## Papers

[English](README.md) · [中文](README.zh-CN.md) · [日本語](README.ja.md)

## Assets

- [macOS arm64: `deploy-manager-1.0.1-macos-arm64.tar.gz`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-macos-arm64.tar.gz)
- [macOS arm64 checksum: `deploy-manager-1.0.1-macos-arm64.tar.gz.sha256`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-macos-arm64.tar.gz.sha256)
- [Windows x64: `deploy-manager-1.0.1-winx64.tar.gz`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-winx64.tar.gz)
- [Windows x64 checksum: `deploy-manager-1.0.1-winx64.tar.gz.sha256`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-winx64.tar.gz.sha256)

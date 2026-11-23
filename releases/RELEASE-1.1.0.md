# Agent Runtime / WhiteBox · **1.1.0**

> 中文: [RELEASE-1.1.0.zh-CN.md](RELEASE-1.1.0.zh-CN.md) · 日本語: [RELEASE-1.1.0.ja.md](RELEASE-1.1.0.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom. (The 中文/日本語 pages are notes only.)

**Fourth 1.x beta. Pick your language once — setup and the runtime both use it.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.0-macos-arm64.tar.gz` | macOS arm64 | `32a5c402da830befa605cf2a3a21fd2e2364f0e47575570de8e89f29d2692224` |
| `deploy-manager-1.1.0-winx64.tar.gz` | Windows x64 | `deb6eaef3c1259c069f0fe1f7d718a22bfa5270c760953e4b2ee26e15cc3f75a` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.0-<platform>.tar.gz
cd deploy-manager-1.1.0-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.0.2

### 1) Choose a language once — the whole flow follows

- The **first screen of `wcm setup` is the language menu**: English / 中文 / 日本語. Everything after it — prompts, health check, completion — switches immediately.
- The choice is written into the deployment's `config.xml` (`<UiLanguage>`), and **TUI2 inherits it on startup**: one selection carries from the installer into the running app (welcome tour, hints, status bar).
- `--lang en|zh|ja` (or `WB_LANG`) skips the question for scripted installs; `wcm config --lang` changes it later in place; `WB_TUI_LANG` still overrides per run.
- **English is the default** everywhere: the deployment manager, the setup path of the runtime CLI, and the terminal line (`[terminal] solved [DONE]: …`).

### 2) First run no longer looks broken

On a brand-new deployment, `closeout --check` used to fail on things that simply do not exist yet. Now the empty / seed states are reported as **green with a reason**, not as failures:

- decision library with 0 entries → convergence / tree-chain / version checks are skipped-green;
- knowledge base with seeds only (no L1 yet) → tree-chain and growth checks are skipped-green;
- no diary yet, and no convergence point in the watermark → skipped-green on first run.

Measured on a fresh install: `block 红 0 项（Core 0）· report 红 6（known debts, non-blocking）`.

### 3) `whitebox` command, on both platforms

- After setup you can type **`whitebox`** in a terminal to start *that* deployment.
- POSIX: `/usr/local/bin/whitebox` (falls back to `~/.local/bin/whitebox`), pointing at the deployment's `run.sh`.
- Windows: `%USERPROFILE%\bin\whitebox.cmd`, calling the deployment's `run.cmd`.
- Idempotent; `doctor` now reports a `whitebox command` line; if the directory is not on `PATH`, setup prints the line to add.

### 4) First-visit welcome starts with the three settings

The welcome tour now asks for, in order: **what to call you** (→ IDENTITY), **how you like to work** (→ USER), **your virtual role** (→ rules). Pitfalls are accumulated while working, not demanded on day one. This matches `ONBOARDING.md`.

### 5) Under the hood

- Every user-facing string in the deployment manager lives in a language pack; the runtime CLI's setup path (knowledge build, corpus bump, skip/check lines) is localized too.
- A test gate counts remaining hard-coded Chinese in UI code and **only allows the number to go down** (`ui-cjk-baseline.txt`), so "English by default" cannot silently regress.
- Unit tests **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS package D1–D7 PASS · Windows package D1+D9 PASS.
- Protocol stays **v107** (no prompt-prefix change, no cache reset).

## Coming from 1.0.1 or earlier (Windows)

1.0.1's Windows archive cannot start; both 1.0.2 and 1.1.0 include the fix (the `WINDOWS` symbol is now decided by the target RID). 1.1.0 additionally adds the language choice and the first-run fixes above.

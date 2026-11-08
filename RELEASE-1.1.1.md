# Agent Runtime / WhiteBox · **1.1.1**

> 中文: [RELEASE-1.1.1.zh-CN.md](RELEASE-1.1.1.zh-CN.md) · 日本語: [RELEASE-1.1.1.ja.md](RELEASE-1.1.1.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom. (The 中文/日本語 pages are notes only.)

**Fifth 1.x beta. Installer learns "this folder already has WhiteBox", the TUI paints immediately, and the first closeout stops arguing with itself.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.1-macos-arm64.tar.gz` | macOS arm64 | `9a5222dc0af0e3c5e26b4190815a3298016b04b2aeefb70102a372d8926becf5` |
| `deploy-manager-1.1.1-winx64.tar.gz` | Windows x64 | `c7c65d7c5773c5044efd6a61fb3d109d39cbc7adcff2ed1bba9eb2110553c445` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.1-<platform>.tar.gz
cd deploy-manager-1.1.1-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.0

### 1) Re-running setup on an existing folder is now a choice, not an error

Previously, if the target folder was non-empty, `setup` printed an error and exited. Now it asks:

```
[setup] found an existing WhiteBox deployment at ~/WhiteBox — choose:
  1) Overwrite: wipe that folder and install fresh
  2) Update: replace binaries only; keep workspace / provider / key
  3) Install to a different folder (keep the old one)
  4) Cancel
      choose [1/2/3/4] [2]:
```

- **Update** swaps `app/tui`, `app/cli`, `tools/closeout` (plus launchers and the `whitebox` command) and leaves `whitebox/workspace`, `config.xml` (provider / model / reasoning / language / key path), `state/` and `frozen/` untouched. Doctor prints `deploy-manager: 1.1.1`.
- **Different folder** keeps the old deployment in place; the `whitebox` command always points at the newest install.
- Non-interactive installs must be explicit: `--force` (fresh) or `--update` (in place).

### 2) The TUI paints its first frame immediately

On Windows the window stayed blank until you clicked inside it. The first frame is now written before the input loop starts, so the screen is populated the moment the app opens (all platforms; no change on macOS behaviour).

### 3) Top detail panel is visible by default

- It used to start hidden behind Tab. It now starts **shown**; Tab still toggles it.
- The first line carries the hint after the product name:
  `ⓘ AgentRuntime TUI2 - WhiteBox   (Tab: hide this panel; Tab again: show)`
  (localized for 中文 / 日本語).

### 4) First closeout: the last blocking check is gone

`entry → L4 coverage` blocked on a brand-new deployment after the first real conversation (the runtime files one process finding automatically, and the seed knowledge base has no L1 chain yet). On first run that check now reports, in green:

`first run: N pending entries will be migrated by --apply (l4-sync); backlog N`

Once the library has grown (version > 0 or L1 present), the strict check comes back.

Measured on a fresh deployment, after writing one process finding:
`block red 0 (Core 0) · report red 6 (known debts, non-blocking)`.

### 5) Small polish

- Corpus summary line rounds tokens (`≈ 793 tokens`).
- The missing-API-key message is English by default.

### 6) Unchanged

- Protocol stays **v107** (no prompt-prefix change, no cache reset).
- Language selection, first-run relaxations for the other checks, and the `whitebox` command behave as in 1.1.0.
- Unit tests **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS package D1–D7 PASS · Windows package D1+D9 PASS.

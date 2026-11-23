# Agent Runtime / WhiteBox · **1.1.6**

> 中文: [RELEASE-1.1.6.zh-CN.md](RELEASE-1.1.6.zh-CN.md) · 日本語: [RELEASE-1.1.6.ja.md](RELEASE-1.1.6.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Tenth 1.x beta. Knowledge writes are no longer a promise: the runtime measures them and shows them on the card.**

> **⚠️ Upgrading from an earlier beta?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.
> This build still auto-backs up an old workspace to `<install-dir>-backup-<timestamp>.zip` before a force install,
> and refuses to wipe if the backup fails — but **make your own backup first** anyway.

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.6-macos-arm64.tar.gz` | macOS arm64 | `76104f57f8f2dae5c58a7c332fa91c53a2ad1c69353fda66ed7e9961d114b79d` |
| `deploy-manager-1.1.6-winx64.tar.gz` | Windows x64 | `1ab395d3f12e4dbb736274c48df5f78cf3afe9725a580dc8e28548ba27374341` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.6-<platform>.tar.gz
cd deploy-manager-1.1.6-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.5

### 1) Knowledge writes are measured, not promised

The runtime now measures the **line delta** of the three live files after every successful tool call —
`docs/PITFALLS.md`, `docs/DECISIONS.md`, `docs/PROCESS-FINDINGS.md` — and emits a `[LEDGERWRITE]` event.
The card shows it as `＋坑集 +9` / `＋决策 +2` on the result line.

This covers `exec` + heredoc appends, which the old marker (write/edit receipts only) never saw.
**No marker means nothing was written** — a “to be added later” note can no longer pass for work done.

### 2) Protocol v113: record in the same turn, and point at the entry

- Clause 7: a pitfall or a user decision is written into the project’s live files **in the same turn** it is learned;
  “parked as a todo” is explicitly not a delivery; if the task added nothing, say `no new entries`.
- Clause 12: a `found` line for an entry that was written to a live file must end with that entry’s anchor
  (`-> pitfall #70` / `-> D-0047`), so the report points at the entry instead of promising it.
- Budget: 6,200 → **6,900 tokens** (measured 6,325; nothing was compressed to make room).

### 3) The test suite no longer touches your `whitebox` command

The deployment manager’s own test suite ran a real `setup`, which rewrote the machine-wide `whitebox` launcher
to point at a temporary sandbox; when the sandbox was cleaned up the command died silently.
The suite now runs `setup` with an isolated `HOME` and snapshots/restores the real launcher
(`/usr/local/bin/whitebox`, `~/.local/bin/whitebox`, `%USERPROFILE%\bin\whitebox.cmd`).
Verified by hashing the launcher before and after a full run.

### 4) Fixes and housekeeping

- Protocol clause 19 (from the 1.1.5 re-cut) still holds: closeout / reset / start run on a human request **or** the ≥80% window line.
- Pitfall log: #400 (two protocol clauses contradicting each other), #401 (derived pitfalls index not regenerated),
  #402 (test suite clobbering the global launcher), #403 (knowledge marker blind to `exec`+heredoc).
- Repository hygiene unchanged: no Python, no JSON sources; rules file is `deploy-template.xml`.

## Verification

- Unit tests: **1399 / 1399**
- TUI2 self-test: **PASS**
- Deployment manager self-test: **17 / 17**
- Package gates: macOS **D1–D7 PASS**; Windows **D1 + D9 PASS** (`libc`-free Windows build)
- Independent Windows E2E: **T1–T6 = 10 / 10 PASS** (first closeout, decision D-0001 convergence, knowledge self-growth, promote/close)
- Isolated upgrade test: `--update` keeps `workspace`, `config.xml`, key and user declarations byte-for-byte

## Notes

- Windows: close WhiteBox/TUI2 before updating (Windows locks binaries).
- An in-place `--update` rewrites the `whitebox` launcher to that install directory — expected.
- Version **1.1.6 (beta)**; protocol **v113**.

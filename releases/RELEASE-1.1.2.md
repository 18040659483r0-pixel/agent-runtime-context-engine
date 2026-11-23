# Agent Runtime / WhiteBox · **1.1.2**

> 中文: [RELEASE-1.1.2.zh-CN.md](RELEASE-1.1.2.zh-CN.md) · 日本語: [RELEASE-1.1.2.ja.md](RELEASE-1.1.2.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom. (The 中文/日本語 pages are notes only.)

**Sixth 1.x beta. A five-entry seed now grows itself into a real tree — and the first closeout finally runs clean.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.2-macos-arm64.tar.gz` | macOS arm64 | `78c9b37ef01bdb9b4da567fc4d82145c2aa50276b1eea7beb6d9578a0ddbecae` |
| `deploy-manager-1.1.2-winx64.tar.gz` | Windows x64 | `f2448bce25715f2f9d25fbbc23786f66b66ad1729fb2e07e9caefb80e13e9ec6` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.2-<platform>.tar.gz
cd deploy-manager-1.1.2-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.1

### 1) The four-layer tree grows itself (no template needed)

A deployment ships with **five L4 evidence entries and nothing above them**. Until now the parent layers (L3 → L2 → L1) depended on the remote model inventing a shape from an empty page — it mostly did not, so the chain check stayed red and the first closeout always looked broken. The decision library was worse: the first decision blocked the user outright.

Now `closeout --apply` runs a deterministic grower:

- **Knowledge**: each L4 belongs to a short-name family (`ver`, `pfd`, …); the grower creates the missing `L3 → L2 → L1` parents (`<!--kn: … next=… -->`) and links them.
- **Decisions**: every `L4-####` carrying a `D-####` gets a full `L1 → L2 → L3 → L4` chain in the exact line shape the gates accept.
- Parent bodies are marked **"auto-grown · to be abstracted"** — the remote model can rewrite them any time. The structure is deterministic; the prose is not. Idempotent: it only fills the missing layers.

Measured on a fresh deployment with one process finding and one decision: knowledge **L1 6 / L2 6 / L3 6 / L4 6**, decision **1/1 converged**, both chain checks green.

### 2) The first closeout runs end to end

Four deployment-only gaps are fixed:

- `--apply` looked for the CLI at a source-repo path; in a deployment it now uses `app/cli/AgentRuntime.Cli.dll`.
- Without the publish word-list, the public-pitfalls step is skipped instead of failing the run.
- `--promote` creates the knowledge ledger on first use (v0 → v1) instead of exiting with rc=2.
- `MEMORYINDEX.md` ships with the `## Timeline` section the material check expects.
- Migrated L4 bodies are compacted so the "unmarked body" check no longer false-fires.

### 3) Head files exist from the first day

A deployment now ships `SOUL.md`, `IDENTITY.md` and six `*-changeOrder.md` forms; on the first closeout the runtime creates whichever are still missing (neutral templates — no persona is assumed) and stamps each request form's watermark from its `## 请求` body. Pending `[open]` requests keep the watermark un-advanced, so nothing pretends to be converged.

The deployment's `AGENTS.md` (which becomes the frozen `rules/global.md`) now carries a map of the six head files: what each one is for, when it is used, and where first-visit answers belong.

### 4) Protocol v108

- Clause 15 now explains the six head files (AGENTS / USER / SOUL / IDENTITY / PRINCIPLES / GLOSSARY) and the first-visit three settings.
- Clauses 21 / 22 make the plain-language rule **wording-only**: the persona defined by the head files stays binding, and any in-session (R2) tone instruction joins that constraint set immediately. "Speak plainly" can no longer flatten a requested voice.
- Budget raised **5,600 → 6,200 tokens** (measured: 23,577 characters / 5,895 tokens / 26 lines). A protocol change resets the prompt-prefix cache once on the next start — expected.

### 5) Verified

Unit tests **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS package D1–D7 PASS · Windows package D1+D9 PASS. Isolated fresh deployment: first closeout `block red 0 (Core 0)`, knowledge 6/6/6/6, decision 1/1, only environment-level report notes remain.

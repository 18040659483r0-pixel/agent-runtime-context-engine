# Agent Runtime / WhiteBox · **1.1.3**

> 中文: [RELEASE-1.1.3.zh-CN.md](RELEASE-1.1.3.zh-CN.md) · 日本語: [RELEASE-1.1.3.ja.md](RELEASE-1.1.3.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom. (The 中文/日本語 pages are notes only.)

**Seventh 1.x beta. The deployment now ships the working rules a first-time user actually needs — and decisions are picked up in the format people write them.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.3-macos-arm64.tar.gz` | macOS arm64 | `358f2a2c92a54cf9f1e6cae369005dc2179edf7106e58aa5ec6a24e21abbf764` |
| `deploy-manager-1.1.3-winx64.tar.gz` | Windows x64 | `39edd8c8fa7e92d29e2cb81bfefa9f1020b0ba74aa3670ae0c845f9c7006eef3` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.3-<platform>.tar.gz
cd deploy-manager-1.1.3-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.2

### 1) The deployment's AGENTS.md now carries the working rules

`AGENTS.md` is the source of the frozen `rules/global.md`, so it is in the prompt every turn. The deployment version was a welcome page only; it now also explains how to work:

- **Architecture rules**: look up knowledge/tools before thinking (Tool First); the four-layer knowledge tree (L1 law → L2 question map → L3 unit → L4 verbatim evidence); deterministic work belongs in programs, judgement in the model; explanation-only questions are answered and then stopped; never judge success through a pipe (`| tail` hides the real exit code).
- **Closeout rules**: the fixed pipeline (`--check` → write → `--apply` → `--promote` → `--close` → `--check`); the watermark is written only by `--close`; absolute paths plus read-back when writing; closeout runs **only when the user asks**.
- **Memory**: daily file, long-term file, index, watermark.
- **Safety**: no private-data leakage, ask before destructive actions, review before touching schedulers, ask before anything leaves the machine, keys stay in `state/api_key`.
- **Index pointers**: knowledge, decision entries/library, process findings, glossary, handoff, onboarding.

All private material (repository paths, cross-team split, release pipeline, local paths, internal codenames) is removed; the "startup ritual" is not shipped — it belongs to the development side.

### 2) Decision entries are read in the shape people actually write

Two formats are accepted:

```md
### D-0001 a one-line title
- **Status**: accepted
- **Inferred preference**: what to do next time
- **Original words**: "…"
```

or the older single-line `- **[D-0001] title**` form. Fields `主题 / 判定 / 记录` (topic / ruling / note) are accepted too.

Two real bugs fixed along the way: a `### D-####` heading was **not matched at all** before (only `##` was), and the **format example inside the template's HTML comment was parsed as a real entry** — comments are now stripped before parsing, and duplicate ids keep the first occurrence only.

The grower also no longer edits and inserts at the same time (that shifted indices and wrote a `## L4` heading in as an entry).

### 3) Verified in an isolated deployment

Fresh macOS deployment, one process finding plus **two decision entries** (one written in the `###` style above, one in the older style) → `--apply` → `--promote` → `--close` → `--check`:

```
block red 0 (Core 0) · decisions 2/2 converged (L1/L2/L3/L4 = 2 each) · knowledge 6/6/6/6
only note left: archive declaration (folder is not an SVN working copy — environment)
```

Unit tests **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS package D1–D7 PASS · Windows package D1+D9 PASS.

### 4) Unchanged

- Protocol stays **v108**; language selection, first-run relaxations and the `whitebox` command behave as in 1.1.2.

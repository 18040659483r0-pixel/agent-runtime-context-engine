# Agent Runtime · v9.1 (Alpha 0.1.6) · **Release Candidate**

> 日本語: [`ALPHA-0.1.6-RELEASE.ja.md`](ALPHA-0.1.6-RELEASE.ja.md) · 中文: [`ALPHA-0.1.6-RELEASE.zh-CN.md`](ALPHA-0.1.6-RELEASE.zh-CN.md) · Test report: [`ALPHA-0.1.6-TEST-REPORT.md`](ALPHA-0.1.6-TEST-REPORT.md)

**Status: still a release candidate, and now a *deployable* one.** This release ships the **deployment corpus**
instead of an internal workspace: a brand-new user gets a clean workspace (welcome/onboarding docs, empty
knowledge/docs/memory/handoff) plus a public knowledge seed, and the console greets them once with a short
tour of what the runtime accumulates and why closeout matters.

Protocol zone: **v104** (26 lines / 22,196 chars / ≈5,549 tokens, budget ≤5,600).

## What changed since v9.0

### 1) The published slice is a **deployment** slice (new rule, going forward)

- `kernel/whitebox/workspace` is now produced by the **Workspace Corpus Manager (Wcm)**, not copied from the
  internal workspace: `AGENTS.md` · `BULLETIN.md` · `GLOSSARY.md` · `USER.md` · `ONBOARDING.md`,
  plus `knowledge/ docs/ memory/ handoff/`, a public knowledge seed and its derived layer.
- `config.xml` now points its frozen root at the shipped public corpus (`frozen/`).
- Every future release follows this spec: **deployment corpus + first-use onboarding**.

### 2) First-use welcome (once)

- On first start the console shows a short guide: what gets accumulated (knowledge / decisions / pitfalls),
  the normal flow, why closeout matters, and an invitation to trigger one real accumulation.
- Shown once (`~/.agentruntime/.welcome-shown`); `WB_TUI_NO_WELCOME=1` skips it; localized (en / zh / ja).

### 3) Protocol v104 — head documents now change through request files

- `AGENTS.md`, `USER.md`, `PROJECT-DESIGN-PRINCIPLES.md`, `GLOSSARY.md`, `SOUL.md`, `IDENTITY.md` are read-only
  at runtime; changes are appended to their own `<name>-changeOrder.md` with a watermark line, and closeout
  converges them into the head file and advances the watermark. A closeout gate checks the six pairs.

### 4) Closeout engine: project corpora flow into L4 automatically

- Pitfalls and project decision entries are scanned automatically (per-file watermarks);
  decision L4 lines are written in the library's own shape and marked `（待挂）` until attached to an L3.
- Fixed three real defects: `D-0010` keys evaluating to zero, list-style pitfall files being unread, and
  decision nodes being written in a shape the decision-tree gate does not recognise.

### 5) Console hardening

- **Cross-thread crash fixed**: worker callbacks no longer mutate UI state from a background task; they queue
  and the main loop applies them (single writer).
- **Intent lines** now accept translated block tags (`[计划]` / `[結果]` / `[計画]`) and English plan-like prose.
- **Esc** pauses the remote model; **Esc twice** exits (Ctrl+C no longer exits).
- **Error lines** always use the language pack (no more `[错误] the path is empty.`).
- **Wide-gamut displays**: `WB_TUI_COLORSPACE=display-p3` pre-compensates sRGB palette values for unmanaged
  P3/10-bit panels (default remains sRGB).

## Verification (inside the slice)

- `dotnet build AgentRuntime.slnx -c Release`: **0 errors**.
- `dotnet test tests/AgentRuntime.Tests -c Release`: **1359 / 1361**; the 2 failures are **internal-only**
  (they require the internal pitfall corpus, which the public slice does not ship).
- Pre-publish scan: **0 hard findings**.
- Deployment corpus: 5 onboarding documents + 4 structure dirs + knowledge seed (19/19/19/19) + derived layer.

## Boundary

Private workspace, internal pitfall corpus and training data are not part of the public slice.
Corpus licensing: `kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`.

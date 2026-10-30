# Agent Runtime · v9.0 (Alpha 0.1.5) · **Release Candidate**

> 日本語: [`ALPHA-0.1.5-RELEASE.ja.md`](ALPHA-0.1.5-RELEASE.ja.md) · 中文: [`ALPHA-0.1.5-RELEASE.zh-CN.md`](ALPHA-0.1.5-RELEASE.zh-CN.md) · Test report: [`ALPHA-0.1.5-TEST-REPORT.md`](ALPHA-0.1.5-TEST-REPORT.md)

**Status: nearly release-grade.** Defect density is very low, the runtime now has a single, continuously
exercised console, and the whole user interface is available in **three languages (English by default)**.
This release is still flagged `pre-release` for exactly two reasons: the systematic throughput/latency
work has not been done yet, and public-facing interfaces may still shift with kernel versions.

Protocol zone: **v102** (25 lines / 20,933 chars / ≈5,234 tokens, budget ≤5,600).

## What changed

### 1) TUI1 retired — TUI2 is the only surface (**breaking**)

- The whole `kernel/src/AgentRuntime.Tui/**` project is gone, together with what existed only to serve it:
  `tools/demo-doc`, `docs/TUI-DEMO-SAMPLE.md`, and the `wb-cli` commands `verify-demo` / `demo` / `ctrl-c-e2e`.
- Parts that were still valuable **moved up to the right layer** instead of dying with the old console:

| Was in TUI1 | Now |
|---|---|
| `PanelRouter` · `AblationService` | `src/AgentRuntime.Hosting/Commands/` (one command router shared by TUI2 and the CLI) |
| `CrashDump` (diagnostics for unhandled exceptions) | `src/AgentRuntime.Hosting/Diagnostics/`; **TUI2 now writes it too** (`last-crash.txt` + `crash-log.txt`) |

- Launcher: `whitebox` (PATH command) starts TUI2 directly; legacy flags `--tui1` / `--legacy` / `--tui-old` are stripped.
- **No longer a rollback point**: to go back, use the previous release tag (`v8.1`).

### 2) UI language pack (strongly-typed keys · three languages · **English by default**)

- Keys are **`enum TuiKey` (89 keys)**: a typo in a key name does not compile — no more "missed translation / wrong key".
- Language: `WB_TUI_LANG=en|zh|ja`, **default `en`**; unrelated to the language you speak to the model.
- Covered: top band (session / three-tier modules / stack / board / draft / focus), status meter, selection and
  copy hints, the `[session]` / `[start]` / `[paste]` / `[resume]` / `[interrupt]` / `[error]` prefixes,
  card tool verbs, start-up banner (session number / stream / end state / previous volume / workers),
  and decision-report labels.
- Gates: every key × three languages must be non-empty; with English selected, **no CJK may appear on screen
  or in the start-up banner** (a single Han character turns the gate red).

### 3) Knowledge-node ids: normalised at generation, fail-closed at parse

- Generation: `--l4-sync` normalises the short name (keeps `[a-z0-9]`, ≤16 chars) and zero-pads the sequence to
  three digits, then **self-checks before writing — an invalid id throws instead of being written**.
- Parsing: a line that starts like a node marker but does not match the canonical form now **fails with a line
  number** instead of silently skipping the entire node (the old behaviour loaded 428 of 444 L4 nodes while
  looking like "the tail of the next list was dropped").

### 4) Modules line, crash evidence, self-test green

- The top band's `Modules` line labels each module by the bytes it **actually contributed this turn**:
  `(Cached)` assembled into context / `(Active)` live store fetched on demand / `(Runtime)` everything else.
- TUI2 `--selftest` went from red to green (10 assertions had hard-coded Chinese labels and could not survive
  an English default).

## Verification (done inside the slice, not in the build tree)

- `dotnet build AgentRuntime.slnx -c Release`: **0 errors**.
- `dotnet test tests/AgentRuntime.Tests -c Release`: **1347 / 1349**; the 2 failures are **internal-only**
  (`PitfallsDerivedTests` requires the internal pitfall corpus, which is not part of the public slice).
- Pre-publish scan: **0 hard findings** (soft hits are honorifics/personal names, judged by a human).
- TUI2 `--selftest`: **PASS**.

## Boundary

Private workspace, internal pitfall corpus and training data are not part of the public slice.
Corpus licensing: `kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`.

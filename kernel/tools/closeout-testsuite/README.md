# Test-suite index (local edition)

> English | [中文](README.zh-CN.md) | [日本語](README.ja.md)

> **Four suites, one shape**: each = `SCENARIOS.md` (scenarios + criteria) + `RUNS.md` (actual runs).
> Every criterion is **recomputable** (command / rc / report line / count), never “looks fine”.
> Organised 2026-10-16.

| # | Suite | Version | Location | Status | What it measures |
|---|---|---|---|---|---|
| **S1** | master cost/efficiency suite | v1 | `tools/master-testsuite/` | **in use** | whether one master turn is worth the money: turns / tokens / rework / idle |
| **S2** | master closeout suite | **v1** (2026-10-16) | `tools/closeout-testsuite/` ← this directory | **in use** | whether closeout completes, where it stalls, how far it scales without breaking stability |
| **S3** | master-worker dispatch suite | v1 | `tools/worker-mode-acceptance/` | ⏸ **paused** (single master) | the dispatch chain (payload / contract / receipt / settlement) |
| **S4** | I-series network-tool performance | **v1** (2026-10-18) | `tools/network-testsuite/` | **in use** | five network tools: function / negative cases / **real tokens + wall clock** |

**Why keep S3**: it covers the dispatch chain (no overlap with S1/S2) and will be reused if workers return; worker
memory has left closeout and the chain is disabled, so it is **archived, not run**.

## How to run

| Suite | How | Duration |
|---|---|---|
| S1 | see `tools/master-testsuite/SCENARIOS.md` (human/model driven, scenario by scenario) | minutes to ~15 min |
| S2 | ① mechanical: `SCENARIOS.md` §A (all command-line, seconds)<br>② real: §B (tmux TUI → `/start` → run closeout) | seconds / 5–15 min |
| S3 | ⏸ paused | — |
| S4 | `dotnet run --project tools/network-testsuite` (8 cases, official DS) | ~1 min total |

## Version rules (shared)

- From **v1**: any change to scenarios or criteria ⇒ bump the version and record “v → x, why” in that suite’s `RUNS.md`.
- **Run records are append-only** — one table block per run with date, version, readings, findings.
- Criteria are **hard** (red/green) or **soft** (recorded, not a pass condition) — soft readings show trends.

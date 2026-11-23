# Alpha 0.1.6 · Test & Release Report (v9.1)

> 日本語: [`ALPHA-0.1.6-TEST-REPORT.ja.md`](ALPHA-0.1.6-TEST-REPORT.ja.md) · 中文: [`ALPHA-0.1.6-TEST-REPORT.zh-CN.md`](ALPHA-0.1.6-TEST-REPORT.zh-CN.md) · Release notes: [`ALPHA-0.1.6-RELEASE.md`](ALPHA-0.1.6-RELEASE.md)

All readings were taken **inside the published slice** after cleaning build artifacts.

| Item | Reading |
|---|---|
| Build | **0 errors** |
| Tests | **1359 / 1361** |
| The 2 failures | `PitfallsDerivedTests` — **internal-only** (need the internal pitfall corpus) |
| Pre-publish scan | **0 hard findings** |
| Protocol | v104 · 26 lines / 22,196 chars / 5,549 tokens (≤5,600) |
| Slice size | **655 files / 1.6 MB** tarball (no `bin/obj`) |
| Deployment corpus | 5 onboarding docs + 4 dirs + knowledge seed 19/19/19/19 + derived layer |

## Reproduce

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## Deployment corpus check

```bash
ls whitebox/workspace
# AGENTS.md BULLETIN.md GLOSSARY.md USER.md ONBOARDING.md docs handoff knowledge memory
```

First start shows the welcome guide once, then opens the console as usual.

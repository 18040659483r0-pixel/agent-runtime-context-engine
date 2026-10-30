# Alpha 0.1.5 · Test & Release Report (v9.0)

> 日本語: [`ALPHA-0.1.5-TEST-REPORT.ja.md`](ALPHA-0.1.5-TEST-REPORT.ja.md) · 中文: [`ALPHA-0.1.5-TEST-REPORT.zh-CN.md`](ALPHA-0.1.5-TEST-REPORT.zh-CN.md) · Release notes: [`ALPHA-0.1.5-RELEASE.md`](ALPHA-0.1.5-RELEASE.md)

> All readings were taken **inside the published slice** (fresh directory), not in the private build tree.

| Item | Reading |
|---|---|
| Build (`AgentRuntime.slnx -c Release`) | **0 errors** |
| Tests (`tests/AgentRuntime.Tests`) | **1347 / 1349 passed** |
| The 2 failures | `PitfallsDerivedTests.公开变体_与python派生物逐字节一致` · `坑集索引_与python派生物逐字节一致` — **internal-only**: they need the derived artefacts of the internal pitfall corpus, which the public slice does not ship |
| TUI2 self-test (`AgentRuntime.Tui2 --selftest`) | **PASS (all assertions)** |
| Pre-publish scan (`wb-cli scan <slice>`) | **0 hard findings**, publishable (soft hits are honorifics/personal names) |
| Protocol zone | v102 (25 lines / 5,234 tokens ≤ 5,600) |
| Slice size | **635 files / 1.6 MB** tarball (no `bin/obj`) |

## Reproduce (anyone can run it)

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## Test changes in this release

**Added**
- `TuiStringsTests`: three-language key set is one-to-one, **no CJK in English**, and the start-up banner goes
  through the language pack (switching to Chinese must switch back)
- `TopBandLanguageTests`: renders the top band with a real host — any Han character under English turns it red;
  an empty desk must use the language-pack empty text
- `KnowledgeSinkTests`: **a non-canonical node marker must fail, never be skipped silently** (includes a positive case)
- `TuiInvariantTests`: **preassembled request is sent as the same object — one assembly per turn** (`Assert.Same`)

**Removed with TUI1** (12 files + several frame-level cases)
- `TuiSplitTests`, frame-level cases of `TuiInvariantTests`, `TuiExitVoiceTests`, `BracketedPasteTests`,
  `ExitPolicyTests`, `InputCommandRuleTests`, `LifecycleCardViewTests`, `SelfReportDisplayTests`,
  `DecisionReportBindingTests`, `DecisionReportFlowTests`, `ContinuationCapTests`, `LifecycleCloseoutTests`,
  `ThinkingLineRoleTests`, `ToolVisibilityTests`

> Full suite in the internal build: **1349 / 1349**. The public slice is 2 short (internal-only gates).

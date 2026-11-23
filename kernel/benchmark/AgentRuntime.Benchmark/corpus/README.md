# corpus/ — the publishable measurement corpus (de-identified slices of real material)

> English | [中文](README.zh-CN.md) | [日本語](README.ja.md)

> **What this is**: real-corpus slices used by the benchmark, stacked as **rules → knowledge → memory**, about a
> **10k-token** tier. The content comes from real engineering material (not synthetic) but is **de-identified**, so it
> **ships with the repo** — anyone can reproduce the same tier without private assets.

| File | Source (real material) | Chars | Layer |
|---|---|---|---|
| `rules.md` | workspace rules and process | 4,330 | rules |
| `knowledge.md` | abstract methodology | 8,607 | knowledge |
| `memory.md` | memory index (timeline) | 5,053 | memory |
| **Total** | — | **17,990** | ≈ **9,569 tokens** (~1.88 chars/token measured) |

## De-identification rules (enforced at generation)

1. **Partial injection only**: slice by character budget, never copy a whole document.
2. Names / host paths / intranet addresses / accounts / passwords / tokens / phone numbers / cloud-drive codes /
   amounts → **placeholders**.
3. After the build, a **self-check must report 0 hits**, otherwise the output is refused.

## Reproduce

```bash
# rebuild (from real local material; output is this directory)
dotnet run --project benchmark/AgentRuntime.Benchmark -- public-corpus

# self-check existing artifacts only (no rebuild)
dotnet run --project benchmark/AgentRuntime.Benchmark -- public-corpus --check
```

To change the tier size, edit `Sources` (source → budget) in
`benchmark/AgentRuntime.Benchmark/PublicCorpusCli.cs` and `corpus.tiers` in `benchmark.config.xml`; keep them in sync.

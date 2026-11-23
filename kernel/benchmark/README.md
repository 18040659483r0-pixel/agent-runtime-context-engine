# Benchmark — the ruler for the Agent Runtime (independent of the subject)

> English | [中文](README.zh-CN.md) | [日本語](README.ja.md)

> **Iron rule: the ruler must not belong to the subject.** This directory is **not** part of the Runtime and the
> Runtime **never** references it — otherwise “Runtime changed → Benchmark quietly changed too → numbers look better”
> destroys the experiment.
> Spec version: **V0.1** (fixed 2026-09-14, defined before the architecture). Goal: whatever layer the Runtime reaches,
> the **same ruler** yields comparable `Direct API` vs `Agent Runtime` numbers.

## About `runs/`

- `runs/` is the **local evidence sink**: `summary.md` / `records.jsonl` / stream copies / `-v` diagnostics.
- **Nothing under `runs/` is committed** by design; references like `runs/<name>/` are local pointers.
- The 58 run directories (~3.9 MB) were cleared from this machine on 2026-10-15 (owner’s order: run records stay out of
  public SVN); `runs/` itself remains tracked (empty). Conclusions are reproducible from the summaries/tables in `docs/`.

## 0. Fixed items (lock these for any measurement)

Model, provider, parameters, test data, test procedure, network — **all fixed**. Change any one and historical data is
no longer comparable; rebuild the baseline.

## 1. Five metric families

| Family | Metrics | The question it answers |
|---|---|---|
| Token Efficiency | input / output / total tokens; repeated tokens; total per task | How many tokens are actually saved |
| Cache Efficiency | cached / uncached tokens, hit rate; cost and latency impact | How much input is reused |
| Execution Efficiency | total latency, TTFT, request count, throughput, failures/retries | How much faster and steadier |
| Runtime Overhead | Runtime milliseconds (serialisation / context handling, excluding network) | Does the framework drag |
| Task Equivalence | Did both sides complete the **same** task: success rate + output quality | Did we secretly do less work to save tokens |

## 2. Phase-1 test units

| ID | Shape | Purpose |
|---|---|---|
| **T01** | fixed prompt → model → response | the raw Token / Cost / Latency baseline |
| **T02** | A / A / A / A (same request ×4) | does the upstream cache exist, and what does it really change |
| **T04** | fixed multi-turn script | **session dimension**: task completion + prompt/cached growth per turn |
| **T05** | T04 × toggling modules (bare / +session / +session+memory …) | **module ablation**: the marginal value of each module |
| **T06** | **frozen-context ladder**: three sizes (L0 / L1 1k / L2 10k tokens) × three shapes (stable append / dynamic-first counter-example / Runtime path) × 5 turns | how much freezing buys, and what prefix stability is worth |
| **T07** | **frozen version change**: `v1→v1` / `v1→append tail` / `v1→change head`, 2 turns each | quantifies “**where you change matters more than how much**” |

> `T03` (prefix append) is retired; **T06 supersedes it** (real corpus, three sizes, isolated variants).

### Two hard preconditions for ablation (built into the architecture)

1. **Bare chat always exists**: remove every module and behaviour must equal V0.
2. **Modules are individually removable**: removing any module leaves the others **byte-identical** (tests pin it).

### Experiment discipline (each rule paid for by a real mistake)

1. **Variant isolation**: every variant’s corpus carries a unique salt **at the very front** of the frozen prefix
   (tail-only salt still shares most of the prefix and keeps hitting cache).
2. **Cold-start self-check**: each variant’s **turn 1 must be `cached = 0`**; otherwise external warming invalidates the run.
3. **Raw records first**: reports are views; conclusions must be recomputable from `records.xml` line by line.
4. **Corpus snapshot stays private**: real-material slices live only in `local-corpus/`, never in a public repo.
5. **Cost table fixed**: prices live in `benchmark.config.xml` with source and date.

## 2.5 Observed facts (measured, not targets)

**Upstream prefix cache**

| Observation | Value |
|---|---|
| Exists? | **Yes**; DeepSeek V4 Flash returns both `prompt_cache_hit_tokens` and `prompt_tokens_details.cached_tokens` |
| Granularity | hits are always a **multiple of 64** (896=64×14, 9856=64×154) → 64-token block alignment |
| Short prefix | a few dozen tokens **never hit** (once misread as “no cache support”) |

**Frozen-context ladder (variants isolated, turn 1 cold)**

| Tier | Frozen size | Stable: hit / cost per turn / saved | Dynamic-first counter-example |
|---|---|---|---|
| L0 | 0 chars | 0 / $0.000015–34 / **0%** | — |
| L1 | 1771 chars = **1,000 tokens** | 896 (89%) / $0.000046–55 / **~67%** | 0 / $0.000149–192 / **0%** |
| L2 | 18,760 chars = **9,974 tokens** | 9856 (**98.4%**) / $0.000299–308 / **~78.5%** | 0 / $0.001403–417 / **0%** (**4.7×**) |
| L1/L2 | via the Runtime | identical to calling by hand | — |

> **The bigger and steadier the frozen context, the more you save (0% → 67% → 78.5%); put “changes every turn”
> content first and the gain drops to zero.** Cost reference: cacheRead ≈ 1/5 of input (0.14 vs 0.028).

**V4.1 semantic-focus A/B** (2026-09-15): accuracy A 6/6 = B 6/6, cached 97.7% both, prompt +45 tokens for B.
⇒ architecturally sound, benefit unproven on this fixture (ceiling). Community re-verification is open
(`docs/EXPERIMENT-V4.1-FOCUS.md`; needs ≥500k-token contexts).

## 3. How to compare

```
Direct API   vs   Agent Runtime
```

Same test units, same model, same parameters; run both sides and compare item by item.

## 4. The six numbers we must be able to answer

| Metric | Question |
|---|---|
| Total tokens | how many tokens saved |
| Cached tokens | how much is reused |
| Total cost | how much money saved |
| Latency | how much faster |
| Runtime overhead | does the framework drag |
| Success rate / master context | did capability drop, and how much context isolation saved |

Conclusion form: `no Runtime → with Runtime: tokens ↓x%, cost ↓y%, speed ↑z%, master context ↓w%`.

## 5. Implementation constraints

- A separate executable (`benchmark/AgentRuntime.Benchmark/`) that **references** the Runtime; never the reverse.
- Every run writes **raw records** (JSON/XML): time, model, parameters, per-request usage and latency — conclusions
  must be recomputable from them.
- Reports show **measured** columns only; untested cells say “no measurement yet”.
- `RuntimeTiming.RuntimeOverheadMs` (total − provider) is the single source for Runtime overhead.

## Corpus lock (owner, 2026-09-14)

Test projects may use **only the in-repo 10K slice** (`corpus/rules.md` + `corpus/knowledge.md` + `corpus/memory.md`,
17,990 chars ≈ **9,569 tokens**). The private full corpus (`frozen-private/`, tier A ≈24.5k tokens) **must not** enter
experiments — otherwise “frozen size → hit rate / cost” silently stops being comparable.

Four gates (any failure stops the run):

| # | Gate | Behaviour |
|---|---|---|
| ① | `benchmark.config.xml` → `corpus.lock.allowedSources` | must be exactly the three in-repo relative paths (no absolute, no `..`) |
| ② | `CorpusLock.Verify` at startup (also under `--plan`) | violation → **`exit 3`**, **before** the key is read or a request is sent |
| ③ | `-- lock-corpus --check` | fingerprint + token cap; `--update` re-locks |
| ④ | xUnit `CorpusLockTests` (4 tests) | allow-list / no private refs / token cap / fingerprint + privacy |

Standard action: **change corpus → `lock-corpus --update` → `dotnet test` → commit**.

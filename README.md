# Agent Runtime / WhiteBox · v9.0 (Alpha 0.1.5)

> 日本語: [README.ja.md](README.ja.md) · 中文: [README.zh-CN.md](README.zh-CN.md) · Release notes: [ALPHA-0.1.5-RELEASE.md](ALPHA-0.1.5-RELEASE.md)

**A single-session agent runtime that is transparent, economical and auditable — and can prove it.**

This is the comprehensive design and delivery report for the current implementation. It is not a diff against
an earlier version: it describes what the system *is* today, what it guarantees, and how every guarantee is
measured.

---

## Abstract

Most agent runtimes optimise for capability and leave three things unmeasured: what the model was actually
shown, what it cost, and why the runtime did what it did. WhiteBox treats those three as first-class
engineering objects.

A session is **one append-only event stream**. Every byte shown to the model is assembled from a **stable
prefix** (protocol → rules → knowledge L1 → decision L1 → memory index) plus that stream; the prefix is
byte-identical between turns, so the provider's prefix cache does the heavy lifting. Knowledge and decisions
are **two isomorphic four-layer trees** whose bodies are fetched by id and, within a session, assembled at most
once. Every action is a **tagged event**; every judgement is a **gate** with a negative example; every closeout
writes a **receipt** that can be recomputed.

The delivered result on the reference workload: protocol zone **25 lines / 5,234 tokens**; knowledge tree
**10 / 25 / 46 / 445** nodes; decision tree **4 / 8 / 12 / 16**; **1349 / 1349** internal tests green;
**99.1 %** prefix-cache hit over a real long session; a 423-turn task ledger compressed **45,888 → 16,599
characters (−64 %)**; closeout down to **≈35 s / 5 rounds**.

---

## 0. The original promise, delivered

The project started with one sentence: *one session, as transparent, as economical and as auditable as
possible.* This release closes that sentence.

| Promise | Mechanism | Reading (2026-10-30) |
|---|---|---|
| **Single session** | One process owns one stream; `/reset` is the only way to end it; `start` opens the next one from the previous end state | `--stream` points at exactly one file; resume/replay is positional, never a merge |
| **Transparent** | Append-only events; `Tag` is identity; the prompt is a computed object, not a template | 12 modules, per-module byte contributions visible in the console (`(Cached) / (Active) / (Runtime)`) |
| **Economical** | Resident = L1 only; L2–L4 fetched by id; one assembly per turn; stable prefix never rewritten | prefix cache **99.1 %** (296,225 prompt / 293,632 cached); protocol **5,234 tokens** of a 5,600 budget |
| **Auditable** | Tags, ledgers, gates with negative examples, closeout receipts, decision reports | **18** block/report checks; **242** declared pitfall placements; receipt fingerprint recomputable |

Everything below is the mechanism behind that table.

---

## 1. Why agent runtimes fail these four tests

1. **Transparency dies first.** Prompts are assembled ad hoc; nobody can say which bytes came from which
   source, or whether the "preview" the user saw equals what was sent.
2. **Cost becomes invisible.** A growing history is re-sent wholesale; the bill grows quadratically and the
   cache hit rate is never measured.
3. **Audit becomes archaeology.** The runtime's own decisions (fetch, prune, close out, promote knowledge)
   leave no durable, structured trace — only log lines.
4. **Sessions leak into each other.** Continuity is achieved by carrying everything forward, so each session
   is slower and less predictable than the last.

WhiteBox answers each with a mechanism, not a discipline.

---

## 2. Design invariants

| # | Invariant | Enforced by |
|---|---|---|
| I1 | The stable prefix is byte-identical between turns unless a version event is declared | prefix fingerprint + version ledger |
| I2 | History is append-only; nothing is rewritten or folded | no fold code path exists; window gate closes a session instead |
| I3 | One turn = one assembly; the object shown is the object sent | `Assert.Same(preview.Request, client.LastRequest)` |
| I4 | A node body is assembled at most once per session; later hits return a pointer | fetch-by-id + in-session dedupe |
| I5 | Every judgement has a negative example that turns it red | gate suites (18 runtime checks + 1,349 unit tests) |
| I6 | Nothing is silently skipped: bad input fails closed with a location | parser-level `InvalidDataException` with line numbers |

---

## 3. Architecture

### 3.1 Zones and byte accounting

The prompt is a stack of named regions, each with its own byte budget and lifetime:

| Zone | Contents | Lifetime |
|---|---|---|
| **R1-P** | Protocol zone (v102) — kernel constant, rank 0 | byte-stable until a declared version event |
| **R1** | Rules · knowledge L1 · decision L1 · memory index | stable prefix, re-derived on change only |
| **R2** | Append-only session event stream (the ledger of what happened) | grows by appending, never rewritten |
| **R3** | Focus — what this task is about right now | per turn |
| **R4 / R5** | Board / draft — the work surface carried across sessions | closeout-snapshotted, never silently dropped |

The console shows the real contributions of every module for the last request and the estimate for the next
one, clearly labelled — an estimate is never presented as a fact.

### 3.2 The event stream is the system of record

Every user message, model output, tool call, tool result, hint, decision, interrupt and closeout is one event
with a monotonic sequence and a tag (`E001`, `E002`, …). Position is derived from the stream; nothing outside
it is authoritative. Consequences:

- Crash analysis reads the tail of the stream (the last three events answer "what was it doing?").
- Cross-session handover is positional: `S<k>/E###` anchors, not copied prose.
- Any reading can be recomputed from the file, by anyone, later.

### 3.3 Two isomorphic trees

Knowledge ("what we know") and decisions ("why we chose this") are the same shape:

```
L1 law  →  L2 question shape  →  L3 handling unit  →  L4 verbatim evidence
```

- Exactly one parent per node; depth exactly four; each upper layer must *group* (one L1 covering one L2 is a
  failed abstraction and fails the chain gate).
- L1 is resident; L2–L4 are fetched by id. A node body enters the context once per session.
- L4 is the evidence layer: verbatim, append-only, each with a `src=` anchor back to the original file.
- Node ids are canonical (`L<layer>-<short[a-z0-9]{2,16}>-<seq 3 digits>`); a non-canonical marker is a hard
  parse error, not a skipped line.

Current reference corpus: knowledge **10 / 25 / 46 / 445** nodes (516 edges); decisions **4 / 8 / 12 / 16**.

### 3.4 Twelve modules, one assembly

| Module | Role | Typical bytes |
|---|---|---|
| protocol | kernel contract (R1-P) | ~20.9 k chars |
| rules | engineering rules | ~31 k chars |
| knowledge | L1 laws | ~5.6 k chars |
| decision | L1 orientations | ~1.5 k chars |
| memory-index | paged index; fetched on demand | 0 resident |
| append-stream | session history | grows |
| task-ledger | one line per turn: intent / result / terminal / open | ~0.3 k chars |
| tool | tool face (protocol zone) | 0 resident |
| note | per-turn scratch | 0 when unused |
| current-tail | board (R4) | small |
| dynamic-draft | draft (R5) | small |
| focus | R3 focus | small |

The console labels each module by the bytes it actually contributed this turn: `(Cached)` assembled into the
request, `(Active)` live store fetched on demand, `(Runtime)` mounted but contributing nothing. The label is
derived from the same assembly trace that produced the request — not from a second calculation.

### 3.5 Session lifecycle

```
closeout (converge + watermark + end-state snapshot)
   → reset (archive the old stream, session closed)
      → start (new empty stream, board/draft inherited from the snapshot)
```

- Closeout runs a fixed pipeline: `--check` → `--apply` → `--promote` → `--close` → `--check`, with
  `--l4-sync` moving new evidence into L4 and `--kn-plan/--kn-link` attaching it to the right parents.
- The pipeline is *mechanical*: moving text, bumping versions, re-deriving indexes. Semantic judgement belongs
  to the model; the runtime never guesses meaning.
- A closed session does not block the next one: on boot the runtime sees `closed` and starts a fresh stream,
  printing exactly what it inherited.

---

## 4. Mechanisms

### 4.1 Economy

- **Residency is a decision, not an accident.** Only L1 of each tree lives in the prefix; the rest is addressable.
- **One assembly per turn.** The preview object is what gets sent; proving it is a reference equality test.
- **Stable prefix.** No timestamps, counters or environment values may appear before the growing part; growth
  always goes last. That single rule is what keeps the cache hit near the theoretical maximum.
- **Window as a boundary, not a squeeze.** At 80 % of a 1,000,000-token window the session closes out and
  continues in a new one, instead of degrading silently.

### 4.2 Transparency

- **Every byte has an owner.** Zone stack, per-module contributions, cache hit rate, context total — all read
  from one assembly trace.
- **Two numbers that must not be conflated**: "this task wrote" (accumulated) and "current context" (last sent
  prompt). They are displayed separately and labelled.
- **Readings come from structured sources** (event kinds, ledgers), never from scraped human-readable text.

### 4.3 Auditability

- **Gates with teeth.** 18 runtime checks: prefix/version, corpus lock, ledger reconciliation, archive parity,
  unreviewed files, reachability preconditions, deletion registry, knowledge-tree chain (single parent / layer
  order / reachability / depth), L4 source anchors, decision-tree chain, decision-library promotion, growth
  since last release, and pitfall-placement completeness. Each block-level gate is proven by a negative example
  that must turn red.
- **Declared placement.** Every new pitfall must be declared as `gate / resident / human / todo` with a
  non-dangling pointer (242 declarations today; `todo` capped by an explicit baseline so debts cannot accumulate
  silently).
- **Receipts.** Closeout writes a receipt with a fingerprint; a changed workspace with an unchanged receipt is
  itself a red check.

### 4.4 Autonomy with stop points

- The decision library encodes *orientations* (L1) down to verbatim precedents (L4). When a decision is needed,
  the default is to **stop and ask** with one to three options, each with consequences, and a recommendation
  that cites the L1 it follows.
- Continuous work is bounded by **explicit stop points**: if the user names a stop, the runtime fills the
  intermediate decisions from the library and stops exactly there; if no stop is named, it asks for one first.
- This is why "autonomous" does not mean "unaccountable": every automatic choice points back to a library entry.

### 4.5 One console, three languages

- A single terminal surface (TUI2) replaced the earlier console entirely in v9.0; the old one is retired, not
  hidden behind a flag.
- The interface is a **strongly-typed string table**: 89 keys × English / 日本語 / 中文, English by default,
  switchable at runtime. A missing or misspelled key is a compile error, and a test asserts that no CJK can
  appear on an English screen.
- Diagnostics survive the removal: unhandled exceptions still write a crash record (stream tail + pointer +
  managed stack).

---

## 5. Evaluation (measured, not asserted)

All readings below were produced inside the published slice, in a fresh directory, on 2026-10-30.

| Metric | Value |
|---|---|
| Protocol zone | v102 · 25 lines · 20,933 chars · **5,234 tokens** (budget 5,600) |
| Knowledge tree | **10 / 25 / 46 / 445** nodes · 516 edges · chain gate green |
| Decision tree | **4 / 8 / 12 / 16** · 16/16 converged · 0 orphans |
| Tests (internal build) | **1349 / 1349** |
| Tests (public slice) | **1347 / 1349** — 2 internal-only gates (require the private pitfall corpus) |
| Prefix-cache hit (real 296 k-token session) | **99.1 %** (293,632 / 296,225) |
| Task ledger compression (423 turns) | **45,888 → 16,599 chars (−64 %)** |
| Closeout pipeline | **≈35 s / 5 rounds** (previously ≈9 min / 41) |
| Pre-publish scan | **0 hard findings** over 635 files |

Reproducing any row requires only the repository and the commands in §7.

---

## 6. What we deliberately do not do

- **No semantic guessing in the runtime.** If the model's structure cannot be parsed, the runtime reports the
  location and stops; it does not invent a plausible reading.
- **No silent degradation.** Missing resources produce `source_missing`; readings that cannot be taken say so
  instead of printing zero.
- **No unbounded context.** There is no folding or summarisation of history; the session boundary is explicit.
- **No hidden writes.** Every mutation of durable state goes through a named, greppable entry point, and the
  closeout receipt covers them.
- **No multi-user or parallel-session claims.** One session at a time is a design decision, not an oversight;
  parallelism is a separate research direction.

## 7. Reproducibility

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release          # 0 errors
dotnet test tests/AgentRuntime.Tests -c Release    # 1347/1349 (public slice)
dotnet src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll --protocol-show
dotnet src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll --kn-check whitebox/workspace
```

`--protocol-show` prints version, size and budget; `--kn-check` prints node counts per layer, edge count and a
byte-for-byte check of the derived layer. Same input, same reading.

## 8. Roadmap

1. **Throughput and latency** — systematic measurement first, then optimisation; this is the only reason the
   release is still a pre-release.
2. **Restore semantic coverage** after retiring the old console: decision-report binding, self-report display
   and continuation caps need equivalents on the new surface.
3. **Long-horizon studies** — many sessions of the same task, with the ledger as the auditable record.

## Boundary and licence

Private workspace, the internal pitfall corpus and training data are not part of the public slice.
Code: MIT (see `LICENSE`). Corpus: see `kernel/benchmark/AgentRuntime.Benchmark/corpus/NOTICE.md`.

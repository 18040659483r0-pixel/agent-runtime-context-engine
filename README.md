> 🌐 **English** | [中文](README.zh-CN.md)

# Agent Runtime / Context Engine Architecture (Design Note · Version 5)

> Date: 2026-09-25　Author: myself
> Version: **v5.0** (2026-09-25, **the interaction-contract layer**: frozen protocol zone · solve language & terminal states · the interaction unit (task lifecycle) · the decision report · the presentation layer · **a corrected approval model**); the previous v4.0 is preserved at [tag v4.0](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/tree/v4.0); v3.0 / v2.0 / v1.0 are preserved under their own tags.
> Implementation: the author's **self-owned Agent Runtime / Context Engine kernel** — frozen at v0.1.0 and **evolving since** (currently `0.10.x-dev`; the full test suite reports **685/685**) — ships source, tests and an independent measurement harness in this repository ([`kernel/`](kernel/)); not based on any existing agent framework.
> Series: Note I (*Hierarchical Hybrid Model Architecture*) covers model **form and layering** — a **separate layer** that this note does not alter; this is Note II, covering **runtime context and caching**, now in its **fifth version** (v3.0 added measured data; v4.0 consolidated the implemented context engine; **v5.0 adds the layer above it — the contract between the remote model, the runtime and the human — and reports no new measurements, see §7.7**).

---

## ① Core Proposition

Single-session usage has two wastes: the **context is constantly rebuilt** (summarize → delete → rebuild), losing history and destroying prefix stability; and **huge raw information is dumped straight into the top-level model**, drowning the part that actually needs judgment in noise.

This architecture insists on one line:

> **Do not rebuild, only append; do not touch physical history, only move attention.**

It requires three things at once:

1. **Freeze what is stable** — Knowledge / Rules / Memory Index are versioned independently and frozen into a Frozen Snapshot that forms a stable Prompt Prefix;
2. **Append everything that enters the Session** — append-only: no delete, no move, no reorder, no overwrite;
3. **Leave huge raw information to Workers / Sandbox** — context shrinks downward, information condenses upward.

Above these three stand one **boundary** and two **companion rules**:

- **Determinism gate**: **only content with "deterministic value" may enter the append-only region** — (1) a versioned knowledge base, (2) verified capabilities, (3) facts that have occurred; ideas still under discussion, unfinalized, or unverified have **non-deterministic value** and must be isolated in the lowermost **Dynamic Draft Region**, editable freely (§4.2).
- **Close-out gates promotion**: non-deterministic content **may never** enter the append-only region directly. Only after **full verification**, and **only at the session-tail close-out stage**, is it condensed into a **new version of the versioned knowledge base** — minimizing the probability of producing erroneous knowledge (§4.3).
- **Watermark tracks convergence**: an explicit **Close-out Watermark** must mark which knowledge and history have **already been condensed into a new knowledge-base version** and which are **not yet closed out**; even if a session fails to recover and close-out never runs, the system still knows "where the last close-out should have happened" and lets the user decide when to close out in a new session (§4.3).

A single session then gains two goals at once: **extreme token saving** (a stable prefix harvests the prompt cache, plus big data never reaches the top) and **maximal output efficiency** (the thread of thought never breaks, plus attention can refocus dynamically).

One-line definition:

> An **Agent Runtime / Context Engine** built on versioned knowledge and rules as a stable foundation, a Frozen Snapshot as a stable Prompt Prefix, an append-only Session Stream for context continuity, Semantic Focus to steer current attention, a layered Worker pipeline to isolate massive raw information, a lowermost fully dynamic region to hold undecided content, and a "close-out + watermark" mechanism as the sole path that condenses verified content into a new knowledge-base version.

---

## ② Architecture

### 2.1 Overall structure

```text
                              USER
                               │
                               ▼
                            MASTER
                               │
                     ┌─────────┴─────────┐
                     │                   │
              CONTEXT ENGINE           ROUTER
                     │                   │
       ┌─────────────┼─────────────┐     │
       ▼             ▼             ▼     │
   KNOWLEDGE       RULES         MEMORY  │
       │             │             │     │
       └─────────────┼─────────────┘     │
                     ▼                   │
              FROZEN SNAPSHOT            │
                     │                   │
                     ▼                   │
          SESSION APPEND STREAM          │
                     │                   │
              SEMANTIC FOCUS             │
                     │                   │
                CURRENT TAIL             │
                     │                   │
            DYNAMIC DRAFT REGION         │
                     │                   │
                     └──────────┬────────┘
                                ▼
                          WORKER ROUTER
                                │
                    ┌───────────┼───────────┐
                    ▼           ▼           ▼
               Task Worker  Specialist  Execution
                            Worker       Worker
                    │           │           │
                    └───────────┴───────────┘
                                │
                             SANDBOX
                                │
                       Raw Data / Tools
                                │
                                ▼
                         Condensed Result
                                │
                                ▼
                    SESSION APPEND STREAM
                                │
                                ▼
                              MASTER
```

### 2.2 Two orthogonal axes

**Vertical — Context Engine (how the context stays stable).** Top-down: the higher, the more deterministic; the lower, the more active.

```text
[FROZEN CONTEXT]          immutable   stable prefix        ← deterministic
       ↓
[SESSION APPEND STREAM]   append-only session event stream ← deterministic
       ↓
[SEMANTIC FOCUS]          mutable     current attention (no byte change)
       ↓
[CURRENT TAIL]            most active current working state
- - - - - - - - - - - - - - - - - - -  ← deterministic / non-deterministic divide
[DYNAMIC DRAFT REGION]    fully mutable  undecided ideas / draft requirements  ← non-deterministic (not append-only)
```

**The sole promotion exit — close-out:**

```text
[DYNAMIC DRAFT REGION]   non-deterministic, fully mutable
        │
        │   only at the session-tail close-out stage,
        │   and only for content that passed FULL verification
        ▼
  [knowledge base, new version v+1]   ← the only home of deterministic content
        │
        ▼
   advance the Close-out Watermark → marking the condensed / not-yet-closed boundary
```

The dynamic region sits **at the very tail**, so any add/change/delete happens only at the end of the queue and **never touches the stable prefix above the Cache Boundary**.

**Horizontal — Worker Pipeline (how information is processed).**

```text
Master → Task Worker → Specialist Worker → Execution Worker → Sandbox / Tools
```

The three worker tiers are divided not by model intelligence but by **information scale × decision responsibility**.

### 2.3 Physical layout: Frozen Context and Append Stream

```text
[FROZEN]
Global Domain   v10
Expert Domain   v21
Project Domain  v7
Global Rules    v8
Memory Index    v31

================ CACHE BOUNDARY ================

[SESSION APPEND STREAM]

001 [MEMORY]            a concrete event
002 [PROJECT_KNOWLEDGE] a project knowledge node
003 [PITFALL]           a concrete pitfall
004 [MEMORY]            another piece of history
005 [KNOWLEDGE]         a concrete knowledge item
006 [TOOL_RESULT]       a tool result
007 [WORKER_RESULT]     a worker analysis result

- - - - - - - - - - - - - - - - - - - - - - - - - - - - -
[DYNAMIC DRAFT REGION]      fully mutable; delete / rewrite / reorder allowed
  · ideas under discussion
  · draft functional-point / requirement descriptions
  · unverified hypotheses
```

**Physical order is not grouped by type**; the only ordering criterion is the actual append order into the session, and type is expressed by a stable tag.

---

## ③ Underlying Rules

### 3.1 Layers and responsibilities

| Layer / mechanism | Name | Mutability | Question / responsibility | Value type |
|---|---|---|---|---|
| **Frozen Context** | Knowledge + Rules + Memory Index | immutable (changed only at close-out, versioned) | "what to know / what to obey / what happened" | deterministic |
| **Session Append Stream** | session event stream | append-only | what has already happened in this session | deterministic |
| **Semantic Focus** | attention focus | dynamic (no byte change) | which existing events to attend to now | — |
| **Current Tail** | current tail | most active | current task / decision / pending / worker state | — |
| **Dynamic Draft Region** | fully dynamic region | **fully mutable (delete / rewrite / reorder)** | ideas under discussion / draft requirements / unverified hypotheses | **non-deterministic (exception)** |
| **Close-out** | close-out stage | event-driven (end of lifecycle) | the sole promotion point: verify → condense → version +1 → advance watermark | determinism gate |
| **Watermark** | close-out watermark | monotonic, persisted (runtime level, outside the session) | marks the condensed / not-yet-closed boundary | — |
| **Worker / Sandbox** | processing layer | stateless or disposable | search / parse / compute over massive raw data | — |

### 3.2 Core principles

| # | Principle | In one line |
|---|---|---|
| 1 | Stable Things Freeze | freeze what is stable |
| 2 | Session Context Is Append-only | **deterministic** content once in the session is appended only: no delete, move, reorder |
| 3 | Semantic Focus Is Mutable | attention may change; physical history may not |
| 4 | Huge Raw Data Stays in Workers | massive raw data stays in the Worker Sandbox |
| 5 | Context Shrinks Downward | context shrinks as tasks travel down |
| 6 | Information Condenses Upward | information condenses as results travel up |
| 7 | Runtime Owns State | the runtime owns state; the model is just compute |
| 8 | Recovery Is Deterministic | recovery relies on snapshot + stream position, not re-summarization |
| 9 | Versioning Creates Stability | versioned knowledge and rules give the frozen context its base |
| 10 | Complexity Must Be Earned | every added layer must pay for itself in tokens, speed, reliability, or capability |
| **11** | **Determinism Gates the Stream** | **only deterministic value may enter append-only / frozen; non-deterministic content stays in the lowermost dynamic region** |
| **12** | **Close-out Gates Promotion** | **non-deterministic content never enters append-only / frozen directly; only after full verification, and only at the session-tail close-out, is it condensed into a new knowledge-base version** |
| **13** | **Watermark Tracks Convergence** | **an explicit watermark marks what is condensed into the new version and what is not yet closed, and that record must survive across sessions** |

### 3.3 Separating and layering Knowledge and Rules

**Knowledge** answers "what to know" and is layered **Global → Expert → Project**; **Rules** answers "what to obey" and is layered the same way, with conflict priority **Global > Expert > Project**.

Project experience, once verified and abstracted, sediments upward: `Project → Expert → Global` (concrete project experience → professional methodology → general domain knowledge).

### 3.4 Separating Memory from Knowledge

Memory is not a knowledge base. Knowledge answers "what to know"; **Memory answers "what happened"** and is a timeline recording at least User Input / Agent Output / Tool Actions. **Long-term memory is an index, not full text** — you need not recall a whole life, only first know "what happened that day" and then locate the raw record (the index itself is versioned too).

---

## ④ Key Mechanisms

### 4.1 Session Context Append-Only Principle (the V2 core)

The session is redefined from "a replaceable dynamic context" into **an immutable event stream that may only be appended to**:

```text
Frozen Context        immutable
Session Append Stream append-only
content already in the session   no delete / move / reorder / overwrite
new content            append only
```

**But append-only is not unconditional.** It **protects only content of deterministic value** — (1) a versioned knowledge base, (2) verified capabilities, (3) facts that have occurred. Why this restriction is mandatory is §4.2.

Monotonic growth instead of cropping and rebuilding:

```text
A  →  A+B  →  A+B+C  →  A+B+C+D          (this architecture)
A+B+C  →  delete B  →  A+C                (conventional reclamation)
```

Minimal event structure:

```text
SessionEvent { event_id, sequence, timestamp, event_type, source, content_or_reference, metadata }
```

Key properties: unique event IDs, monotonically increasing sequence, **immutable events**, no reordering or overwriting, append-only.

**Why insist on append-only?** (strengthened here)

1. **Prefix stability** → bytes at the front of the queue stay constant, giving the prompt cache its best conditions (§4.4);
2. **Deterministic recovery** → position is state, no re-summarization needed (§4.7);
3. **Integrity and auditability** → history cannot be tampered with or lost, and the model's reading of "what happened" stops drifting.

All three benefits **rest on the premise that the content will never change again**. Break that premise (allow arbitrary deletion/editing) and all three collapse — which is exactly what the next section addresses.

### 4.2 The append-only exception: the fully dynamic region and the determinism gate

**Motivation (from software engineering).** A functional point / requirement is naturally written as "**describe once + augment incrementally**", which looks ideal for append-only: it is genuinely incremental and accumulated over time.

**But it has a fatal premise problem**: the user's description of a functional point **may be logically incomplete**. So it needs repeated discussion, completion, revision — sometimes a full reversal.

**Therefore it must not enter append-only.** Once inside, any deletion or edit would **break the integrity of the whole append-only queue** — and "stable prefix, recoverable, tamper-proof" is exactly what we are protecting. **Putting changeable content into an append-only region is tearing down that region's value by hand.**

**Design conclusion (the exception):** isolate such content into the lowermost **Dynamic Draft Region** of the vertical stack, which is **not append-only** and may be freely added to, changed, and deleted:

```text
Frozen Context           immutable          deterministic
Session Append Stream    append-only        deterministic
Semantic Focus           mutable (no bytes)
Current Tail             active
- - - - - - - - - - - - - - - - - - - -    deterministic / non-deterministic divide
Dynamic Draft Region     fully mutable      non-deterministic (the only region that may be edited)
```

**Why at the very bottom?** Because rewrites happen only at the **tail of the queue**, and **never touch the stable prefix above the Cache Boundary**. It thus satisfies two seemingly contradictory demands at once: free discussion and editing, without sacrificing prefix caching.

**Entry rule (Determinism Gate):**

| Content | Value type | Belongs to |
|---|---|---|
| versioned knowledge base | deterministic | frozen |
| verified capabilities | deterministic | frozen |
| facts that have occurred | deterministic | append-only |
| ideas under discussion, draft requirements, unverified hypotheses | **non-deterministic** | **dynamic region (editable)** |

**Promotion — close-out is the only exit:** dynamic-region content **may never** enter append-only directly, nor be frozen mid-session. Its **only exit** is: at the **session-tail close-out stage**, content that has passed **full verification** is **condensed into a new version of the knowledge base** (version + 1). **Content that fails verification is either discarded at close-out or left in the dynamic region for another round of discussion.** This minimizes the probability of erroneous knowledge — **knowledge is fixed only at close-out, and only after verification.**

> **In one line: append-only's value comes from determinism; non-deterministic content never enters it directly but passes the single "close-out + full verification" gate to become a new knowledge version.** This boundary is not a compromise — it is protection for append-only.

### 4.3 Close-out and the Watermark

**Close-out = the sole promotion stage.** It runs at the end of the session lifecycle, in four steps:

```text
(1) verify    run "full verification" on each dynamic-region candidate
              (logically complete? verified by evidence or practice?)
(2) condense  those that pass → written into a NEW version of the knowledge base (version + 1)
(3) version   record the new versions (Knowledge / Rules / Memory Index)
(4) reset     clear the dynamic region, advance the watermark, reset into the next Cell
```

**Watermark = the condensed / not-yet-closed boundary.** It records "where the last close-out should have happened":

```text
Watermark = {
    frozen_snapshot_id,     the frozen snapshot the last close-out was based on
    stream_cursor,          the session cursor (sequence) reached by condensation
    knowledge_versions,     knowledge-base versions after close-out
    timestamp
}
```

Crucially, the **watermark persists at the runtime level (outside the session)**, so **even if a session fails to recover, the watermark is never lost**.

**Pending close-out.** Events/candidates after the watermark = content still awaiting close-out. Its typical cause: **the user failed to recover a session**, so close-out could not run.

**New-session startup flow (handling pending close-out):**

```text
new session starts
      │
      ▼
read watermark → is there "not yet closed" content?
      │                           │
     no                          yes
      │                           ▼
  start normally      let the AI understand the pending content
                                 │
                                 ▼
      ask the user: "run the close-out now, or leave it to the end of this session?"
                          │                    │
                   close out now          leave it to the end
                          │                    │
                  run close-out now      record as pending; close out at session end
```

**Why a watermark is mandatory.** Close-out must not depend on whether the session ended normally — if recovery fails, close-out never triggers. **An explicit historical watermark keeps the system always knowing "where to resume condensing"**, without relying on model re-summarization (complementary to §4.7's recovery pointer: the recovery pointer locates *state*, the watermark locates *convergence progress*).

### 4.4 Stable Prefix and the Cache Boundary

For the whole session, the Frozen Context is the constant prefix:

```text
[FROZEN]
Global Domain vX · Expert Domain vY · Project Domain vZ
Global Rules vA · Expert Rules vB · Project Rules vC
Memory Index vD
================ CACHE BOUNDARY ================
```

Rules: **fixed field order, fixed serialization, no modification within the lifecycle, no dynamic content inserted ahead of it.**

This forms a stable Prompt Prefix **architecturally**, giving the provider's prompt cache its best conditions. **Caveat**: actual cache matching, TTL, billing, and cache granularity remain provider-defined (see §5.3).

### 4.5 Semantic Focus

Append-only does not mean the model must attend to all history equally. The physical context stays put; only a layer of **dynamic navigation** is added:

```text
Physical Context (never rewinds)   Semantic Focus (mutable)
E001 E002 E003 ... E238            ACTIVE FOCUS: E004 E007 E120 E201 E238
```

> **Physical context never rewinds; Semantic Focus may change dynamically.**

It is neither a second knowledge base nor a complex reordering mechanism — just "which existing events to look at now". **It moves attention only and changes no byte**, so it never conflicts with append-only.

### 4.6 Worker Context Funnel: Context ↓ and Information ↑

> **Context must not be copied downward with the task; it must shrink. Information must not be passed up raw; it must condense with each tier.**

```text
down:  Master → Task → Specialist → Execution → Sandbox      Context ↓
up:    Sandbox → Execution → Specialist → Task → Master       Information Density ↑
```

What eventually returns to the Master session is, preferentially: **key facts / key evidence / verification results / anomalies / conclusions / necessary references and file locations / open questions**, not massive raw data.

Worker tiers are **capability boundaries, not a mandatory pipeline**: the router may choose `Master → Execution`, `Master → Specialist → Execution`, or the full chain. The rule: **take one fewer tier whenever possible, but never sacrifice necessary decision capability to save tokens.**

### 4.7 Deterministic Recovery (record the position, don't re-summarize)

Under append-only, a snapshot only needs to record **position and state**:

```text
Frozen Snapshot ID · Session ID · Stream Cursor · Active Focus · Current Tail · Pending Operations · Worker State
```

e.g. `F001 + Stream Cursor E238 + Focus(E201,E207,E238)` recovers directly to `F001 + E001…E238 + Current State`, **without asking the AI to re-summarize**. Prefer full recovery via the **Context Snapshot**; if incomplete, fall back to **Frozen Snapshot + Session Append Stream + Memory**. The close-out watermark (§4.3) complements it: the recovery pointer locates *state*, the watermark locates *convergence progress*.

### 4.8 Runtime / Model Decoupling

Runtime state belongs to the runtime (Frozen Snapshot / Session Stream / Semantic Focus / Current Tail / Worker State / Context Snapshot / **Watermark**), **not to any LLM**:

```text
Model A  →  interrupt  →  Snapshot  →  Model B  →  continue
```

The model is merely **compute**, and is therefore replaceable by construction.

### 4.9 Difference from conventional context management

```text
conventional: Context → Task Change → Rebuild → Delete / Summarize → New Context
this work:    Frozen Prefix → Append Event → Append Event → Semantic Focus Change → Append Event
```

The root difference: **working state is managed not by frequently rebuilding the context, but by versioning, an append-only stream, and Semantic Focus**; the only place where "editing" is allowed is the fully dynamic region at the very tail, isolated for that purpose — and its only path to being fixed is the **close-out stage**.

### 4.10 Cost objective (a design model, not a measured result)

Combining Note I's "quantity" with Note II's "price" gives this architecture's **design objective** — it describes the intended direction, not a verified formula:

```text
minimize   C = T(A) · P₀ · [ 1 − (1 − w)·H ]

T(A) = T₀ · A^(−α)       token quantity: falls with abstraction/worker layers
P₀                      uncached token price
w                       hit-token discount ratio (provider-defined)
H                       prefix-cache hit ratio: Stable Prefix + Append-only drive it toward 1
```

- **Stable Prefix + Append-only** raise `H` and push the effective price `P_eff = P₀·[1−(1−w)·H]` down;
- the **Worker Context Funnel** pushes the `T` that reaches the top down;
- the two multiply in the same direction → cost is compressed twice.

### 4.11 Claim taxonomy (a V2 honesty requirement)

| Kind | Meaning | Examples here |
|---|---|---|
| **Architecture Principle** | definitional claim, prerequisite for the architecture to cohere | append-only, stable prefix, semantic focus, determinism gate, close-out gates promotion, watermark |
| **Engineering Design** | engineering choices | SessionEvent structure, snapshot fields, cache-boundary layout, close-out steps, watermark fields and storage |
| **Hypothesis** | assumption awaiting experiment | worker layering ⇒ fewer tokens without quality loss; close-out-gated promotion ⇒ lower erroneous-knowledge rate |
| **Experimental Result** | measured or public data | **first measured round (§4.12)**: 10k-token frozen context + stable-prefix appends ⇒ **98.4%** input hit, **−78.5%** per-turn cost; counter-example (dynamic content at the head) ⇒ **0%** hit, **4.7×** cost; hits align to **64-token blocks**; kernel overhead ≤1.3 ms. **v5.0 adds no new measurements — the interaction-contract layer of §⑦ is deliberately unmeasured in this version (§7.7)** |

### 4.12 First measured round (2026-09-14, new in v3.0)

> **Carrier**: the architecture's **minimal kernel v0.1.0** (shipped in this repo) plus an **independent measurement harness** — the ruler must not be part of the measured object, otherwise editing code also edits the ruler and the numbers lose meaning.
> **Fixed variables**: model/endpoint = DeepSeek V4 Flash (OpenAI-compatible); prices = input $0.14 / cacheRead $0.028 / output $0.28 per 1M tokens; frozen corpus = real engineering material (rules → knowledge → memory) sliced by character budget and **calibrated in tokens** to two tiers; 5 turns × three forms per tier; **variant isolation** (every variant's turn 1 shows `cache_hit = 0`, a cold-start check).

| Tier (frozen size) | Form | Input hits | Per-turn cost | Saving vs. fully uncached |
|---|---|---|---|---|
| L0 · 0 tokens (minimal-byte) | stable append | 0 | $0.000015–0.000034 | **0%** |
| L1 · 1,000 tokens | stable prefix + append | 896 (89%) | $0.000046–0.000055 | **≈67%** |
| L2 · 9,974 tokens | stable prefix + append | **9,856 (98.4%)** | $0.000299 | **78.5%** |
| L1／L2 · **counter-example** | **dynamic content at the head** | **0 (all five turns)** | L2: $0.001403–0.001417 | **0%** (**4.7×** cost) |
| L1／L2 · through the kernel | rules module + session module | **identical** to hand-rolled calls | same | same |

**Reproducible observations**

1. **Hit counts are always multiples of 64** (896 = 64×14, 9,856 = 64×154, 256 = 64×4) ⇒ the upstream prefix cache aligns to **64-token blocks**; prefixes shorter than that granularity can never hit.
2. The larger the frozen context, the **higher the saving** (0% → ≈67% → 78.5%) — consistent with the direction claimed in §4.4.
3. **Putting anything that changes every turn at the very front drives the benefit to zero** (counter-example: 0 hits across five turns, 4.7× cost) ⇒ "dynamic content must not lead" is upgraded from a design convention to a **cost-bearing engineering constraint**.
4. **Hits through the kernel are identical to hand-rolled calls** ⇒ the runtime **does not swallow** any cache benefit (positive evidence for §4.8).
5. Kernel overhead: **max 1.3 ms / mean 0.1 ms** over 55 real calls, negligible against model latency (≈0.5–2 s).
6. **Session control group**: removing the session module makes multi-turn follow-up tasks **fail outright**; token savings must therefore always be recorded alongside **task equivalence**, or we get "cheaper but not done".

**What this round does not prove (evidence boundary)**

- A **single provider / single model** only; cache granularity and discount `w` across providers are not compared (§5.3 item 2 still open).
- The frozen context is **statically injected**; the automatic close-out / watermark convergence of §4.3 is **not implemented** and therefore untested.
- Worker layering (the fall of `T`), snapshot recovery rate, and knowledge-error rate remain **unverified**.
- Raw records (recomputable): `kernel/benchmark/runs/20260914-183356-clean-ladder-r2/records.jsonl`.

---

## ⑤ Implementation Note

### 5.1 A minimal implementable runtime (prove the Context Engine itself first)

```text
1. Frozen Snapshot          6. Semantic Focus
2. Snapshot Version         7. Dynamic Draft Region
3. Stable Context Prefix    8. Close-out + Watermark
4. Session Append Stream    9. OpenAI-compatible API
5. Current Tail            10. Request / Cache logging + Context Snapshot / Recovery
```

Then add, one layer at a time: Knowledge Versioning → Rules → Memory Index → Router → Worker → Sandbox → UI.

> **Implementation status (v3.0)**: items 1–4 and 9 of the list above are implemented in **kernel v0.1.0** (`kernel/`: 54 fully offline tests, an independent measurement harness, and a freeze-criteria document); items 5–8 and 10 remain for later versions.

### 5.2 Engineering principles

> **Minimal box + modular + grow one layer at a time.**

Phase one should **not** include complex UI, multi-agent orchestration, automatic knowledge extraction, a complex router, a large memory, multi-model scheduling, or a giant code index. **The core novelty is proven first by `Context + Snapshot + Append-only + Close-out/Watermark + Recovery`.**

### 5.3 Verification plan and evidence boundary

The architecture still needs experimental validation, and **theoretical assumptions must not be written as experimental facts**. At minimum, verify:

1. ✅ **measured** (§4.12): the effect of append-only context on prompt-cache hit ratio
2. ⏳ **partially measured**: differences in caching behavior across providers (a **64-token block** granularity was observed on the DeepSeek side; cross-provider comparison still open)
3. token-cost comparison of an append-only stream vs conventional summary/compact
4. the effect of Semantic Focus on task completion rate
5. the effect of worker context shrinkage on token consumption
6. the effect of worker layering on total latency
7. the effect of worker layering on final answer quality
8. snapshot recovery success rate
9. task continuity after model switching
10. generality across task domains
11. **the correctness of pending-close-out detection / watermark advancement, and the real effect of "close-out-gated promotion" on the knowledge-error rate**
12. ⏳ **the interaction-contract layer (§⑦), all of it**: whether solve-language vocabulary + the three terminal blocks actually make "done / stuck / waiting / unsolvable" separable to the host, and whether automatic stop becomes safe **without** regressing task completion;
13. ⏳ whether a **conditionally mandatory decision report** improves the human's ability to decide the next step, and what it costs in extra rounds;
14. ⏳ whether the **card (task lifecycle)** presentation reduces reader effort compared with a raw turn transcript, measured as time-to-answer for a fixed set of questions;
15. ⏳ whether the **frozen protocol zone** can be held to a token budget across versions **without** ever paying more than one cold-start per protocol bump (the 4.6× counter-example is currently a single observation, not a distribution).

> **Boundary statement**: performance, cache hit ratios, and token-saving percentages must come from real experiments or public data and must not be invented from architectural intuition. This version claims only **architectural design and direction**.

### 5.4 Generality

What the architecture solves is the common problem of "**how to split a large problem into different information-processing responsibility tiers**", so it is not limited to coding agents: software engineering, financial analysis, quant, historical research, photography analysis, legal documents, research, data analysis, business research, and more.

### 5.5 Originality statement

The author **is building a self-owned Agent Runtime / Context Engine from scratch** (not based on any existing agent framework). **That kernel is now implemented and frozen at v0.1.0, with source, tests and an independent measurement harness shipped in this repository ([`kernel/`](kernel/)); the first measured round is in §4.12.** Later versions will close the remaining items of §5.3. Together, this note and the kernel form the original record: architecture (this README) + a runnable implementation + **recomputable measured evidence**.

---

## ⑥ v4.0 changes (architecture consolidation · 2026-09-17)

This version **adds no new claims**; it consolidates what is **already implemented** and states a few conventions explicitly.

### 6.1 Deterministic admission for the dynamic regions (Current Tail / Dynamic Draft / Semantic Focus)

Beyond the stable prefix, exactly **three controlled dynamic regions** are allowed — and their admission rules are **deterministic** (not “whatever the model feels like writing”):

| Region | Purpose | Admission rule (deterministic) |
|---|---|---|
| **Current Tail** (whiteboard) | Carry “latest state / todos of this task”, replacing “rewrite a summary” | Bounded; single write path; **append-only, never edits the front** |
| **Dynamic Draft** | Hold **pending** material (un-promoted knowledge / promises) | Enters only by explicit declaration; **must not** leak into the stable region |
| **Semantic Focus** | Pin attention to the events/passages relevant to this turn | **Derived** from event tags; recomputable and explainable; dangling tags **raise warnings** |

**Shared constraint**: dynamic regions are **never placed first** (§4.4 / the counter-example in §4.12); they move **attention**, never **physical history** (§①).

### 6.2 Tool face and approval gate (abstract)

> Deliberately abstract — mechanism detail is out of scope for this note.

- **Capability face**: the kernel ships a minimal tool set (file read / write / edit, directory listing, command execution); **same names, same semantics**, and **each tool can be switched off independently**.
- **Human in the loop**: any action that **changes local state** or is **irreversible** is **asked first, executed second**; **the model may not self-approve**; the default is **deny** (fail-closed) — no nod, no action.
- **Visibility**: the approval surface shows the **literal action that will run**; **one batch at a time, item by item**; authorization and execution are separated.
- **Not a sandbox (honest statement)**: the tool face **is not a security sandbox** — it cannot stop out-of-scope behaviour; safety rests on the **quality of the approval**, so it is not written up as a security boundary.
- **Observable**: every tool call and result enters the event stream and stays recomputable; the observation surface is **read-only**.

### 6.3 Observation console and its invariants

The runtime ships an **observation console**: conversation on the left, the **byte-exact context ledger** (per-region bytes / fingerprints / versions / order, plus this turn’s hits and overhead) on the right. Its value is **falsifiability**:

- **Read-only**: panels only render — they **do not change** a single byte sent to the model;
- **Not an injection source**: write actions can only trigger existing write paths; no new regions are produced;
- **Recomputable**: panel output is plain text — dumpable, diffable, usable as experiment records.

### 6.4 Capability layering and the corpus gate

- **Three capability layers**: resident (L1/L2 abstractions) → on-demand (L3 detail). **Only abstractions and indexes stay resident**; detail lives on disk and is loaded on demand — same motivation as §4.4 (do not let a bloating resident region eat the prefix-cache gain).
- **Corpus gate**: the public **experiment corpus** is a **de-privatised slice** of real engineering material (≈10k-token tier, character-budget slicing + token calibration); **the full corpus is never published** (a pre-publish scanner enforces hard red lines: private corpora / local paths / intranet addresses / credentials / file-sharing links).

### 6.5 Engineering status (abstract)

The rehearsal proceeds in **four stages**: **shadow mode** (run the same turn twice and compare the assembled context) → **double run** (same task set on both sides; criteria: **accuracy does not regress; size and hit rate must be explainable**) → **limited cut-over** (low-risk task types first, ticked off one by one) → **full cut-over** (with a rollback channel and a rehearsed rollback). **The first three stages have recomputable records; full cut-over has not happened.**

> **Measurement discipline**: platform-fixed overhead is **accounted separately** on each side and only the **shared segments** are compared; **quantities with different units are never placed side by side** (items / batches / calls must carry their unit, and old data is **never back-inferred**).

### 6.6 License

- **The kernel and the observation console (TUI) are MIT-licensed** (see `LICENSE` at the repository root): free to use, modify and redistribute, retaining the copyright and license notice.
- **Out of scope for open source**: the internal knowledge base, private corpora and internal engineering documents (they are not part of what this repository publishes).

### 6.7 Changelog

| Version | Date | Change |
|---|---|---|
| v1.0 | 2026-09-11 | Initial note (*Prefix-Cache-Optimal Layered Agent Architecture*) |
| v2.0 | 2026-09-14 | Append-only session stream + close-out / watermark + stable-prefix boundary |
| v3.0 | 2026-09-14 | **First measured round** added (64-token blocks; saving 0% → 78.5%; counter-example 4.7×) |
| **v4.0** | **2026-09-17** | **Architecture consolidation**: dynamic regions (§6.1) · tool face & approval gate (§6.2, abstract) · observation console (§6.3) · capability layering & corpus gate (§6.4) · engineering status (§6.5) · **MIT license** (§6.6) |
| **v5.0** | **2026-09-25** | **The interaction-contract layer**: frozen protocol zone (§7.1) · solve language & terminal states (§7.2) · interaction unit / task lifecycle (§7.3) · decision report (§7.4) · presentation layer (§7.5) · **approval model revised** (§7.6, a correction) · capability layers L1–L4 (§7.7) · **no new measurements** (§7.9) |

---

## ⑦ v5.0 changes: the interaction-contract layer (2026-09-25)

> **This version adds no measured results.** The architecture is still being optimised, so the experiments that would validate §⑦ are **deferred on purpose** — the honest statement is "designed, implemented in the kernel, not yet measured" (§7.9). What v5.0 adds is the layer v4.0 lacked: **how the remote model, the runtime and the human talk to each other**, and how that conversation stays *inside* the cache discipline of §4.4 instead of breaking it.

### 7.0 Why a contract layer at all

§①②③④ describe how context is *stored*; §6.2 sketched the *tool face*. Both leave one question open: the model is a **remote, stateless, replaceable** compute resource (§4.8). Everything the architecture needs from it — how to read context, how to report state, how to say "I am done" — must therefore be written down **somewhere the model always sees**, and must never drift per session.

That "somewhere" cannot be the user's corpus, and cannot be a close-out target:

```text
the contract has three properties that force it to live in exactly one place:
  1. fixed          it does not evolve with the session   ⇒ belongs to the frozen region
  2. architectural  it is the interface the kernel owns  ⇒ the user may not change it
  3. minimal        it sits at rank 0, i.e. at the very front of the cached prefix
                    ⇒ any change to it zeroes the whole cache (measured: 4.6×)
```

⇒ **one home for the contract** — the same discipline as "one home for the index".

### 7.1 The frozen protocol zone (R1-P)

Definition (a two-question test, applied to every candidate line):

> **Necessity**: without it, can the remote model still use this runtime *correctly*?
> **Ownership**: does it change **only with the kernel version** (neither the user nor the close-out may touch it)?

Content that fails either question is **corpus**, not contract: persona, tone, output style, report templates and glossaries belong to the *rules* region (user-editable, close-out-promotable); business knowledge belongs to *knowledge*; history belongs to the *memory index*.

Placement, inside the frozen region:

```text
R1 Frozen Region
  ├─ protocol            architectural · immutable · never domain-filtered   ← rank 0 (first)
  ├─ global / expert / project domains
  ├─ rules
  └─ memory index
================= CACHE BOUNDARY =================
R2 Session Append Stream … (§4.3)

three hard properties, each backed by an executable gate:

  1. source is code only        no configuration entry point, no domain switch  → type-level impossibility
  2. close-out cannot touch it  the promotion whitelist excludes it             → throws
  3. the user cannot detach it  --bare / --modules cannot remove it             → errors
```

**Budget.** The zone is budgeted **in tokens, measured before it is fixed**, and grows only when a real defect demands it:

| protocol version | budget (tokens) | measured | what forced the change |
|---|---|---|---|
| v5 | ≤280 | 258 | zone created (L1/L2/L3 usage + rules + memory index) |
| v9 | ≤600 | 598 | solve language + three terminal states + close-out / reset |
| v12 | ≤900 | 865 | `risk:` declaration on every tool call |
| v16 | ≤1,100 | 1,035 | the `[TOOL]` shape written down literally, with an example |
| v20 | ≤1,500 | 1,390 | two negative clauses ("no terminal block unless done"; "asking a human *is* a terminal state") |
| **v22** | **≤1,900** | **1,698** | decision-report clause + a `body:` slot |

**Discipline the zone enforces on itself:** it says only *how to talk*, never *how to be*; a gate **byte-compares** the human-readable copy of the text against the code constant, so the copy can never drift silently; and the version number is recorded in the manifest but **never enters the prompt** (a version is bookkeeping, not content).

### 7.2 Solve language and the three terminal states

Two concrete gaps motivated a shared vocabulary:

| Gap | Symptom | Cost |
|---|---|---|
| **no word for "finished"** | the only stop signal was "it did not call a tool this round", so **done / stuck / waiting-for-a-human / unsolvable looked identical to the host** | the host dare not stop by itself; the human cannot tell "it is done" from "it ran out of words" |
| **no action for "this session should end"** | history only grows, so the prompt gets longer and the cache more expensive; "close-out" existed only as an offline command | long tasks carry all history forever; changing session means losing the whiteboard and the draft |

The fix is one vocabulary plus two exits, carried **inside the protocol zone** so the model always sees it:

```text
vocabulary (also the first two lines of the [TAIL] whiteboard)
    Equation · Known Conditions · Solution · Solve Step · Solution Set · Intervention
    [TAIL]  solve: <the current solution, in one line>
            step:  <which step of it we are on>

exits (three independent terminal blocks — the host may now stop by itself)
    [DONE]         finished, plus what was produced
    [NO-SOLUTION]  *proved* that this route cannot work (not "not finished this round")
    [NEED-USER]    a decision / a fact / a resource is required from the human

lifecycle actions
    close-out   condense verified work into a new knowledge version, then reset
    reset       start a fresh, empty event stream (whiteboard + draft are inherited by key)
    proposal    context ≥ 20% of the window and the task is closed ⇒ propose close-out + reset
```

Defining `[NO-SOLUTION]` as *proved impossible* rather than *not yet done* is not pedantry: a real run wrote `[NO-SOLUTION] round not ending — evidence incomplete, continuing`, the runtime believed it, and a live task was frozen as *unsolvable*. **A negative claim needs a precise scope, because the host acts on it.**

### 7.3 The interaction unit: a turn is a unit of computation, not of interaction

> **The user observes a lifecycle; the runtime executes turns; the trace records everything.**

```text
user message
    │
    ▼
TaskLifecycle            ← the interaction boundary (one request = one card)
    │  ├─ turn  ├─ turn  ├─ turn …        ← internal computation, not shown as messages
    ▼
  card: created → updated in place → frozen at closure
```

- one request = **one card** for the user, however many turns the runtime needed internally;
- the card is **updated in place** while the task runs and **frozen** when it closes, so the reader sees a task, not a turn-by-turn log scroll;
- a task that needs a human decision becomes an explicit **decision card**, and the answer comes back through an explicit `/decide` path — never inferred from free text.

The deeper point is a boundary correction: **exposing the *execution* layer as the *interaction* layer is what makes an agent transcript unreadable.** Splitting them lets the runtime be as chatty or as quiet as it likes internally without changing what the human reads.

### 7.4 The decision report (lifecycle brief)

Closure is not just "the task stopped". A lifecycle **owes the human a report**: what was asked, what was done, what it cost, what remains, and what the next step could be. In v5.0 this is **conditionally mandatory**:

```text
terminal block lands
      │
      ▼
host requests the report exactly once (a Hint in the stream — no new event kind)
      │
      ▼
the model writes it as prose (an article, not raw material)
      │
      ▼
the card renders it *below* the lifecycle block; the card itself is never rewritten
```

Three properties make it cheap rather than ceremonial:

- **it is a projection of the stream** — replaying a recorded stream re-renders the same report without re-running the task;
- **it may arrive in the same turn as the terminal block**, and the binding accepts either route (the terminal turn itself, or the turn after the host's request) — a rule that allowed only one route silently lost real reports;
- **one request per closure, not one per process** — the bookkeeping is cleared by "a non-terminal turn means work started again", not by session teardown.

### 7.5 The presentation layer: semantics / style / bytes

> The producer says **what a thing is**; only the layer that puts it on screen decides how it looks; the plain-text frame **does not change by a single byte**.

```text
producer (model text / panel rows / tool rows)   "what is this"      → roles, not colours
        │
        ▼
presentation IR (new)   RichText = text (markup stripped) + role spans
        │
        ▼
renderer (the only place that emits SGR)   plain == rich, byte for byte in text
```

This is the same split a chat channel uses: the model emits *semantics* (markdown), the channel's renderer owns *style*. Two invariants are gated: **markup is stripped before any width computation** (or alignment breaks), and **the plain render equals the rich render** once styling is removed (so a colour decision can never change what the machine reads).

### 7.6 The approval model, revised — an honest correction to §6.2

§6.2 (v4.0) described the tool face as *ask first, execute second*, with an approval surface and a fail-closed default. **v5.0 revises that — and the revision is reported as a correction, not as a move in the same direction.**

| | v4.0 / v12 | **v13 (current)** |
|---|---|---|
| where risk is judged | the runtime classifies, and may ask a human in-task | **the model judges** and declares `risk:` on every tool call |
| in-task gate | confirmation window / item-by-item y-N | **removed** — the runtime classifies, records, and **runs** |
| what safety rests on | gate quality | **the remote model's discipline** (protocol clauses) + the runtime's **hard red lines** (protected paths, credentials, publish / irreversible) |
| record | approval log | **record-and-run**; the record is not a nod |

The clause that carries it says, in effect: declare the risk with every call; write `none` only when you judge that nothing can be harmed, and hand everything else — hesitation included — to the human **before the terminal block**, in plain language. The runtime still refuses its hard red lines, and a false declaration is recorded.

**Honest statement**: this trades a mechanism for a discipline. It is what the author wants (a gate that interrupts inside every task was judged more harmful than useful in real use), but it must **not** be read as "the runtime is now a security boundary". It is not, and this note does not claim it is. §6.2's sentence "not a sandbox" stands; only its mechanism description is superseded.

### 7.7 Capability layers L1–L4 (extends §6.4)

§6.4 described three layers. The fourth is where a capability's own heavy material lives:

| layer | content | residence |
|---|---|---|
| L1 | the abstract law ("which class of problem this capability solves") | resident |
| L2 | problem → layer-3 pointers (one-to-many) | resident |
| L3 | one problem → how to handle it (steps / commands / criteria) | on demand |
| **L4** | a capability's **heavy internal knowledge** (the material behind a step) | on demand |

L3 and L4 share one shape (`<capability>/L<N>.jsonl` — **the line number is the address**), and the human-readable `SKILL.md` **degrades to a read-only export** that can still be regenerated for compatibility. The point is unchanged from §4.4: **only abstractions and indexes stay resident**; everything heavy stays on disk and is fetched by address.

### 7.8 Engineering status (2026-09-25, abstract)

- **Protocol zone v22** — 13 lines / 6,792 characters / 1,698 tokens (budget ≤1,900), rank 0, gated three ways (§7.1).
- **Kernel** — `0.10.x-dev` on top of the frozen v0.1.0; full suite **685/685**; build 0 warnings / 0 errors.
- **Interaction layer implemented** — solve language + terminal states · cards · decision reports · single-column console · presentation layer · L1–L4 capability layers.
- **Rehearsal (unchanged from v4.0)** — shadow mode · double run · limited cut-over have recomputable records; **full cut-over has not happened**.
- **Close-out** — three tiers (per-task · per-session · reset) with a self-check pipeline; the runtime can run its own close-out, including its own test gates.

### 7.9 Evidence boundary for this version

**What v5.0 claims**: architecture and engineering design (the claim taxonomy of §4.11 still applies).
**What v5.0 does not claim**: any *new* measured effect. §⑦ is an **unmeasured design layer** — the interaction-contract layer was built because the author needed it daily, not because it was measured to be better. Items 12–15 of §5.3 are the measurements that would settle it, and they are **deferred until the architecture stops moving**, so that the numbers describe a stable object.

> Deliberate statement, so that no reader has to guess: **this version ships code and design, not results.** The only measured numbers in this note remain the first round of §4.12; they describe the append-only context engine, not §⑦.

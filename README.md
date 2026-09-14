> 🌐 **English** | [中文](README.zh-CN.md)

# Agent Runtime / Context Engine Architecture (Design Note · Version 3)

> Date: 2026-09-14　Author: myself
> Version: **v3.0** (2026-09-14, first measured results included); the previous v2.0 is preserved at [tag v2.0](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/tree/v2.0); the initial v1.0 (2026-09-11, *Prefix-Cache-Optimal Layered Agent Architecture*) is preserved at [tag v1.0](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/tree/v1.0).
> Implementation: the author's **self-owned Agent Runtime / Context Engine kernel is implemented and frozen at v0.1.0**; source, tests and an independent measurement harness ship in this repository ([`kernel/`](kernel/)); not based on any existing agent framework.
> Series: Note I (*Hierarchical Hybrid Model Architecture*) covers model **form and layering** — a **separate layer** that this note does not alter; this is Note II, covering **runtime context and caching**, now in its **third version** (first version with measured data).

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
| **Experimental Result** | measured or public data | **first measured round (§4.12)**: 10k-token frozen context + stable-prefix appends ⇒ **98.4%** input hit, **−78.5%** per-turn cost; counter-example (dynamic content at the head) ⇒ **0%** hit, **4.7×** cost; hits align to **64-token blocks**; kernel overhead ≤1.3 ms |

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

> **Boundary statement**: performance, cache hit ratios, and token-saving percentages must come from real experiments or public data and must not be invented from architectural intuition. This version claims only **architectural design and direction**.

### 5.4 Generality

What the architecture solves is the common problem of "**how to split a large problem into different information-processing responsibility tiers**", so it is not limited to coding agents: software engineering, financial analysis, quant, historical research, photography analysis, legal documents, research, data analysis, business research, and more.

### 5.5 Originality statement

The author **is building a self-owned Agent Runtime / Context Engine from scratch** (not based on any existing agent framework). **That kernel is now implemented and frozen at v0.1.0, with source, tests and an independent measurement harness shipped in this repository ([`kernel/`](kernel/)); the first measured round is in §4.12.** Later versions will close the remaining items of §5.3. Together, this note and the kernel form the original record: architecture (this README) + a runnable implementation + **recomputable measured evidence**.

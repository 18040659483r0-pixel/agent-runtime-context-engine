# Agent Runtime / Context Engine · Alpha 0.1

> A deployable, measurable, fully customizable agent runtime.
> This document describes the **stable design of Alpha 0.1**; it intentionally omits the project's evolution history.

## Abstract

Agent Runtime turns model collaboration into two engineering problems:

1. **Context = a stable prefix + an append-only event stream.** The prefix is byte-stable and cacheable; history is append-only and never rewritten.
2. **Knowledge and decisions = two isomorphic four-layer trees.** Only L1 is resident; L2–L4 are fetched by id; within a session, a node's body is assembled at most once.

The runtime performs **mechanical work only** — assembly, validation, accounting, fetching, closeout. Semantic judgement belongs to the model. Every reading is reproducible.

## 1. Goals

- **Cacheable**: no time, randomness, or environment in the stable prefix; one changed byte is a version event.
- **Auditable**: append-only events; tags are identity, positions live only in the ledger.
- **Explainable**: knowledge and decisions are trees with ids, layers, and edges; fetches and prunes are reviewable.
- **Deployable**: bare baseline + CLI; customization is plain text, no code changes.
- **Reproducible**: same input ⇒ same bytes; same command ⇒ same reading.

## 2. Architecture

### 2.1 Context layers

| Zone | Content | Nature |
|---|---|---|
| **R1-P** | Protocol (v74: 15 lines / 10,701 chars / 2,676 tokens) | Kernel constant, Rank 0 |
| **R1** | Rules · knowledge L1 · decision L1 · memory index | Stable prefix, byte-reproducible |
| **R2** | Event stream (`E###`, append-only) | History and tool results |
| **R3** | Focus (event ids relevant to this turn) | Dynamic |
| **R4** | Whiteboard (current solution/step) | Dynamic |
| **R5** | Draft (open items) | Dynamic |

Assembly order: **R1-P → R1 → R2 → R4 → R5 → R3**. Focus comes last so dynamic content never front-loads the prefix.

### 2.2 Knowledge tree (L1–L4)

```
L1 law → L2 question shape → L3 unit → L4 evidence
```

- Single marker: `<!--kn: <id> [next=<id,id>] [src=<path>]-->`
- id: `L<layer>-<short>-<nnn>`; the layer is encoded in the id.
- **Only L1 is resident**; L2–L4 are fetched by the model with `[KN] <id>`.
- **Session dedupe**: first fetch assembles the body; a second fetch returns only a `kn-ref` pointer (first event tag + sequence).
- Derived files (`l1.xml` / `nodes.xml` / `summary.xml`) are recomputed from the live document and byte-deterministic; structural checks (edges, orphans, seq) are re-runnable.

### 2.3 Decision library

Isomorphic to knowledge: `L1 orientation → L2 decision shape → L3 decision unit → L4 verbatim evidence`.

- Decision L1 enters frozen R1, ordered after knowledge L1.
- `USER-DECISION-ENTRIES.md` collects raw decisions; closeout converges them into `DECISION-LIBRARY.md`.
- `PROJECT-DESIGN-PRINCIPLES.md` is a live project document, read on demand, never resident.
- The optional bypass module may decide for the user only when **both** the decision library and the principles are satisfied; otherwise it stops and asks.

### 2.4 Tool face

One call = **one XML element**:

```text
[TOOL] <call><name>read</name><path>/abs/file</path></call>
```

- Members are taken by name, order-independent; multi-line bodies go verbatim inside the element, without escaping.
- Each call may carry a `risk:` claim; the runtime classifies, records, and executes — it does not ask inside a task.
- Calls beyond the per-reply pace are **queued**, never rejected or lost.
- Shape normalization first: `<TOOL>`, bare `<call>`, and native-marker residue are normalized to the protocol shape; examples inside fenced blocks are not calls.

### 2.5 Closeout

Closeout is an **extensible module**: one declaration per requirement, split into Core and Extension.

- Core red = record and warn, **do not block** by default; Extension red is accounted only.
- The convergence point records archive revision, code revision, pitfall counts, live↔archive parity, and a fingerprint.
- A green closeout removes the previous red note; a red closeout leaves its name both in the convergence line and in the ledger.

## 3. Deployment

The deployment version is a **bare baseline**: 20 knowledge seeds (5 per layer) and 3 decision seeds.

```bash
./deploy.sh init ~/MyAgent
./deploy.sh provider --target ~/MyAgent --base-url https://api.deepseek.com \
  --model deepseek-v4-flash --api-key-env DEEPSEEK_API_KEY
./deploy.sh doctor  --target ~/MyAgent
./deploy.sh check   --target ~/MyAgent
./deploy.sh reset   --target ~/MyAgent --all
./deploy.sh run     --target ~/MyAgent [--cli]
```

- **Paths**: the package root is found by walking up to `config.xml`; the internal layout is fixed; relative paths in the config resolve against the config directory; the whole directory can be moved.
- **Provider**: only `BaseUrl` / `Model` / `ApiKeyEnv` / `ApiKeyFile` / `ContextWindow`; secrets live in env vars or key files only.
- **Customization**: rules `AGENTS.md`, persona `SOUL.md`/`IDENTITY.md`, user `USER.md`, knowledge `knowledge.md`, decisions, project principles — six plain-text files; changing them changes the world.

## 4. Evaluation

### 4.1 Regression

- Unit/integration tests: **1339 / 1339**.

### 4.2 Single-master performance suite (M1–M10)

| Metric | Reading |
|---|---|
| Tasks | **10 / 10 solved** |
| Dispatches | **0** |
| Rounds / tools | **81 / 46** |
| Total wall time | **267.6 s** |
| Tool-shape rejects | **4** (four distinct shapes, all self-corrected; no shape repeated) |

Coverage: verbatim read, locate/list, precise edit, create artifact, run command, reading recomputation, bug fix, decision report, size-aware scheduling, closeout artifacts.

### 4.3 Closeout suite

- Mechanical A1: `--check` (Core 0) → `--close` writes convergence → final **Core 0 / report 0**.
- Real-machine three stages: `/closeout` backlog → `/reset --start` → `/closeout` clean; knowledge tree rebuilt (20 nodes / 15 edges); no tool rejects, no silent exit.

### 4.4 Deployment suite

8 / 8: package integrity, `init`, official DeepSeek provider, `doctor` + `check`, real TUI, fetch dedupe, `reset`, whole-directory relocation.

- Real-machine stage: **2 rounds / 1 tool / 0 rejects / 5 s**, output contains `L1-…` and `[DONE]`.

### 4.5 SWE-bench Lite

Three real instances, run with the official harness + Docker and the official DeepSeek `deepseek-v4-flash`:

| Instance | Result |
|---|---|
| `psf__requests-1963` (preserve method across redirects) | **resolved 1/1** |
| `pallets__flask-4045` (reject dotted blueprint/endpoint names) | **resolved 1/1** |
| `pytest-dev__pytest-11143` (numeric literal is not a docstring) | **resolved 1/1** |

**3 / 3 resolved.** A gold-patch sanity run is also resolved, confirming the container environment works.
This is a **live subset**, not the pass rate of the full 300 instances.

## 5. Reproducibility

- Package: `agentruntime-alpha-0.1-macos-arm64.zip`
- sha256: `6ad972dfadb49ca5151072dcf6f3abcedf000da8bd5dc5bbc68cef713cfd1981`
- Environment: macOS arm64 · .NET 10 · official DeepSeek (`deepseek-v4-flash`)
- Pre-publish scan: **0 hard red lines**

## 6. Alpha boundaries

- The **uplink curation line** (L4→L3→L2→L1) is not automatic yet; during alpha the tree is maintained by hand.
- The 80% auto-closeout uses `ContextWindow = 128,000` as an assumption; adjust it to the model.
- SWE-bench is a small live subset, not the full 300-instance run.
- Only a macOS arm64 build is published; other platforms require `dotnet publish`.

## License

- Code and runtime: **MIT**.
- Knowledge/decision seeds: **data, not covered by MIT**; running, reproducing, and evaluating with the repository is allowed; redistribution, commercial use, and training require contacting the author.

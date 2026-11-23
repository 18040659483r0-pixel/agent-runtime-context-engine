# Agent Runtime

> English | [中文](README.zh-CN.md)

> **Version 0.1.0 (kernel freeze, 2026-09-14)** — freeze criteria, frozen items and measured evidence: `docs/FREEZE-0.1.0.md`.
> Change log: `CHANGELOG.md`

> **A from-scratch Agent Runtime / Context Engine kernel** (the engineering companion to the second paper).
> Current stage: **V0 minimal loop + V1 module pipeline**.

## V0 does exactly four things

```
User → Runtime → Model API → Response → Runtime → User
```

1. Accept one user message
2. Assemble a minimal request
3. Call an OpenAI-compatible API
4. Return the model output unchanged

**Explicitly out of scope:** knowledge, memory, router, worker, cache tuning, database, persistence,
web, UI, DI container. Each of these arrives layer by layer on the roadmap below, and every layer can be tested on its own.

## Module pipeline (hot-swappable; bare chat is always available)

```
user input
   │
   ├─ module 1 (config order) → contributes messages (system / history / memory / knowledge …)
   ├─ module 2
   │
   └─ engine appends “this turn’s user message” (always last)
        │
        └─ IModelClient → upstream API → response
   └─ each module observes (e.g. writes session history)
```

**Three hard constraints** (each guarded by tests):

1. **Bare chat always exists**: with `--bare` (or `modules: []`) behaviour is **byte-identical to V0**.
2. **Modules only add**: a module cannot rewrite engine behaviour; remove any one and the others stay **byte-identical** (the premise of ablation).
3. **Switches live in config only**: the `modules` array in `config.xml`; order = contribution order in the prompt; `--modules a,b` overrides.

| Module | What it does |
|---|---|
| `session` | Multi-turn: puts past turns into the prompt and records this turn (V1, in-memory) |
| `append-stream` | **Session event stream** (V3): append-only events (JSONL on disk); survives process exit and **resumes across processes**; also the home of **one-at-a-time L3 loading** (skills / pitfalls) |
| `system-rules` | Top-of-prompt rules / persona: injects a fixed system text (the smallest hot-swap example) |
| `rules` | **Rules zone** (V2): Global → Expert[selected domains] → Project, filtered by domain |
| `knowledge` | **Knowledge zone** (V2): same three layers |
| `memory-index` | **Memory index zone** (V2): one layer (Global), an index rather than full text |
| `focus` | **Semantic focus** (V4.1, R3): compresses “what this turn should care about” into one line `[FOCUS] E208 E216`; navigates, never edits history; must sit **after** `append-stream` (third gate) |
| `current-tail` | **Current tail** (V4.2, R4): persists the “what am I doing now” whiteboard (`[TAIL]` + lines) **locally in the Runtime**; the model’s self-report overwrites the store, so a restart resumes; must sit **after** `append-stream`, **before** `dynamic-draft` (third gate); config keeps only `storePath` (limits and format live in the **protocol zone**) |
| `dynamic-draft` | **Dynamic draft zone** (V4.3, R5): the **only** home for ideas under discussion / unsettled requirements / unverified assumptions (`[DRAFT]` + lines); the **only region allowed wholesale edits** (but with a **single write entry: whole-block overwrite** — no remove/move/patch); must sit **after** `current-tail`, **before** `focus` (third gate); config keeps only `storePath` |
| `tool` | **Tool surface** (V7, G1/G2): the model writes `[TOOL] name {"key":"value"}` (shape fixed at v18: **keys quoted**, risk on the same line outside the JSON) → Runtime parses → **approval gate** → execute → the result returns as an **event** (`[TOOLRESULT]` / `[TOOLDENIED]`, line numbers are addresses and replayable); minimal set `read`/`list`/`write`/`edit`/`exec`; **read-only needs no approval, everything else does (one approval, one action), undecidable ⇒ deny (fail-closed), the model can never self-approve**; **zero injection** (no prompt bytes change) ⇒ must sit after `append-stream` and before `current-tail` (ordered as an R2-class region); it requires an event sink (tool without `append-stream` fails at assembly). Spec: `docs/DESIGN-TOOL-FACE.md` |
| `protocol` | **Protocol zone** (R1-P, architectural): the minimal interaction contract between the remote AI and the local Runtime (layered read order, fetch-by-id, and the `[FOCUS]` / `[TAIL]` / `[DRAFT]` self-report formats and limits); since v5 it also carries L1/L2/L3 usage, the rules zone and the memory-index contract; it sits **first** in the frozen zone, is **never domain-filtered**, **cannot be changed at closeout**, and **cannot be removed by the user**; budget **≤8 lines / ≤280 tokens** (measured 6 lines / 1,029 chars / 258 tokens; see `docs/REPORT-PROTOCOL-ZONE.md` §4 P4) |

A new module touches two places only: `AgentRuntime.Modules/ModuleRegistry.cs` + `RuntimeConfiguration.KnownModules`.

### Frozen zone (V2, in progress) — a stable prefix organised as zone / layer / domain

The **stable prefix** above the cache boundary is made of three “zone” modules, all deriving from `FrozenZoneModuleBase` (subclasses only answer “which slots does my zone have”):

```
① rules.global     → rules.expert[domains sorted by id]     → rules.project      ← absolute top (constraints)
② knowledge.global → knowledge.expert[domains sorted by id] → knowledge.project  ← content layer, parallel
③ memoryindex.global                                                            ← content layer, parallel
```

> **Hierarchy is visible in the types**: `RulesModule` derives from `FrozenZoneModuleBase` (top); `KnowledgeModule` / `MemoryIndexModule` share `ContentZoneModuleBase` (parallel content layer). See `docs/DESIGN-OO.md` (shipped with every delivery/deployment).

- **Source of content**: `config.frozen.root` points at the on-disk corpus (relative paths resolve against the **config file’s directory**); it maps `rules/global.md`, `rules/expert/<domain>.md`, `knowledge/global.md`, `memory/index.md`, … Each section’s version lives in its header `<!-- frozen: version=N -->` (**never in the prompt**); without a header, a hash of the body is used. **A missing file means the section does not exist** (that is what makes domain switching save tokens).
- **Two corpora, two homes**:
  - **Product corpus** `frozen/` (**clean, shipped with the source**): the minimum needed to teach a user how to use the software;
  - **Measured corpus** `benchmark/AgentRuntime.Benchmark/corpus/` (**de-identified, publishable**): real slices so others can reproduce results.
- **Domains are chosen before launch** (`--domains a,b` or `config.frozen.domains`; **empty = load all**): a software-engineering session does not load finance knowledge, so irrelevant material stays out of the prompt.
- **Determinism gate**: at assembly the frozen zone is placed **wholesale first** (`DeterminismGate`) — a dynamic module (e.g. `session`) is never allowed ahead of the stable prefix (otherwise the whole cache dies; measured cost: 0% hit, 4.7× the price).
- **Global fingerprint + version ledger**: `FrozenPrefix.Assemble` yields a byte fingerprint of the whole prefix; `FrozenManifest` records “which section is which version”, so a fingerprint change can be diffed down to the exact sections (the ledger never enters the prompt).
- **Closeout and watermark** (paper §4.3, minimal form): `--closeout` verifies the current prefix → reports pending misc items → writes the watermark (**Runtime level, outside the session**); on startup an unfinished closeout (never closed / prefix changed) is detected and a **ledger diff** is shown; in an interactive terminal the user may close out right there.
- `--list-domains` lists preset domains (**no key required**), `--list-misc` lists pending misc items; both are the data source for a future GUI.
- **Version numbers never enter the prompt** (versions are the ledger); the prefix byte fingerprint is `FrozenSnapshot.Id` (`SHA256`).
- `AgentRuntime.Modules` is a **separate assembly** — delete that DLL and the Runtime still runs bare chat.

## Session event stream (V3) — append-only, resumable, the home of one-at-a-time L3 loading

Paper §4.1: a session is no longer a “replaceable dynamic context” but an **immutable, append-only event stream**.

```
user input → event stream (append-only) → every event gets a sequence number (1,2,3,…), type expressed by tag only
```

- **The API is the constraint**: `SessionAppendStream` exposes only `Append`; reads always return `IReadOnlyList`; events are get-only records. There is **no** delete / insert / reorder / overwrite entry point (tests pin this).
- **Order is identity**: sequence numbers must be strictly 1..N; `ValidateInvariants()` (assembly) and `SessionStreamStore.Load()` (replay) are two gates, and a corrupt file fails loudly — **never silently carry a bad state**.
- **On-disk = append-only JSONL**: one event per line; Chinese text is not escaped, enums are written by name (human-readable, diffable); **no timestamps** (body and ledger are separate).
- **Cross-process resume**: state survives exit; the next launch **replays from the file** (measured: round 2 `cached 29,312 / 29,542 = 99.2%`, because events are only appended at the **tail**).
- **One-at-a-time L3 loading** (skills / pitfalls flow in on demand, **one item per call**):

```bash
# 1) append a skill manual to the event stream (offline, free)
wb-cli run --config src/AgentRuntime.Cli/config.private.xml \
  --stream ~/.agentruntime/stream.xml \
  --append ~/.openclaw/workspace/skills/svn-workflow/SKILL.md \
  --source skills/svn-workflow/SKILL.md

# 2) view the stream (renders the tail)
wb-cli run --config src/AgentRuntime.Cli/config.private.xml --stream ~/.agentruntime/stream.xml --stream-show --stream-tail 20

# 3) run with the stream module (the skill is now in the prompt)
wb-cli run --config src/AgentRuntime.Cli/config.private.xml --stream ~/.agentruntime/stream.xml \
  --modules rules,knowledge,memory-index,append-stream --verbose "..."
```

- ⚠️ `session` and `append-stream` are **mutually exclusive** (both are “the home of session history”; together they duplicate it) — config validation stops you.
- ⚠️ The event stream **always follows the frozen zone** (the determinism gate: dynamic content never goes in front).
- ⚠️ `focus` **must follow every dynamic region** (third gate: putting it earlier **fails instead of being fixed up** — focus changes every turn, so only the tail position keeps its churn free).
- ⚠️ `current-tail` **must follow `append-stream` and precede `dynamic-draft`** (third gate V4.2/V4.3: canonical dynamic order `R2 → R4 → R5 → R3`).
- ⚠️ `dynamic-draft` **must follow `current-tail` and precede `focus`** (the draft is fully mutable but still sits before the always-changing R3).
- ⚠️ `tool` **injects nothing yet occupies a slot** (ordered as R2-class) ⇒ after `append-stream`, before `current-tail`/`dynamic-draft`/`focus`; it is also the **only** module that requires an event sink (no `append-stream` ⇒ assembly error, no silent downgrade).
- 📐 Design notes (class diagram / invariants / extension points): `docs/DESIGN-OO.md`; focus spec: `docs/DESIGN-V4.1-SEMANTIC-FOCUS.md`; current-tail spec: `docs/DESIGN-V4.2-CURRENT-TAIL.md`; draft-region spec: `docs/DESIGN-V4.3-DRAFT-REGION.md`.

## Technical baseline (fixed 2026-09-14)

| Item | Value |
|---|---|
| Language | C# 14 |
| Framework | .NET 10 (LTS, supported until 2028-11-14) |
| Target | `net10.0` |
| Platforms | macOS / Windows / Linux, one Core |
| Entry point | Console / CLI (V0 has no UI, no web) |
| HTTP | `System.Net.Http.HttpClient` |
| JSON | `System.Text.Json` |
| Tests | xUnit **v3** (`xunit.v3` 3.2.2, not v4) |
| Benchmark | a **separate directory/project** (see `benchmark/README.md`) |

No .NET Framework / Core 3.x / 8 / 9 / 11-RC; no MAUI; no ASP.NET Core in V0.

## Directory layout

```
AgentRuntime/
├── AgentRuntime.slnx              # solution (.slnx)
├── (launcher moved into tools/wb-cli)  # launcher: wb-cli run "hello"
├── src/
│   ├── AgentRuntime.Models/       # pure data: ChatRequest/ChatResponse/Message/TokenUsage
│   ├── AgentRuntime.Core/         # kernel: IModelClient / IRuntimeModule / engine / timing / config
│   ├── AgentRuntime.Modules/      # pluggable modules: session / rules (delete the DLL and bare chat still runs)
│   ├── AgentRuntime.Providers/    # the only place that knows HTTP and the OpenAI-compatible protocol
│   └── AgentRuntime.Cli/          # composition root: config.xml + Program.cs
├── tests/
│   └── AgentRuntime.Tests/        # xUnit v3 (fully offline, zero network)
├── benchmark/                     # the ruler (independent of the subject), see its README
├── tools/mock-provider/           # offline mock provider (development only, not part of the Runtime)
└── docs/PITFALLS.md               # this project's pitfall log
```

### Dependency direction (boundaries are set by humans; cycles are forbidden)

```
        Cli ──→ Modules ──→ Core ──→ Models
         │        │          ↑          ↑
         └────────┴──────────┴──────────┘
                     Tests ──→ (all)

Benchmark ──→ Runtime      (Runtime never references Benchmark)
```

- **Models**: zero dependencies, DTOs and `[JsonPropertyName]` only.
- **Core**: depends on Models only; it does **not** know what DeepInfra / DeepSeek / OpenAI are.
- **Providers**: the only layer that depends on HTTP and wire formats.
- **Cli**: the **only** place that constructs concrete implementations (composition root).
- Adding a provider (DeepSeek / SiliconFlow / Ollama …) means adding a provider class; Core and Cli logic stay untouched.

## Run it (three steps in a fresh terminal)

```bash
# 0. one-time local prerequisite (already done): SDK in ~/.dotnet, ~/.local/bin/dotnet symlinked,
#    ~/.local/bin on PATH in ~/.zshrc — a new terminal can use it directly.
dotnet --version        # expect 10.0.x

cd ~/Documents/AgentWorkFlow/software-company/projects/AgentRuntime

# 1. one sentence in → one sentence out (real model, default config = session module)
wb-cli run "hello"

# 2. multi-turn (now the session module really matters)
wb-cli run --chat ""

# 3. bare chat: no modules at all (the control group for session-dimension ablation)
wb-cli run --bare --verbose "hello"

# 4. choose a module set (order = contribution order in the prompt)
wb-cli run --modules system-rules,session --verbose "hello"

# 5. diagnostics: token usage / cache / latency / Runtime overhead
wb-cli run --verbose "hello"

# 6. read from stdin (scriptable: lines = turns)
printf 'My name is Zhang, please remember.\nWhat is my name?\n' | wb-cli run --chat --verbose

# 7. build + test (xUnit v3, 54 cases, fully offline)
dotnet build AgentRuntime.slnx
dotnet test  AgentRuntime.slnx

# 8. offline loop without a key (mock provider, free)
dotnet tools/mock-provider/bin/Release/net10.0/MockProvider.dll &     # in another terminal
wb-cli run --config src/AgentRuntime.Cli/config.mock.json --verbose "hello"

# 9. the ruler (independent experiment system): plan first, then the full run
wb-cli bench --plan
wb-cli bench --calibrate             # calibrate corpus tier tokens (3 calls)
wb-cli bench --label my-run          # full run, results under benchmark/runs/<time>-<label>/
```

> `wb-cli run` is the launcher: the default config is passed explicitly first; a `--config` you pass yourself comes last and wins.

## Verified (2026-09-14, real endpoint)

**① Single-turn baseline**

```
$ wb-cli run --verbose "hello, introduce yourself in one sentence"
Hello, I am an AI assistant that can answer questions, give advice and help with tasks.
tokens.prompt 36 · completion 234 · total 270 · cached 0
latency.total 2228.1ms · provider 2227.9ms · runtime overhead 0.2ms
```

**② Session ablation (same 3-turn script, only the session module differs)**

| Turn | +Session | Bare chat |
|---|---|---|
| 1 “My name is Zhang, remember it” | prompt 39 · answers “noted” | prompt 39 · answers “noted” |
| 2 “What is my name?” | prompt 65 (3 messages) · **correct: Zhang** ✅ | prompt 34 (1 message) · **cannot answer** ❌ |
| 3 “Say it again” | prompt 105 · ✅ | prompt 35 · ❌ |
| Subtotal | prompt 209 / total 724 / 5093ms | prompt 108 / total 538 / 5328ms |

→ **Session buys capability, at the price of a growing prompt** — the first time “what one module is worth” was stated in numbers.

**③ Upstream prefix cache (T02 probe)**

```
same long prefix (1810 tokens), 1st call: cache_hit 0     / miss 1810
same long prefix (1810 tokens), 2nd call: cache_hit 1664  / miss 146   ← 92% hit, 1664 = 64 × 26
```

→ The upstream cache **really exists**, but it is **aligned to 64-token blocks** and the prefix must be byte-stable; short conversations never reach it.

**④ Frozen-context ladder (the most valuable group: the 10k tier)**

| Tier (frozen) | Stable prefix + append | Counter-example: dynamic content first |
|---|---|---|
| L0 (not frozen) | 0 hits · 0% saved | — |
| L1 (**1000 tokens**) | 896 hits (89%) · $0.000046–55 per turn · **~67% saved** | 0 hits · **0% saved** |
| L2 (**9974 tokens**) | **9856 hits (98.4%)** · $0.000299 per turn · **78.5% saved** | 0 hits · $0.001403 · **0% saved** (**4.7×**) |
| L1/L2 through the Runtime | **identical to calling by hand** | — |

→ **The larger and steadier the frozen context, the more you save (0% → 67% → 78.5%); put “changes every turn” content first and the gain immediately drops to zero.**
Across 55 real calls: **Runtime overhead max 1.3ms / average 0.1ms**.

- Endpoint `https://api.deepseek.com/chat/completions`, model `deepseek-v4-flash` (OpenAI-compatible path; `baseUrl` without `/v1` also works).
- `cached 0` on single-turn / short sessions is normal: there is no reusable, long-enough stable prefix (hits are **64-token block aligned**).

## Key handling

- `config.xml` **never stores a key**, only where to get one: `apiKeyEnv` (environment variable, wins) or `apiKeyFile` (local file, `~` supported).
- Keys **never** enter logs, exceptions or `--verbose` output; `OpenAICompatibleClient` exceptions carry only URL / status / a truncated body (tests pin this).
- The file behind `apiKeyFile` **must not be committed to SVN**.
- Environment-variable route: `export AGENTRUNTIME_API_KEY=...` (in your own terminal; never paste it into chat or a commit).

## Roadmap (each layer independently testable)

| Version | Content |
|---|---|
| **V0** | one sentence → model → one sentence ✅ |
| **V1** | Session (multi-turn, a pluggable module) ✅ |
| **V2** | Frozen Context (stable prefix / cache boundary) ✅ three zone modules + domain filter + determinism gate + global fingerprint/version ledger + closeout watermark (minimal); measurements in CHANGELOG |
| V3 | Append Stream (append-only event stream) ✅ stream + JSONL persistence + cross-process resume + one-at-a-time L3 loading (`--append`) + invariant gates |
| V4 | Snapshot (deterministic resume) ✅ ledger of positions + atomic write + `--resume` with three gates (prefix fingerprint self-proof / orphan tail refusal / missing stream report); see `docs/DESIGN-V4-SNAPSHOT.md` |
| **V4.1** | **Semantic Focus ✅ implemented**: tag identity + a pure Core/Focus surface + the third gate + `--focus-show/--focus/--focus-clear/--focus-policy` + tests **169/169**; default `minWeight=0.95`. Real-machine A/B: **A 6/6 = B 6/6** (baseline ceiling) ⇒ benefit verification handed to the community (needs ≥500k-token contexts; see the spec §12); experiment plan/data: `docs/EXPERIMENT-V4.1-FOCUS.md` |
| **V4.2** | **Current Tail (R4) ✅ implemented**: protocol R1-P + redefined modes (`--bare` baseline / `--vacuum`) + R4 wiring and I1~I18; `--tail-show/--tail/--tail-clear/--tail-report` (whiteboard in `~/.agentruntime/tail/<sessionId>.json`, the single source of truth, corrupt files fail loudly); tests **199/199**. Four real-machine groups (16 calls): **R4 changes every turn while stable-prefix hits do not drop**; correctness 4/4 × 4 groups |
| **V4.3** | **Dynamic Draft (R5) ✅ implemented**: `[DRAFT]` section + protocol v4 + order **R2 → R4 → R5 → R3 (R3 last)** + `--draft-show/--draft/--draft-clear/--draft-report` (draft in `~/.agentruntime/draft/<sessionId>.json`, single source of truth, corrupt files fail loudly; **no partial edits, whole-block overwrite only**); without it, byte-identical to V4.2; tests **221/221** |
| V5 | Knowledge / Memory |
| V6 | Router (multi-model routing) |
| V7 | Worker (context funnel) |

**Test targets precede architecture**: the benchmark spec (V0.1) is fixed in `benchmark/README.md` — the five metrics Token / Cache / Latency /
Runtime Overhead / Task Equivalence, and the three test units T01 / T02 / T03.
`TokenUsage`, `RuntimeTiming` and friends are designed from V0 for **future measurement**, so the architecture does not need to be rewritten later.

## Design principles

1. **Boundaries by humans, code by AI, conclusions by the benchmark.** Dependencies flow one way; cycles are forbidden.
2. **The ruler is independent of the subject** (`benchmark/` never enters Core).
3. **Data safety**: keys never persisted, never logged; exceptions diagnostic but never leaking.
4. **Replaceability**: providers and models are swappable (Runtime–Model decoupling); Core knows no vendor.
5. **No premature optimisation**: V0 is a few hundred lines — no DI container, no enterprise web layering.

## License (MIT)

- The **kernel** (`src/AgentRuntime.Core` · `Modules` · `Providers` · `Hosting` · `Cli`) and the **TUI observer** (`src/AgentRuntime.Tui2`) are **MIT** — see the repo-root `LICENSE` (keep the copyright and permission notice).
- **Not open source**: `frozen-private*` (private corpora) · internal knowledge bases · experiment records (`benchmark/runs/`) · internal engineering docs. Anything published must first pass `wb-cli scan` (private corpora / host paths / intranet addresses / credentials / cloud-drive links = **hard red lines**).

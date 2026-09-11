> 🌐 **English** | [中文](README.zh-CN.md)

# Prefix-Cache-Optimal Layered Agent Architecture (Design Note II)

> Date: 2026-09-11　Author: myself (implementation platform: OpenClaw only)
> Series: Note II. Note I (*[Hierarchical Hybrid Model Architecture](https://github.com/18040659483r0-pixel/hierarchical-hybrid-model-architecture)*) reduced the **number of tokens**; this note reduces the **price of every token**.

---

## ① Core Proposition

Note I used layering to drive the **token count** down. But as long as the context is **re-assembled, re-ordered, or re-summarized every turn**, its byte prefix changes, the model's **prefix cache (Prompt / KV cache)** is invalidated, and bytes that were already computed are **paid for again at full price**. This is the second and more insidious waste of single-session mode: **not too many tokens — every token bought repeatedly at full price.**

This architecture makes exactly one claim:

> **Freeze everything in the context that can stay unchanged into byte-level constants, and let change happen only as a strict tail-append.**

Every turn then costs only a tiny increment Δ; everything else hits the prefix cache. In one line:

> **Keep the context physically almost still — whatever does not move, the model recomputes for free.**

---

## ② Architecture

```text
┌──────────────────────────────────────────────────────────────────────────────────┐
│ ①  FROZEN REGION  冻结区域   [ read-only · versioned · never rewritten ]         │
│                                                                                  │
│ 0-1  Inviolable Rules        全局铁则 / 绝对底线        (global ver, e.g. v1.0)  │
│ 0-2  Global Knowledge Index  高度抽象的方法论目录        (session ver, e.g. v2.4)│
└──────────────────────────────────────────────────────────────────────────────────┘

                                  │ 100% byte-identical  ⟶  prefix-cache HIT
                                  ▼

┌──────────────────────────────────────────────────────────────────────────────────┐
│ ②  DYNAMIC REGION  动态增量区   [ Byte-Level Append-Only Log · tail-add ]        │
│                                                                                  │
│ [1] Specialist Index      增量专家知识库    中等抽象目录 · 按需懒加载            │
│ [2] Execution Artifacts   具象操作技能库    指针下钻 · SOP / 代码块 / 踩坑       │
│ [3] Memory Index          动态记忆索引      时间流日志：发生/输入/回应/操作      │
│ [4] Context Workspace     执行工作区        当前 Turn 对话与工具输入输出         │
└──────────────────────────────────────────────────────────────────────────────────┘

                                  │ pointers & checkpoints (outside the prompt)
                                  ▼

┌──────────────────────────────────────────────────────────────────────────────────┐
│ ③  ENTITY / SNAPSHOT  实体与快照层   [ associated store · zero prompt cost ]     │
│                                                                                  │
│ Raw Memory Logs  ·  Context Snapshots  ·  Frozen Domain Knowledge (source)       │
└──────────────────────────────────────────────────────────────────────────────────┘

                                  │
                                  ▼

┌──────────────────────────────────────────────────────────────────────────────────┐
│ ④  SESSION LIFECYCLE  生命周期与进化闭环   [ only place the prefix changes ]     │
│                                                                                  │
│ Atomic Flow    Context_N = Context_(N-1) ⊕ ΔFlow_N        (append-only)          │
│ Recovery       hot: Context Snapshot  |  cold: Frozen Base + Memory              │
│ Graceful Close (1) distill  →  (2) Layer-0 Gatekeeper audit                      │
│                →  (3) version +1  →  (4) clear prefix, GC, fresh session         │
│ Self-heal      orphaned session ⟶ auto lightweight "closing agent"               │
└──────────────────────────────────────────────────────────────────────────────────┘
```

Layer legend — ① Frozen Region (inviolable rules + global knowledge index) is the maximally reused prefix; ② Dynamic Region is the append-only log (specialist index / execution artifacts / memory index / workspace); ③ Entity·Snapshot is the out-of-prompt associated store reached by pointers; ④ Session Lifecycle is the only moment the prefix is allowed to change.

---

## ③ Underlying Rules

| Layer | Name | Mutability | Role | Effect on prefix cache |
|---|---|---|---|---|
| **0 Frozen** | Inviolable Rules + Global Knowledge Index | read-only, versioned; only the wrap-up gate may edit | global iron rules / absolute bottom lines + a highly abstract methodology index | **100%-byte-stable prefix** → always a hit, reused across sessions |
| **1 Dynamic** | dual index + append log | append-only | specialist index / execution artifacts / memory index / workspace | tail-append → hit ratio → 100% as it grows |
| **2 Entity·Snapshot** | raw logs · snapshots · knowledge source | associated store, never in the prompt | target of Layer-1 pointer drill-down | **zero prompt cost** (a pointer only) |
| **3 Lifecycle** | flow increment / recovery / wrap-up / self-heal | event-driven | the only moment the prefix may change (GC) | resets the prefix at the session boundary |

---

## ④ Key Mechanism (definition-level conclusions)

### 4.1 Minimal-Append Law (necessity + minimality)

The context evolves by appending only:

```text
Context_N = Context_(N-1) ⊕ ΔFlow_N          (⊕ = byte-level tail append)
```

The increment `ΔFlow_N` is **logically necessary and minimal** iff all three hold:

- **Necessity**: removing `ΔFlow_N` changes the reachable behavior (the increment is non-empty, non-decorative).
- **Non-redundancy**: no byte of `ΔFlow_N` is derivable from `Context_(N-1)`.
- **Append-only**: `Context_(N-1)` remains a byte-prefix of `Context_N` (the prefix is never rewritten).

Formally:

```text
ΔFlow_N = min{ |Δ| :  Behavior(Context_(N-1) ⊕ Δ) ≠ Behavior(Context_(N-1))
                      ∧  Δ ⊄ Closure(Context_(N-1)) }
```

**Direct consequence of minimality**: the write cost of an update is **O(|Δ|)**, not **O(|context|)**.

### 4.2 Cache-Hit Law

With turn-N context length `L_N = L_(N-1) + |Δ_N|`, the prefix-cache hit ratio is

```text
H_N = L_(N-1) / L_N = L_(N-1) / ( L_(N-1) + |Δ_N| )   ⟶  1
```

Append-only makes `H_N` increase monotonically with the turn count and approach 1; the instant a prefix is rewritten, `H` collapses to 0.

### 4.3 Main Formula (cache-dividend / cost law)

Let `P₀` be the price of an uncached input token; `w` the hit-token price ratio (`0 < w < 1`, a hit costs `w·P₀`); `C_in` the input cost with no cache at all; `C_out` the output cost (cache-independent).

```text
C = [ 1 − (1 − w)·H ] · C_in + C_out
```

| Symbol | Meaning |
|---|---|
| `C` | total cost of finishing the task |
| `C_in` | input cost with no caching = `P₀ · L_in` |
| `C_out` | output cost (cache-independent) |
| `w` | hit-token price ratio (`0 < w < 1`) |
| `H` | prefix-cache hit ratio |

**Effective price:**

```text
P_eff = P₀ · [ 1 − (1 − w)·H ]
```

Each point of hit ratio pulls the effective price one step closer to `w·P₀`.

### 4.4 Corollaries

1. **Savings limit.** When the architecture guarantees `H → 1`, the savings ratio `ρ = 1 − C/C₀ → 1 − w`. With a typical hit discount such as `w ≈ 0.1` (hit ≈ 1/10 of full price; varies by provider), **the long-run saving approaches 90%**, independent of task length.
2. **Longer is cheaper.** `H_N = L_(N-1)/L_N` increases with `N`; the marginal input price of each turn `→ w·P₀`. The longer the session, the larger the dividend.
3. **Minimality is maximal saving.** The only uncached part is `Δ_N`. Shrinking `Δ` to its necessary minimum minimizes both the write and the bill; rewriting the prefix sets `H = 0` and collapses the dividend. **Necessity/minimality and economy are therefore one and the same law.**
4. **Cross-session reuse.** The frozen base `B` (Layer 0) is **byte-identical in every session** → paid once at full price, hit thereafter at `w·P₀`. Its cost is amortized over the whole session population.
5. **Composite law with Note I.** Multiplying the two notes — total cost = quantity × unit price:

```text
C = T(A) · P₀ · [ 1 − (1 − w)·H ]
  = T₀ · A^(−α) · P₀ · [ 1 − (1 − w)·H ]
```

   Note I shrinks the token count `T` with layers; Note II shrinks the effective price with `H`. **Both curves multiply in the same direction — cost is compressed twice.**

### 4.5 Side benefit: immutability is integrity

The frozen region is read-only and changes **only through a versioned gate**, which yields two side effects: (1) no untrusted content can alter the global bottom lines (**prompt-injection resistance**); (2) the iron rules always sit at the same byte position, so the model sees the same prefix every turn (**instruction-drift resistance**). **Security and cache-friendliness point to the same design here — not changing is optimal.**

---

## ⑤ Implementation Note

The author **is building** this system and uses **OpenClaw exclusively** as the implementation platform; experimental data will follow to substantiate these conclusions. This note records the idea first, to establish it as the author's **own original work, not copied from others**.

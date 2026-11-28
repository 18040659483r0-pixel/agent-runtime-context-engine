# Agent Runtime / WhiteBox · **1.1.9**

> 中文: [RELEASE-1.1.9.zh-CN.md](RELEASE-1.1.9.zh-CN.md) · 日本語: [RELEASE-1.1.9.ja.md](RELEASE-1.1.9.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Hotfix. A tool call that the model wrote correctly could vanish without a trace; it can no longer.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.9-macos-arm64.tar.gz` | macOS arm64 | `f9c8e2c2134de501d74854fa3b12759443a54d284b9a52c3f3c9d3e02a968ede` |
| `deploy-manager-1.1.9-winx64.tar.gz` | Windows x64 | `cd4c2529e073f7764abf84909431b3143df1e77c6ec216b826e29a3828043f08` |

> **⚠️ Upgrading from an earlier beta?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.

## What changed since 1.1.8

### 1) Fixed: a tool block could be dropped in silence

- **Symptom** (winx64, real session): `write` was called 41 times; only 8 produced a result. The other 33 produced
  **no event at all** — no result, no denial, no approval. The model concluded “write is broken” and worked around it.
- **Cause**: the block-header scanner treats code fences, quotes and 4-space indentation as *quoted examples*.
  When a reply contained only such a block, the parser returned “nothing” — and nothing is what the model saw.
  (The line form itself was never broken: `key: value` / `key<<` … `>>` has always been valid input.)
- **Fix**: those blocks are no longer silent.
  - only such blocks → the reply is **rejected** with a message that names the cause and the fix
    (“`[TOOL]` must start its own line at top level”);
  - mixed with real blocks → the real ones run, and a `tool-fence` hint reports how many were skipped;
  - a header glued to prose (`ok, writing: [TOOL] write …`) is rejected the same way.
- **Safety is unchanged**: a block inside a fence / quote / indent is still **never executed**.

### 2) Protocol v121: the shape rule now lives in the contract

Clause 5 now states it explicitly: the `[TOOL]` header must start a line of its own at top level; a block inside a
code fence, a quote or a 4-space indent is an example and is not executed. It also restores the zero-escape line form
as a legal shape when a value contains `<` or `&` (previously the protocol said the line form was retired, which
contradicted the tool face — that contradiction is gone). Protocol size: 27 lines / 28,384 chars / ≈7,096 tokens
(budget ≤7,200).

## Verified before shipping

- Unit tests: **1413 / 1413** (macOS). TUI2 self-test: **PASS**. `wcm selftest`: **17 / 17**.
- Re-verified by the Windows side: six parser cases **6/6**, plus a real end-to-end turn (line-form `write` with
  XML content → file written byte-for-byte).
- macOS package: D1–D7 + D9 **PASS**; Windows package: D1 + D9 **PASS**.
- `release-check` (byte-for-byte vs source) **PASS** on both packages:
  mac `db64116f…`, win `93135d11…`.

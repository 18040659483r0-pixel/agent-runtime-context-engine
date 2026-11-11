# Agent Runtime / WhiteBox · **1.1.5**

> 中文: [RELEASE-1.1.5.zh-CN.md](RELEASE-1.1.5.zh-CN.md) · 日本語: [RELEASE-1.1.5.ja.md](RELEASE-1.1.5.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Ninth 1.x beta. Every turn now records its own performance breakdown, repeated tool calls are challenged, and the whole repository is C# + XML.**

> **⚠️ Upgrading from v1.1.3 beta (or any later beta)?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.
> This build still auto-backs up an old workspace to `<install-dir>-backup-<timestamp>.zip` before a force install,
> and refuses to wipe if the backup fails — but **make your own backup first** anyway.

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.5-macos-arm64.tar.gz` | macOS arm64 | `79ec199cfd9573bef89a62fd5d29c863e6ac9751f8dcd00c6ebde32bad887be2` |
| `deploy-manager-1.1.5-winx64.tar.gz` | Windows x64 | `b91f63794c01f70a04aaac5c75d6bf1fc5363d2f7352bc0f18a91ba8527ad20f` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.5-<platform>.tar.gz
cd deploy-manager-1.1.5-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.4

### 1) Every turn records its own performance breakdown

The usage ledger (`<stream>.usage.xml`) now stores, per turn:

- `ProviderMs` — model call time
- `ToolMs` — tool execution time
- `EngineMs` — assembly/serialization/persistence (`TotalMs − ProviderMs − ToolMs`)
- `AllocBytes` — managed allocations for the turn
- plus prompt / cached / uncached / completion tokens and message count

Old ledgers without the new fields still load (missing = 0). The panel shows engine/tool columns instead of the old single “overhead”.

### 2) Repeated tool calls are challenged

If the **same tool + same arguments** returns the **same result three times in a row**, the runtime emits a `tool-repeat-guard` hint: stop repeating, change strategy. It only fires when the result is identical; changing arguments or result resets the counter. This prevents a model from looping on the same build/read/command.

### 3) Protocol v111: one build, one acceptance

Clause 23 now says: a small edit ⇒ rebuild once and re-run only the affected step; acceptance runs **once at the end**; a failed closeout step is fixed and re-run alone — never re-check the whole pipeline. Protocol is 26 lines / 24,768 chars / 6,192 tokens (budget ≤ 6,200).

### 4) Closeout notices are reminders, never forced

- 60% / 70% context: “start planning a closeout — reminder only, **not interrupting or forcing**; you may continue, but turns get slower.”
- 80%: `[收尾建议]` (was `[收尾要求]`) and explicitly says continuing is allowed.

### 5) No more Python or JSON in the repository

- `deploy-template.json` → **`deploy-template.xml`** (XmlSerializer).
- Benchmark result artefacts → XML.
- The tree contains no `.py` / `.json` source files (framework-required `*.deps.json` / `*.runtimeconfig.json` remain, as an explicit exception).
- The deployment template’s `AGENTS.md` now carries the same “C# + XML only” rule.

### 6) Knowledge/decision/memory highlighting, all languages

- Knowledge & decisions (including bare `L1`–`L4`, node ids, “knowledge tree”, “decision library”) use the intent colour + bold.
- Memory (`memory index`, `MEMORY.md`, `記憶索引`, …) uses the result colour + bold.
- Recognition now covers English and Japanese wording as well as Chinese.

## Verification

- Unit tests: **1395 / 1395**
- TUI2 self-test: **PASS**
- Deployment manager self-test: **17 / 17**
- Package gates: macOS **D1–D7 PASS**; Windows **D1 + D9 PASS** (`libc`-free Windows build)
- Isolated upgrade test (v1.1.4 → v1.1.5): `--update` keeps workspace/config/key/user declarations; `deploy-template.xml` present in the package.

## Notes

- Windows: close WhiteBox/TUI2 before updating (Windows locks binaries).
- An in-place `--update` rewrites the `whitebox` launcher to that install directory — expected.
- Version **1.1.5 (beta)**. `deployment-template/` and `frozen/` are unchanged from 1.1.4 except the rules file name/serialization.

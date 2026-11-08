# Agent Runtime / WhiteBox · **1.0.0-beta.1**

> 中文: [RELEASE-1.0.0-beta.1.zh-CN.md](RELEASE-1.0.0-beta.1.zh-CN.md) · 日本語: [RELEASE-1.0.0-beta.1.ja.md](RELEASE-1.0.0-beta.1.ja.md)

**The first 1.x beta.** The product version now moves from the old 0.9.x / Alpha numbering to **1.x**, starting here.
This release is about one thing: **you can download a package, answer four questions, and have a working WhiteBox.**

## Download & run

1. Download **`deploy-manager-1.0.0-beta.1.tar.gz`** (see assets below).
2. Verify: `shasum -a 256 -c deploy-manager-1.0.0-beta.1.tar.gz.sha256`
3. Extract and run the wizard:

```bash
tar -xzf deploy-manager-1.0.0-beta.1.tar.gz
cd deploy-manager-1.0.0-beta.1
dotnet wcm.dll setup
./run.sh            # Windows: run.cmd
```

Full guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What's new in 1.0.0-beta.1

### 1) Deployment manager (new)

`wcm setup` is a terminal wizard that performs the whole installation:

- **four questions only** — install directory → provider → reasoning level → API key;
- defaults: **DeepSeek official `deepseek-v4-flash`**, reasoning **low**; `high` is discouraged (slow and expensive, little gain), and the chain-of-thought view is rendered by WhiteBox itself at any level;
- installs runtime binaries, frozen corpus, a 5-node knowledge seed, onboarding docs, and the process-findings ledger;
- builds the knowledge index, runs `doctor`, and offers to launch the TUI;
- keys are stored **inside the installation** (`state/api_key`, 0600) — never in your home directory, never in `config.xml`;
- non-interactive mode for automation: `setup --yes --no-run --api-key-file <file>`.

### 2) Process findings become a first-class knowledge source

- Decision reports now distinguish **`found knowledge`** (durable, reusable discovery) from **`found status`** (this project's current condition). Only knowledge is persisted.
- Accepted reports automatically append knowledge findings to `workspace/docs/PROCESS-FINDINGS.md` (idempotent by report fingerprint).
- Closeout moves them into the knowledge tree as **L4** (`L4-pfd-NNN`, contiguous numbering) and the normal loop grows them to L3 → L2 → L1.
- Protocol zone: **v106** — 26 lines / 22,269 chars / **5,568 tokens** (budget ≤ 5,600).

### 3) TUI2 input quality of life

- **Up/Down history**: submitted text is kept (up to 100 entries); ↑ walks older, ↓ walks newer, and moving past the newest restores your draft. Multi-line drafts keep normal caret movement.
- **Short paste goes inline**: pastes of **≤ 100 characters** are inserted directly into the input box; only longer text is folded into a single atomic paste label.

### 4) Closeout reliability fixes

- Missing mandatory artifacts are auto-created as empty placeholders (minimal deployment can close out).
- L4 ids generated from explicitly named sources now use the **ordinal in the source file**, keeping `1..N` contiguous.
- Report section headings are fully language-pack driven (English by default), and heading detection is an exact match (no more whole paragraphs turning into heading colour).
- Request-file watermark fingerprints are computed from the **line-start** `## 请求` heading.
- `Why/Impact` splitting tolerates the Chinese em dash `——`.

## Verification (measured, not claimed)

| Item | Reading |
|---|---|
| Unit tests | **1,368 / 1,368** |
| TUI2 `--selftest` | **PASS** |
| Deployment test suite **1.0.0** (D1–D8) | **PASS** (D8: one real provider round-trip) |
| Package smoke test | `setup --yes --no-run` → `doctor` green → deployed TUI `--selftest` PASS |
| Protocol zone | v106 · 5,568 / 5,600 tokens |

## Known limitations

- The package is validated on **macOS (arm64)**; .NET 10 assemblies are OS-independent, but the **Windows wizard path still needs a real-machine pass** before we call it fully supported.
- The package is framework-dependent: **.NET 10 must be installed**.
- `wcm rule-check` currently reports a drift on the Windows-side rule file (`deploy-template.json`); the fix is queued on the Windows side.
- The 5-node knowledge seed is intentionally minimal: L3/L2/L1 appear after the first real closeout.

## Assets

- `deploy-manager-1.0.0-beta.1.tar.gz` — self-contained deployment manager package (runtime + frozen corpus + workspace seed + docs)
- `deploy-manager-1.0.0-beta.1.tar.gz.sha256` — checksum

## Papers

The three language versions are updated in this release:
[English](README.md) · [中文](README.zh-CN.md) · [日本語](README.ja.md)

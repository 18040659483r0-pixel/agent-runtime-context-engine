# Agent Runtime / WhiteBox · **1.1.4**

> 中文: [RELEASE-1.1.4.zh-CN.md](RELEASE-1.1.4.zh-CN.md) · 日本語: [RELEASE-1.1.4.ja.md](RELEASE-1.1.4.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Eighth 1.x beta. English-first CLI, a report layout built for the terminal, and an upgrade path that cannot silently eat your workspace.**

> **⚠️ Upgrading from v1.1.3 beta (or any later beta)?**
> If you want to keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.
> This build does auto-back up an old workspace to `<install-dir>-backup-<timestamp>.zip` before a force install,
> and refuses to wipe if the backup fails — but **make your own backup first** anyway.

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.4-macos-arm64.tar.gz` | macOS arm64 | `60f5826db89b92c642027441c52d3b0412cc5fbf56b3aa1a810c95dfbbf3ca54` |
| `deploy-manager-1.1.4-winx64.tar.gz` | Windows x64 | `18f9536a4575981017e1e1545f050afc3e540919dbf005c759995d5d7baa6fee` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.1.4-<platform>.tar.gz
cd deploy-manager-1.1.4-<platform>
dotnet wcm.dll setup      # first screen: choose your language
./run.sh                  # Windows: run.cmd
# after setup you can also just type:  whitebox
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.1.3

### 1) CLI messages are now a language pack (English by default)

Every command-line surface — the main CLI, `tools/closeout`, the benchmark tool, and the TUI2 help/diagnostics — now reads from a single language table instead of hard-coded Chinese.

- Default is **English**; `--lang zh|ja` switches, and `WB_CLI_LANG` / `WB_TUI_LANG` / `config.xml`'s `UiLanguage` are honoured (explicit flag > env > config > English).
- Missing translations fall back to the Chinese source string, so nothing goes blank.
- The project gate now also fails if a new CLI string has no translation.

### 2) The decision report is laid out for the way TUI2 refreshes

TUI2 always shows the newest content at the bottom, so the report now puts the reading flow first:

- **Terms** and **Raw material** sit together at the top, **unnumbered** (material used to be “chapter 4”).
- Numbered sections are now `1 need · 2.x steps · 3 outcome · 4.x findings · 5.x next · 6 body`.
- `next:` items must state a **concrete action** (who does what, and what choosing it changes). `rec` is only a marker, never the content; an empty `next [rec]:` line is dropped instead of rendering as “1. (recommended)” with nothing after it.
- Protocol zone is **v110**: 26 lines / 24,586 chars / 6,147 tokens (budget ≤ 6,200).

### 3) Knowledge / decision / memory markers are highlighted in the card

- **Knowledge & decisions** (`知识树 / 知识库 / 决策库 / 节点 id L1~L4-xxx-nnn`, “写成项目决策”, “写成踩坑集”, …) use the **intent colour + bold**.
- **Memory** (`记忆 / 记忆索引 / 当日记忆 / MEMORY.md / memory / 日记`) uses the **result colour + bold**.
- Highlighting only adds ANSI styling; the plain-text projection is byte-for-byte unchanged.

### 4) Upgrades can no longer silently eat your workspace

- Closeout declarations are split: `tools/closeout/requirements/` = **system built-ins** (replaced by `--update`), `tools/closeout/requirements-user/` = **your extensions** (kept; same id overrides the built-in).
- `--update` first moves any declaration you changed or added in the old `requirements/` into `requirements-user/`, then replaces the built-in set.
- A force/overwrite install that finds an old `whitebox/workspace` now backs it up to a timestamped zip (workspace + `config.xml` + `requirements-user`) and aborts if the backup fails.
- `--update` leaves `whitebox/workspace`, `config.xml`, `state/api_key`, and `frozen/` untouched.

## Verification

- Unit tests: **1388 / 1388**
- TUI2 self-test: **PASS** (including the new highlighting assertions)
- Deployment manager self-test: **17 / 17**
- Package gates: macOS **D1–D7 PASS**; Windows **D1 + D9 PASS** (cross-platform symbol gate: no `libc` in the Windows build)
- Isolated upgrade test: v1.1.3 → v1.1.4 via `--update`; user declaration migrated and still effective; workspace / config / key byte-identical.
- Isolated force test: old workspace zipped before wipe; archive verified to contain the workspace marker, `config.xml`, and `requirements-user`.

## Notes

- Windows: close the running WhiteBox/TUI2 before updating (Windows locks the binaries).
- An in-place `--update` rewrites the `whitebox` launcher to point at that install directory — expected when updating the same directory.
- Version stays **1.1.4 (beta)**. The `deployment-template/` and `frozen/` trees are unchanged from 1.1.3, so no workspace migration is required for this release.

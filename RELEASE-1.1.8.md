# Agent Runtime / WhiteBox · **1.1.8**

> 中文: [RELEASE-1.1.8.zh-CN.md](RELEASE-1.1.8.zh-CN.md) · 日本語: [RELEASE-1.1.8.ja.md](RELEASE-1.1.8.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Twelfth 1.x beta. True color by default, honest path highlighting, and a setup check that no longer cries wolf.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.8-macos-arm64.tar.gz` | macOS arm64 | `c96e177c30dd1d2220a049e2592c043f520d04437f23f5d3c26c6b417e973b45` |
| `deploy-manager-1.1.8-winx64.tar.gz` | Windows x64 | `8d2a51841c817ec2e9b924ca00fbf6952805eee2f3b37e305e91f7dad69180f3` |

> **⚠️ Upgrading from an earlier beta?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.
> Make your own backup first anyway.

## What changed since 1.1.7

### 1) True color is now the default (and that is why the gold looked “milky”)

- **Symptom**: on a true-color terminal the accent gold showed up as a washed-out yellow, and paths were nearly invisible.
- **Root cause**: the depth detector treated a missing `COLORTERM` as “this terminal cannot do 24-bit”. Windows Terminal
  (and some other hosts) leave both `TERM` and `COLORTERM` empty, so the UI silently fell back to 16 colors, where gold and path
  share the same code.
- **Fix**: pick the depth from the **host itself**, then from the environment:
  1. `--color-depth` given → use it;
  2. `NO_COLOR` / not a TTY / `TERM=dumb` → no colors;
  3. Windows Terminal (`WT_SESSION`) → true color;
  4. macOS → true color;
  5. Windows → true color (the runtime enables VT itself, and 16-color output already proved VT is on);
  6. `COLORTERM` says truecolor/24bit (or `TERM` does) → true color;
  7. `COLORTERM` set, or `TERM` contains `256color` → 256 colors;
  8. otherwise → 16 colors.
- **Result**: accent gold is the real `#F6C453` and paths are `#F0C987` again. `--color-depth 256` / `16` still downgrade on demand.

### 2) Numbers are not paths any more

- **Symptom**: a module row like `模块 | 8/12 | …` painted `8/12` as a file path.
- **Root cause**: the auto-annotator accepted any `x/y` shape.
- **Fix**: a relative-path span must now contain at least one ASCII letter; `8/12`, `4/12`, `1/2`, `3/8` are left plain.
  Real paths (`docs/PITFALLS.md`, `src/AgentRuntime.Core/x.cs`, `handoff/x-1.md`) and `./a/b` keep their highlight.

### 3) `wcm setup` no longer reports a false failure

- Since the launcher became opt-in (`--register-launcher`), the post-install health check still counted a missing global
  `whitebox` command as a failure — so a perfectly good install ended with `ok=0`.
- It is now an informational line (`ℹ️ … not registered`), and the install reports `ok=1`.

### 4) Release hygiene

- A private project name that had slipped into the bundled benchmark corpus was replaced with a neutral phrase.
  The corpus lock file was regenerated; the corpus stays within its size budget.
- Internal: the release gate now requires the packaged `AgentRuntime.Tui2.dll` to be **byte-identical** to a fresh
  `dotnet publish` of the same source (sha256), for both platforms.

## Verified before shipping

- Unit tests: **1406 / 1406** (macOS). TUI2 self-test: **PASS**. `wcm selftest`: **17 / 17**.
- macOS package: D1–D7 + D9 **PASS**; Windows package: D1 + D9 **PASS** (Windows branch, no `libc`).
- `release-check` (byte-for-byte vs source) **PASS** on both packages:
  mac `d3c31882…`, win `c59bb991…`.

# Agent Runtime / WhiteBox · **1.2.0**

> 中文: [RELEASE-1.2.0.zh-CN.md](RELEASE-1.2.0.zh-CN.md) · 日本語: [RELEASE-1.2.0.ja.md](RELEASE-1.2.0.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Every answer ends with a report you can read in full; the Windows window no longer freezes while you drag it.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.2.0-macos-arm64.tar.gz` | macOS arm64 | `9698075721c01cab458dfc9b00653385785b6488cb5c5f43844f2340fa225188` |
| `deploy-manager-1.2.0-winx64.tar.gz` | Windows x64 | `58e62c15b308fbcc5affed6f12ea579ad0c6375581d6c7d0a6c55a695a0b0267` |

> **⚠️ Upgrading from an earlier beta?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.

## What changed since 1.1.9

### 1) Every question ends with a decision report

- **Symptom**: ask a one-line question (“give me that path”) and the answer arrived as a bare `[RESULT]` line.
  The card only shows intent/result, so the **material** — the actual text you asked about — was rewritten/trimmed
  away, and there was no report to open.
- **Why**: the runtime only requested a report when the model declared a terminal state. A reply with no tool call
  and no `[DONE]` was treated as the normal “waiting for your next sentence” stop.
- **Fix**: when the chain is about to stop **without** a terminal state, the runtime asks once:
  *finish with `[DONE]` / `[NO-SOLUTION]` / `[NEED-USER]`, and include the `[REPORT]` in the same turn*
  (the report is what carries the material). Existing terminal paths are unchanged.

### 2) The status bar wraps instead of truncating

- Narrow terminal or long wording no longer cuts off “thinking … 00:42 ⠋⠙”.
- The bar is packed by segments (`[state] counts` · summary · hints · activity), wraps at the display width,
  and may use up to three rows (adaptive to window height).
- The `Tab: modules` hint is gone; `Esc: pause · Esc twice: quit` stays.

### 3) Windows: dragging the window no longer freezes the UI

- **Root cause** (found on a real winx64 console): Windows sets the input handle for **window-resize, focus,
  menu and key-up** records too, but `ReadFile` produces **no bytes** for them and simply blocks. Any
  “wait for the handle, then `ReadFile`” loop deadlocks while you drag the window — the UI only came back after
  a click.
- **Fix**: peek the console records, treat only key-down/mouse as readable, and **consume** resize/focus/menu/key-up
  records before waiting again (trace line `drain type=N`). The main loop also ticks at a fixed cadence
  (33 ms busy / 100 ms idle), so resize repaints within ≈100 ms and the animation never depends on input.
- POSIX path untouched (SIGWINCH).

### 4) Diagnostic: `--anim-probe`

`AgentRuntime.Tui2 --anim-probe 3` prints animation frames and reports `frames / distinct / codes`, so a host can
tell “frames are not emitted” from “the font has no Braille glyphs”.

## Verified before shipping

- Unit tests: **1413 / 1413** (macOS). TUI2 self-test: **PASS**. `wcm selftest`: **17 / 17**.
- macOS package: D1–D7 + D9 **PASS**; Windows package: D1 + D9 **PASS**.
- `release-check` (byte-for-byte vs source) **PASS** on both packages:
  mac `1e470179…`, win `b06fb57b…`.
- Windows real-machine: dragging the window repaints immediately; `resize:` and `drain type=4` lines appear in
  `~/.agentruntime/last-tui2-input.txt`; `--anim-probe` reports distinct frames.

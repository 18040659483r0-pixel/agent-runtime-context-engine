# Agent Runtime / WhiteBox · **1.1.7**

> 中文: [RELEASE-1.1.7.zh-CN.md](RELEASE-1.1.7.zh-CN.md) · 日本語: [RELEASE-1.1.7.ja.md](RELEASE-1.1.7.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom.

**Eleventh 1.x beta. A smoother thinking animation, a plainer voice that does not flatten the answer, and a closeout that reads only the workspace aggregates.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.7-macos-arm64.tar.gz` | macOS arm64 | `23382f1d1fc9ffd269f918e5d5e7891d342337df81dcd3efec2c38d1e7afd8d3` |
| `deploy-manager-1.1.7-winx64.tar.gz` | Windows x64 | `ed28f58300bcc8043b430749083ccc110aec43ed05bc1be4059021b8de5490f15` |

> **⚠️ Upgrading from an earlier beta?**
> To keep your corpus / memory (`whitebox/workspace`), install this build with **`--update`**:
>
> ```bash
> dotnet wcm.dll setup --target <your-install-dir> --update --yes
> ```
>
> Do **not** use `--force` / “Overwrite” for upgrades — it wipes the whole install directory.
> This build still auto-backs up an old workspace to `<install-dir>-backup-<timestamp>.zip` before a force install,
> and refuses to wipe if the backup fails — but **make your own backup first** anyway.

## What changed since 1.1.6

### 1) The thinking animation is actually smooth (10 → 30 fps)

- Frame interval `100 ms → 33 ms` (≈30 fps). Measured: **6 → 25 distinct frames per second** with the tail unchanged.
- The root cause was not only the interval: the render gate compared a key that only carried the elapsed clock,
  so during thinking the card was repainted about **once per second**. The animation phase is now part of that key.
- Output is still single-slot coalesced (an old frame is dropped, never queued), so a slow terminal cannot pile frames up.
- A regression test now pins “≥ 24 distinct frames per second”.

### 2) Plain language is a manner, not a style (protocol v116 → v120)

- **v116**: plain language is an *explanation mode* — not a style, not a language version; the report's `body:` stays verbatim.
- **v117** (corrects v116's over-reading): plain language is the manner of the **whole reply**, and it **stacks** with the
  voice set by the head files (lively, warm, the rigorous teacher). Two defects are named: a dry lecture/telegram voice,
  and a reply the reader cannot summarise. Accuracy is never traded for simplicity.
- **v118 — new clause 27**: sort an instruction *before* recording it. One test: *would it still stand if this project
  disappeared?* Yes ⇒ user decision (project copy + workspace aggregate, global id, status). No ⇒ project design
  (that project's `docs/DESIGN*.md`; never a decision entry). Unsure ⇒ project design only, marked
  `(possibly cross-project -> ask)`. A decision entry that names files or modules is a defect.
- **v119 → v120**: closeout reads **only** the three workspace aggregates. Decision anchors carry the **global** id;
  `--l4-sync` never reads project folders. Project files remain backup and attribution.

### 3) Decision numbering: one space, no collisions

- `--next-id decision` now reads **both books** (the aggregate and the library's bare anchors). Before, it reissued
  `D-0025` while the library already used ids up to `D-0048`.
- New provenance gate: every L4 anchor must exist in the workspace aggregate; a project-local anchor is rejected
  (mirror it first). The old gate only asked “does this line carry a D id”, so anchoring into the wrong book stayed green.
- 32 historical project-local decisions were **mirrored** into the aggregate as global ids (`D-0025`–`D-0056`),
  with `src=<project>#<local id>`.

### 4) Launcher / first-instance fix

- “WhiteBox enters first instance on first run”: a manual `setup` rewrote the machine-wide launcher (pitfall #404).
  The launcher now keeps pointing at the install directory that performed the setup.

### 5) A recorded decision is in force from that turn

- A decision written into the live file must be acknowledged by the **next** `[PLAN]`; the knowledge / decision
  libraries still catch up only at closeout.

### 6) Docs and housekeeping

- **An internal project**: four implementation notes were moved from its decision log into its own design document
  (they are design, not user decisions). The remaining decision log keeps only real user orientation.
- Closeout state at the cut: knowledge **v77** (L4 1239), decisions **52/52**, pitfalls **419/419**.

## Verification

- Unit tests (macOS): **1405 / 1405**
- Protocol gates: **14 / 14** (budget **27 lines / 28,068 chars / 7,017 tokens**, cap 7,200)
- `--protocol-show`: **v120**
- `--next-id decision`: **D-0057**
- `--l4-sync --dry-run`: lists only `决策（工作区）` / `坑集（工作区）` / `过程发现（工作区）` — no project paths
- Closeout: decision tree **53 L4 anchors, all verifiable**; convergence **52/52**, gaps 0, orphans 0
- Frame-rate regression test: **25 distinct frames per second** (requirement ≥ 24)


## Notes

- Windows: close WhiteBox/TUI2 before updating (Windows locks binaries).
- An in-place `--update` rewrites the `whitebox` launcher to that install directory — expected.
- Version **1.1.7 (beta)**; protocol **v120**.
- **Security**: the judging layer now normalizes every filesystem target at entry and recognises both `/` and `\`
  in command paths, so a protected path can no longer escape as `Ask`. Regression test included.

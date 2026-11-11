# DEPLOY — WhiteBox 1.1.5 (English)

> 🌐 **English** · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

> **⚠️ Upgrading from v1.1.3 beta (or later)?** To keep your workspace (`whitebox/workspace`), install a newer beta with **`--update`** — never overwrite-install. `--force` wipes the whole install dir (this build auto-backs up an old workspace to a timestamped zip first, and refuses to wipe if the backup fails; still make your own backup).

Five minutes from download to a running WhiteBox. This is a deployment manual, not the architecture paper.

## 0. What you need

| Item | Notes |
|---|---|
| **.NET 10** | `dotnet --version` must be `10.x`. Download: https://dotnet.microsoft.com/download/dotnet/10.0 |
| **Platform** | Two packages: **macOS arm64** and **Windows x64**. Use the one matching your OS; both need **.NET 10**. |
| macOS / Windows / Linux | Each package is built and labelled for its platform |
| A provider key | Default: **DeepSeek official** (`https://api.deepseek.com`, model `deepseek-v4-flash`) |
| Network | Only for the model calls; setup itself is offline |

## 1. Extract

```bash
tar -xzf deploy-manager-1.1.5-<your-platform>.tar.gz   # macos-arm64 / winx64
cd deploy-manager-1.1.5-<your-platform>   # the extracted folder
```

On Windows use any unzip tool; then run the commands below in `cmd` or PowerShell (replace `/` with `\`).

## 2. Run the setup wizard

```bash
dotnet wcm.dll setup
```

It asks four things:

1. **Install directory** — default `~/WhiteBox` (`%USERPROFILE%\WhiteBox` on Windows).
2. **Provider** — 1) DeepSeek official (recommended) or 2) custom OpenAI-compatible (base URL + model).
3. **Reasoning level** — default **low** (fast, cheap, enough). `high` is not recommended; WhiteBox renders its own chain-of-thought, so you will never lose the reasoning view either way.
4. **API key** — masked input (never echoed); stored in `<install>/state/api_key` with `0600`.

Then it installs the runtime, frozen corpus, a 5-node knowledge seed, onboarding docs, the process-findings ledger, builds the knowledge index, and runs `doctor`.

## 3. Start

```bash
./run.sh            # macOS / Linux
run.cmd             # Windows
# or
dotnet wcm.dll run --target ~/WhiteBox
```

## Non-interactive (CI / batches)

```bash
dotnet wcm.dll setup --target /opt/wb --yes --no-run \
  --api-key-file ~/.agentruntime/api_key
```

- `--from-config <file>` reuses an existing WhiteBox `config.xml` (base URL / model / reasoning).
- `--api-key-file <file>` reads the key from a file — **never put a key on the command line**.
- `--think low|medium|high`, `--build` (compile from source if no packaged binaries), `--force` (overwrite target).
- `--dry-run` prints the plan (target / provider / think / key source / payload) **without writing anything**; `--help` prints all flags.
- The last line is machine-readable: `[setup] ok=1 target=…`.

## Provider, context window, and re-configuring

- **Context window** is configured per provider. Setup suggests a default for the chosen model and lets you override it (`--context-window <tokens>`).
  Defaults are conservative: `deepseek-v4-flash` 1,000,000; `gpt-5` 400,000; `gpt-4.1` 1,000,000; `claude-sonnet/opus-4` 200,000; `gemini-2.5/3` 1,000,000; `qwen3`/`kimi-k2` 262,144; unknown → 128,000. **Verify against your provider's docs** — these are defaults, not limits.
- **Multiple providers**: `config.xml` keeps a `Providers` list plus `ActiveProvider`. All entries are stored; **only the active one is used**.
- **Change settings without reinstalling**:

```bash
wcm config --target ~/WhiteBox --list
wcm config --target ~/WhiteBox --provider-name qwen \
  --base-url https://dashscope.aliyuncs.com/compatible-mode/v1 \
  --model qwen3-max --context-window 262144 --think low --active
```

  `wcm config` edits `config.xml` in place; it never touches the workspace. `wcm setup --force` is a full reinstall (wipes the target).

## Layout

```
<install>/
  config.xml                 provider / reasoning / paths
  app/tui|cli/               runtime binaries
  tools/closeout/            closeout tool + declarations
  frozen/                    public frozen corpus
  whitebox/workspace/        clean workspace: seed knowledge, onboarding, findings ledger
  state/                     stream, snapshots, api_key (0600)
  run.sh / run.cmd
```

## Security

- The key lives **inside the installation** (`state/api_key`), never in your home directory and never in `config.xml`.
- Setup never prints the key. Use `--api-key-file` in automation.
- To rotate the key, rerun `setup` (or overwrite `state/api_key` manually).

## Windows verification checklist

This tool has **no Windows-only code path** by design (plain .NET APIs). Two branches are OS-guarded: file mode (`chmod`) and the launcher (`run.sh` / `run.cmd`). When you run it on Windows, verify:

- [ ] `dotnet wcm.dll setup` runs in **cmd / PowerShell**; prompts (zh/en) render without mojibake (setup forces UTF-8 output).
- [ ] Target defaults to `%USERPROFILE%\WhiteBox`; `run.cmd` is created and starts the TUI.
- [ ] Key is written to `<target>\state\api_key` (ACL inherited; `chmod` is skipped on Windows).
- [ ] `dotnet wcm.dll doctor --target <dir>` shows `平台 … Windows` and `启动器 … run.cmd` in green.
- [ ] If the target already exists with read-only files, setup clears attributes before deleting.
- [ ] AI path: `dotnet wcm.dll setup --target <dir> --yes --no-run --api-key-file <file>` completes and prints `[setup] ok=1`.

> The Windows **package** must be built on Windows (or at least built from the same commit) and labelled `winx64`; do not use the macOS package on Windows.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet: command not found` | Install .NET 10 and reopen the terminal |
| `找不到知识树派生层` | Rerun `dotnet wcm.dll setup` (it rebuilds the index) or `app/cli/... --kn-build whitebox/workspace` |
| `401 / unauthorized` | Check `state/api_key` (single line, no quotes) |
| TUI shows old behaviour | Make sure you started the TUI from **this** installation |
| `doctor` red | It names the missing item; fix it and rerun `dotnet wcm.dll doctor --target <dir>` |

## Test it

```bash
dotnet wcm.dll selftest                      # corpus self-test (17/17)
dotnet app/tui/AgentRuntime.Tui2.dll --selftest   # TUI logic self-test
```

## Uninstall

Delete the installation directory. Nothing is written outside it except the key file inside `state/`.


## Downloads

- **Source**: [https://github.com/18040659483r0-pixel/agent-runtime-context-engine](https://github.com/18040659483r0-pixel/agent-runtime-context-engine)
- **All releases**: [https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases)
- **macOS arm64**: [deploy-manager-1.1.5-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz)
- **Windows x64**: [deploy-manager-1.1.5-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz)
- **Checksums**: [macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz.sha256)

# DEPLOY — WhiteBox 1.0.0-beta.1 (English)

> 🌐 **English** · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

Five minutes from download to a running WhiteBox. This is a deployment manual, not the architecture paper.

## 0. What you need

| Item | Notes |
|---|---|
| **.NET 10** | `dotnet --version` must be `10.x`. Download: https://dotnet.microsoft.com/download/dotnet/10.0 |
| **Platform** | **This package: macOS (arm64).** Windows is **not included** in this release (it ships after the Windows port is verified). |
| macOS / Windows / Linux | .NET 10 assemblies are OS-independent, but only the macOS build is provided here |
| A provider key | Default: **DeepSeek official** (`https://api.deepseek.com`, model `deepseek-v4-flash`) |
| Network | Only for the model calls; setup itself is offline |

## 1. Extract

```bash
tar -xzf deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz
cd deploy-manager-1.0.0-beta.1
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

# Deployment test suite (deploy-testsuite)

> English | [中文](README.zh-CN.md) | [日本語](README.ja.md)

**What it tests**: installs once through the real `deploy` flow inside a **sandbox**, runs one real turn against the
**official DeepSeek** endpoint, then verifies fetch de-duplication, restoring the initial library, and moving the whole
directory.

**Corpus**: the deployment edition (`dist/alpha-0.1`’s `templates/workspace` + `frozen`).
**Binaries**: latest source (`--refresh` runs `dotnet publish` into the package’s `app/`; on by default).

## How to run

```bash
# plan only
dotnet run --project tools/deploy-testsuite

# real run (calls official DS; very cheap — D5 asks one question)
dotnet run --project tools/deploy-testsuite -- --run

# options
#   --package <dir>   package root (default dist/alpha-0.1)
#   --proj <dir>      source repo root (auto-detected via AgentRuntime.slnx)
#   --out <dir>       evidence directory (default .stage1/deploy-tests/<timestamp>)
#   --no-refresh      do not publish; use the binaries already in the package
#   --keep            keep the sandbox copy (default: remove pkg/moved, keep report + target)
```

## Criteria (D1–D8)

| # | Scenario | Hard criterion |
|---|---|---|
| D1 | package completeness | 13 required files present (binaries / templates / config / deployer / frozen / knowledge) |
| D2 | init sandbox | `deploy init` rc=0; target gains `config.xml` + a self-contained `app/` |
| D3 | provider = official DS | `BaseUrl=https://api.deepseek.com` · `Model=deepseek-v4-flash` · key only via env/file |
| D4 | doctor + check | both rc=0; knowledge tree 20 nodes, frozen corpus versions 82/2/33 |
| D5 | real-machine TUI | read the first `<!--kn: L1-` entry of `knowledge.md`; output contains the id and `[DONE]`; **rc=0 · 0 refusals** |
| D6 | fetch de-duplication | first fetch `Kind=Knowledge`; second returns only `kn-ref` (`Kind=Hint`) |
| D7 | reset | after polluting a knowledge file, `reset --all` restores the initial sha; `check` stays green |
| D8 | whole-directory move | copy the whole target elsewhere; `doctor` + `check` stay green (no absolute-path dependency) |

## Boundaries

- D5 is the **only** paid segment (one real model call); everything else is offline commands.
- Keys never appear in any artifact: only `ApiKeyEnv` / `ApiKeyFile` are validated.
- The verdict is `ok="1"` in `report.xml`; any red exits 1, never silently.

## Known limits

- Requires .NET 10 and a working DeepSeek key (`~/.agentruntime/api_key` or `AGENTRUNTIME_API_KEY`).
- D8 proves **moving a directory on the same machine**; another machine also needs .NET 10 (`deploy doctor` reports it).

# WhiteBox · Mac 本地版（稳定版）

> **这份副本的名字**：**WhiteBox · Mac 本地稳定版**（代号 `wb-mac`）
> **一句话**：**主人主要用这一份去操作软件公司的其他项目**；**OC 的默认职责 = 只维护这一份**；
> SVN 里那份是**前沿开发版**（想继续开发它时，OC **也可能**被用来操作它，但总的说仍以本地稳定版为主）。
>
> 定调原话（2026-10-15）：「**我们需要一个真正高度稳定版本的本地 WB，是这个时候了**」。
> 本地版**暂不进 SVN**（主人先不接版本库）。

## 目录约定（**甲方案：自包含部署目录** · 2026-10-15 定）

**锚点 = 部署根自己的 `config.xml`**；相对路径一律**相对 config 所在目录**（不是 exe、不是 cwd）。
所以下面这些**永远同层**，整包搬到哪都不变：

| 路径（相对部署根） | 是什么 |
|---|---|
| `config.xml` | ★ **锚点**：`workspace` / `frozen-private` / `state` 都由它算 |
| `workspace/` | 工作区（`AGENTS.md` `BULLETIN.md` `KNOWLEDGE.md` `memory/` `handoff/` …） |
| `frozen-private/` | 冻结语料（A/B 档） |
| `state/` | 运行态（stream / snapshot / focus / tail / draft） |
| `src/` `tests/` `tools/` `benchmark/` `docs/` | 程序与文档 |

> `~/Documents/WhiteBoxWorkspace` 仍在，但它是**指向 `WhiteBox/workspace` 的软链接** —— 两个名字都能用，
> 真实文件只有一份（在部署根里）。

> **为什么这样定死**：部署给别人时**不用管目录层级** —— 程序、配置、工作区、语料永远同一个相对关系。

## 起它

```bash
cd ~/Documents/WhiteBox
dotnet build -c Release
dotnet src/AgentRuntime.Tui/bin/Release/net10.0/AgentRuntime.Tui.dll --config config.xml
```

（配置在**部署根**：`config.xml`；填 provider 见 §要改的配置 / `docs/DEPLOY.md`。）

（详细部署/填 provider 见 `docs/DEPLOY.md`。）

## 要改的配置（**因为工作区换了位置**）

SVN 那份里，配置用相对路径 `../../whitebox/workspace` 指工作区；本副本的工作区在**同级目录**，所以要把这几处的
`lifecycle.workspace` / `frozen.root` / `l3.repo` 等路径改到 `~/Documents/WhiteBoxWorkspace/…`（或改用绝对路径）。

## 两个名字怎么用（以后说话不混）

- **"本地版 / `wb-mac`"** = `~/Documents/WhiteBox`（**稳定版**，OC 日常维护、由它去推 SVN）
- **"前沿版 / SVN 版"** = `…/software-company/projects/AgentRuntime`（**开发版**，接收本地版的推送）

> 起这个名字就是为了以后一句"**本地版**"或"**前沿版**"就说得清是哪一份。
> 想改名（比如叫别的代号）说一声，我把这里和后续记录一起换。

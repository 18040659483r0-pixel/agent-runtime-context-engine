# Agent Runtime / WhiteBox · **1.1.2**

> English: [RELEASE-1.1.2.md](RELEASE-1.1.2.md) · 日本語: [RELEASE-1.1.2.ja.md](RELEASE-1.1.2.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.2
> （本页只是说明；若发布页打不开，用文末的直链）

**第六个 1.x beta。5 条种子的知识库自己长成四层树；首次收尾终于能干净跑完。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.2-macos-arm64.tar.gz` | macOS arm64 | `78c9b37ef01bdb9b4da567fc4d82145c2aa50276b1eea7beb6d9578a0ddbecae` |
| `deploy-manager-1.1.2-winx64.tar.gz` | Windows x64 | `f2448bce25715f2f9d25fbbc23786f66b66ad1729fb2e07e9caefb80e13e9ec6` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.2-<平台>.tar.gz
cd deploy-manager-1.1.2-<平台>
dotnet wcm.dll setup      # 第一屏：选语言
./run.sh                  # Windows：run.cmd
# 安装完也可以直接敲：whitebox
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.1.1 之后改了什么

### 1）四层树自己长（不需要样板）

部署版只带 **5 条 L4 证据**，上面三层（L3 → L2 → L1）过去靠远端模型在空白页上「自己发明格式」——它多半不会，于是链闸门恒红、首收永远像坏了；决策库更狠：**第一条决策直接把用户卡住**。

现在 `closeout --apply` 会跑一个确定性生长器：

- **知识库**：每条 L4 归入一个短名族（`ver` / `pfd` / …），缺哪层补哪层（`<!--kn: … next=… -->` 真实格式）；
- **决策库**：每条带 `D-####` 的 `L4-####` 自动配出 `L1 → L2 → L3 → L4` 链（行格式就是闸门认的那种）；
- 父节点正文标 **「自动生长·待抽象」**，远端 AI 随时可重写：**结构确定、措辞不是**；幂等，只补缺的层。

实测（全新部署 + 1 条过程发现 + 1 条决策）：知识 **L1 6 / L2 6 / L3 6 / L4 6**、决策 **1/1 收敛**，两条链闸门全绿。

### 2）首次收尾能跑完

修掉 4 个只有部署树才会踩的缺口：

- `--apply` 找不到 CLI（源码仓路径）⇒ 部署版改用 `app/cli/…`；
- 没有禁字清单 ⇒ 跳过「公开坑集变体」，不让首收失败；
- `--promote` 没有知识账本 ⇒ 自动建（v0 → v1），不再 rc=2；
- `MEMORYINDEX.md` 模板缺时间线小节 ⇒ 补上；
- 迁移的 L4 正文去空行 ⇒ 不再误报「未标记正文」。

### 3）头部文件第一天就存在

部署包现在带 `SOUL.md`、`IDENTITY.md` 与六份 `*-changeOrder.md`；首次收尾时缺哪个建哪个（**中性模板，不预设人格**），并按 `## 请求` 正文重算水位线指纹。还有 `[open]` 请求时**不抬水位**，不假装收敛。

部署版 `AGENTS.md`（会进冻结 `rules/global.md`）新增**六文件地图**：每份是什么、什么时候用、首访答案该落到哪。

### 4）协议 v108

- 第 15 条补六份头部文件的用途 + 首访三件事的落点；
- 第 21/22 条：**「说人话」只约束措辞**，不覆盖人格 —— 头部文件定的语气、以及 **R2 当轮**的语气要求并行成立；
- 预算 **5,600 → 6,200 token**（实测 23,577 字符 / 5,895 token / 26 行）。协议变更 ⇒ 下次启动缓存归零一次（正常）。

### 5）验证

单测 **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS 包 D1–D7 PASS · Windows 包 D1+D9 PASS。隔离全新部署：首收 `block 红 0（Core 0）`、知识 6/6/6/6、决策 1/1，只剩环境级 report。

## 直接下载

发布页打不开时，直接点下面的链接：

- **macOS arm64**：[deploy-manager-1.1.2-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.2-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-winx64.tar.gz)
- **校验和**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.2/deploy-manager-1.1.2-winx64.tar.gz.sha256)

发布页（Assets 在底部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.2

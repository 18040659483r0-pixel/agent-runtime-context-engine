# Agent Runtime / WhiteBox · **1.1.4**

> English: [RELEASE-1.1.4.md](RELEASE-1.1.4.md) · 日本語: [RELEASE-1.1.4.ja.md](RELEASE-1.1.4.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.4
> 本页只是说明；打不开就用文末直链。

**第八个 1.x beta。CLI 默认英文、决策报告按终端阅读顺序重排、升级不再可能静默吞掉你的语料区。**

> **⚠️ 从 v1.1.3 beta（或之后任何 beta）部署过的用户：**
> 想继续使用原来的语料与记忆（`whitebox/workspace`），安装新版**必须用 `--update`**：
>
> ```bash
> dotnet wcm.dll setup --target <你的部署目录> --update --yes
> ```
>
> **不要**用 `--force` /「1) 覆盖」升级 —— 它会清空整个部署目录。
> 本版在 `--force` 前会把旧 workspace 自动备份为 `<部署目录>-backup-<时间戳>.zip`，备份失败则拒绝清空；
> 但仍建议你**自己先备份一份**。

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.4-macos-arm64.tar.gz` | macOS arm64 | `60f5826db89b92c642027441c52d3b0412cc5fbf56b3aa1a810c95dfbbf3ca54` |
| `deploy-manager-1.1.4-winx64.tar.gz` | Windows x64 | `18f9536a4575981017e1e1545f050afc3e540919dbf005c759995d5d7baa6fee` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.4-<平台>.tar.gz
cd deploy-manager-1.1.4-<平台>
dotnet wcm.dll setup      # 第一步选语言
./run.sh                  # Windows: run.cmd
# 部署完成后也可以直接输入：whitebox
```

手册：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 自 1.1.3 以来的改动

### 1）CLI 文案改为语言包（默认英文）

主 CLI、`tools/closeout`、benchmark、TUI2 help/诊断行全部走同一张语言表，不再写死中文。

- 默认 **英文**；`--lang zh|ja` 切换，也认 `WB_CLI_LANG` / `WB_TUI_LANG` / `config.xml` 的 `UiLanguage`
  （显式参数 > 环境变量 > 配置 > 英文）。
- 漏译会原样回落中文，不会空白；项目闸门会拦住「新增文案没有译文」。

### 2）决策报告按 TUI2 的阅读方式重排

TUI2 总是刷新到最下面最新内容，所以报告把「先读什么」放在最前：

- **术语**与**原料**一起放在顶部、**不编号**（原料不再是「第四章」）。
- 编号固定为 `1 need · 2.x 解步 · 3 结果 · 4.x 过程发现 · 5.x 接下来 · 6 成品正文`。
- `next:` 每条必须写**具体动作**（谁做什么、选它改变什么）；`rec` 只是标记，不是内容；
  空 `next [rec]:` 会被丢弃，不再出现「1. （它建议）」后面没有内容。
- 协议区 **v110**：26 行 / 24,586 字符 / 6,147 token（预算 ≤6,200）。

### 3）知识 / 决策 / 记忆在卡片里高亮

- **知识 / 决策**（`知识树 / 知识库 / 决策库 / 节点 id L1~L4-xxx-nnn`、「写成项目决策」「写成踩坑集」等）
  用**意图色 + 加粗**。
- **记忆**（`记忆 / 记忆索引 / 当日记忆 / MEMORY.md / memory / 日记`）用**结果色 + 加粗**。
- 只加 ANSI 样式；纯文本投影逐字节不变。

### 4）升级不再可能静默吞掉语料区

- 收尾声明拆两处：`tools/closeout/requirements/` = **系统内置**（`--update` 替换），
  `tools/closeout/requirements-user/` = **用户扩展**（保留；同 id 覆盖内置）。
- `--update` 会先把旧 `requirements/` 里「你改过 / 新增」的声明迁到 `requirements-user/`，再替换内置目录。
- `--force` / 覆盖若发现旧 `whitebox/workspace`：先备份为带时间戳的 zip（workspace + `config.xml` + `requirements-user`），
  备份失败则中止，不清空。
- `--update` 不动 `whitebox/workspace`、`config.xml`、`state/api_key`、`frozen/`。

## 验证

- 单测 **1388 / 1388**
- TUI2 自检 **PASS**（含新增高亮断言）
- 部署管理器自检 **17 / 17**
- 包闸门：macOS **D1–D7 PASS**；Windows **D1 + D9 PASS**（跨平台符号闸门：win 包不含 `libc`）
- 隔离升级实测：v1.1.3 → v1.1.4 `--update`；用户声明迁移后仍生效；workspace / config / key 逐字节不变
- 隔离 force 实测：清空前生成 zip，确认含 workspace 标记、`config.xml`、`requirements-user`

## 说明

- Windows 升级前请先完全退出 WhiteBox/TUI2（Windows 会锁二进制）。
- 原地 `--update` 会把 `whitebox` 启动器重写到该部署目录 —— 更新同一目录时这是预期行为。
- 版本仍是 **1.1.4（beta）**；`deployment-template/` 与 `frozen/` 相对 1.1.3 没有变化，本次不需要迁移 workspace。

## 直接下载

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-winx64.tar.gz
- 校验和：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.4/deploy-manager-1.1.4-winx64.tar.gz.sha256
- 发布页：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.4

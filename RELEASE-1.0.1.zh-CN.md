# Agent Runtime / WhiteBox · **1.0.1**

> English: [RELEASE-1.0.1.md](RELEASE-1.0.1.md) · 日本語: [RELEASE-1.0.1.ja.md](RELEASE-1.0.1.ja.md)

> ⬇️ **源码与安装包下载**：都在 **英文发布页**（本页只是说明，没有下载列表）→ [前往发布页 / v1.0.1](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.0.1)

**第二个 1.x beta。这次带上 Windows 包。**

两个部署包，各自带部署管理器：

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.1-macos-arm64.tar.gz` | macOS arm64 | `28cb4ed68dfe7fdfea8d0c4ce195247e4e077da2557f4271a9e744f22526349b` |
| `deploy-manager-1.0.1-winx64.tar.gz` | Windows x64 | `378d9740599df9d988c1036c3760e5fbc871439211b7cc4e02e4baa382902a55` |

两者都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.0.1-<平台>.tar.gz
cd deploy-manager-1.0.1-<平台>
dotnet wcm.dll setup
./run.sh        # Windows 用 run.cmd
```

说明：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.0.0-beta.1 之后的变化

### 1) Windows 包 + 兼容加固

- 新增 `win-x64` 包：同一套部署管理器与运行时，按 Windows 构建。
- 控制台强制 UTF-8（cmd / PowerShell 不乱码）。
- 递归删除先清只读属性。
- 宿主不支持 `ReadKey` 时，掩码输入自动回退普通读行。
- `doctor` 增加**平台**与**启动器**（`run.sh` / `run.cmd`）两项。

### 2) 多 provider（单活跃）+ 每模型上下文窗口

- `config.xml` 支持 `Providers` 列表 + `ActiveProvider`，只使用 active 那个；旧单 provider 配置完全兼容。
- setup 按模型给上下文默认值，可用 `--context-window` 覆盖。默认值取保守值、可改，**以 provider 文档为准**。
- 新增 `wcm config`：就地改 provider / 思考档 / 上下文 / 密钥，**不重装、不动 workspace**。

### 3) 冻结语料路径与部署种子

- 收尾按 `config.xml` 的 `Frozen.Root` 重建/校验 `frozen/`（此前部署版会写到没人读的目录）。
- 部署种子补齐 `MEMORY.md` / `MEMORYINDEX.md` / `DECISION-LIBRARY.md`，首次冻结构建不再因缺源失败。
- `setup` 装完即生成一次部署 frozen，首装即自洽。

### 4) 单 session 设计：worker 退役

- worker / 派发退役：即使 `worker.enabled=true` 或显式列出 `worker-dispatch` 也不会装配。
- CLI `worker` 与 `--worker-run` 直接报「已退役」。
- 只有 master；无子代理。

### 5) 协议 v107 + 会话前缀锁

- 收尾**只在被要求时**（或运行时打印收尾窗口时）执行；日常任务结束只把学到的东西写进活版（坑集 / 决策 / 过程发现）。
- 冻结前缀来源在**会话内绑定一次**；运行中收尾/晋级不会改变当前 prompt。`start`/`reset` 才重新绑定最新版本。

### 6) TUI2 输入

- **↑/↓ 历史**：提交过的文本最多 100 条；↓ 翻过最新一条恢复草稿。
- **短粘贴直插**：≤100 字符直接进输入框；更长才压成一个原子粘贴标签。

## 性能状态

**本 beta 没有做专门的性能优化。** 文中读数是基线测量，不是调优结果；优化会随 beta 系列逐步做，并给前后读数。

## 验证

| 项 | 读数 |
|---|---|
| 单元测试 | **1,370 / 1,370** |
| TUI2 `--selftest` | **PASS** |
| 部署管理器自测 | **17 / 17** |
| macOS 包（全新目录） | 校验 OK → `setup` → `doctor` 全绿 → 部署版 TUI `--selftest` PASS |
| Windows 包 | 以 `win-x64` 构建；首次使用请按 `DEPLOY` 的 Windows 清单逐项核 |

## 已知限制

- 两个包都需要 **.NET 10**。
- Windows 首次使用建议按 `DEPLOY` 清单核（控制台编码 / 密钥路径 / `run.cmd`）。
- 全新部署的知识/决策 L1 为空：首次真实收尾长出 L1 前，这两段冻结语料会跳过（只报告、不阻断）。

## 论文

[中文](README.zh-CN.md) · [English](README.md) · [日本語](README.ja.md)

## 资产（下载）

> 若上面的发布页打不开，可直接点以下链接：

- [macOS arm64: `deploy-manager-1.0.1-macos-arm64.tar.gz`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-macos-arm64.tar.gz)
- [macOS arm64 checksum: `deploy-manager-1.0.1-macos-arm64.tar.gz.sha256`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-macos-arm64.tar.gz.sha256)
- [Windows x64: `deploy-manager-1.0.1-winx64.tar.gz`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-winx64.tar.gz)
- [Windows x64 checksum: `deploy-manager-1.0.1-winx64.tar.gz.sha256`](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.0.1/deploy-manager-1.0.1-winx64.tar.gz.sha256)

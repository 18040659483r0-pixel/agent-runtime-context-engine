# Agent Runtime / WhiteBox · **1.1.9**

> English: [RELEASE-1.1.9.md](RELEASE-1.1.9.md) · 日本語: [RELEASE-1.1.9.ja.md](RELEASE-1.1.9.ja.md)

> ⬇️ **源码与下载**：请到发布页 —— 源码包与安装包在页面底部的 **Assets** 里。
>
> - [macOS arm64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-macos-arm64.tar.gz.sha256)
> - [Windows x64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.9/deploy-manager-1.1.9-winx64.tar.gz.sha256)

**紧急修复：模型写对了的工具调用，不会再无声消失。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.9-macos-arm64.tar.gz` | macOS arm64 | `f9c8e2c2134de501d74854fa3b12759443a54d284b9a52c3f3c9d3e02a968ede` |
| `deploy-manager-1.1.9-winx64.tar.gz` | Windows x64 | `cd4c2529e073f7764abf84909431b3143df1e77c6ec216b826e29a3828043f08` |

> **⚠️ 从旧 beta 升级？**
> 要保留语料 / 记忆（`whitebox/workspace`），请用 **`--update`** 安装：
>
> ```bash
> dotnet wcm.dll setup --target <你的安装目录> --update --yes
> ```
>
> 升级**不要**用 `--force` /「覆盖」—— 那会清空整个安装目录。

## 1.1.8 → 1.1.9 改了什么

### 1) 修复：工具块可能被静默丢弃

- **现象**（winx64 真机）：`write` 调用 41 次，只有 8 次产生结果；其余 33 次**没有任何事件**（无结果、无拒绝、无审批）。模型因此判断「write 坏了」，改走绕路。
- **根因**：块头扫描把**代码围栏 / 引用 / 4 空格缩进**里的行当「引述示例」，整块跳过；若那条回复只有这种块，解析器返回「什么都没有」—— 模型也就什么都看不到。（行式键值本身一直是合法输入：`key: value` / `key<< … >>`。）
- **修法**：这类块不再沉默。
  - 只有被包住的块 ⇒ **整轮拒**，文案点名原因与改法（「`[TOOL]` 必须顶格独占一行」）；
  - 与真块混在一起 ⇒ 真块照跑，另发 `tool-fence` 提示，说明跳过了几个；
  - 块头贴在正文后面（`好的，我来写：[TOOL] write …`）⇒ 同样拒。
- **安全性质不变**：围栏 / 引用 / 缩进里的块**永远不执行**。

### 2) 协议 v121：形状规矩写进契约

第 5 条现在明说：`[TOOL]` 块头必须顶格独占一行；代码围栏、引用、4 空格缩进里的块按**示例**处理、不执行。同时恢复**零转义行式**在值含 `<` / `&` 时的合法地位（此前协议写「行式已退役」，与工具面话术矛盾，现已消除）。协议规模：27 行 / 28,384 字符 / ≈7,096 token（预算 ≤7,200）。

## 发布前已验证

- 单测 **1413 / 1413**（macOS）；TUI2 自检 **PASS**；`wcm selftest` **17 / 17**。
- Windows 侧复验：六条解析用例 **6/6**；真实端到端一回合（行式 `write` + XML 正文 → 文件逐字一致）。
- macOS 包：D1–D7 + D9 **PASS**；Windows 包：D1 + D9 **PASS**。
- `release-check`（与源码逐字节）两包 **PASS**：mac `db64116f…`、win `93135d11…`。

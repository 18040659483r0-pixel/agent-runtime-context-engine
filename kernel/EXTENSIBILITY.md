# EXTENSIBILITY.md —— WB 本地版 · **收尾流程的扩展声明**

> **一句话**：收尾流程是**「要件模块」拼起来的**；**改它 = 改声明，不改代码**。
> 这份文件是**"想改收尾之前必读"的唯一入口**：先说清**哪些能动、哪些不能动**，再给**照做就对的步骤**。
>
> 适用对象：**远端 AI（WB 自己）/ 运维 / 用户** —— 谁要按自己的需求改收尾逻辑，先读这里。
> 相关实现：`tools/closeout/`（收尾工具）· `src/AgentRuntime.Core/Closeout/`（共享声明契约）。
> 主人 2026-10-16 定：**每个要件都是可热插拔模块**；**memory 系统只读（兜底）**；**区分「平时」与「收尾中」两档权限**。

---

## 0. 三十秒判断：我想改 X ⇒ 改哪儿

| 我想… | 改哪里（**唯一处**） | 风险 | 怎么确认改对了 |
|---|---|---|---|
| 加一个收尾要件 | `tools/closeout/requirements/<新id>.xml` + 在 `BuiltinChecks` 里绑判据 | 低 | `--check` 的模块行多出它；`模块声明` 绿 |
| 让某要件**红了不拦人**（扩展） | 该文件 `<Class>extension</Class>` | 低 | 汇总行 `Core N 项阻断 · 扩展 M 项仅上账` |
| 某要件**平时只读、收尾才可写** | `<Outside>r</Outside><During>rw</During>` | 低 | `--check` 模块行显示 `r→rw` |
| **暂时**不要某个要件 | `<Enabled>false</Enabled>`（或把文件移走） | 低 | 它的判据**从清单里消失**（不是"判绿"） |
| 改某条判据的**阈值** | 该文件 `<Checks><Check><Id>…</Id><Params>` | 中 | **指纹变** + 该项红绿翻面 |
| 改某条判据的**严重度** | `<Check><Sev>report</Sev>` | 中 | 该项不再阻断（只上账） |
| 换版本库实现（SVN / 以后 git / 不用） | `tools/closeout/archive.xml` 的 `<Kind>` | 低 | `存档声明` 项 + `--check` 报告头 `ARCHIVE=` |
| 改**收尾协议本身**（顺序闸门 / 指纹 / 收敛点） | 🚫 **不能改** | — | 见 §2 |

**一条硬口径**：**改声明 ⇒ 指纹变；指纹在 `--check` 报告头打印** —— 判据被改过**藏不住**，这是本系统的设计目标之一。

---

## 1. 唯一声明处（先看这里）

```
tools/closeout/requirements/<id>.xml      ← 每个要件一个文件（加/删文件 = 加/删要件）
```

```xml
<Requirement>
  <Id>pitfalls</Id>                       <!-- 稳定 id，必须已知（见下） -->
  <Title>踩坑集</Title>
  <Order>30</Order>                       <!-- 收尾顺序，小的先跑 -->
  <Class>core</Class>                     <!-- core=必达（红⇒阻断） | extension=扩展（红⇒只上账） -->
  <Outside>rw</Outside>                   <!-- 平时权限：r | rw -->
  <During>rw</During>                     <!-- 收尾中权限：r | rw -->
  <Enabled>true</Enabled>
  <Artifacts>                             <!-- 它拥有哪些落点（路径相对**仓库根**；Must=必须存在） -->
    <Artifact><Role>src</Role><Path>docs/PITFALLS.md</Path><Must>true</Must></Artifact>
  </Artifacts>
  <Checks>                                <!-- 可选：只声明你要改的那几条判据 -->
    <Check><Id>pitfalls.placements</Id><Sev>block</Sev><Enabled>true</Enabled>
      <Params><Param><K>todoBaseline</K><V>8</V></Param></Params></Check>
  </Checks>
</Requirement>
```

**认识的模块 id**：`closeout` · `memory` · `rules` · `pitfalls` · `knowledge` · `frozen` · `ledger` · `bulletin` · `handoff` · `docs` · `archive`。
不认识 ⇒ **响亮红**（不静默产出一个空模块）。

**可改的判据 id**（本版只有这 4 个**真的会被兑现**；声明别的 ⇒ **响亮红**）：

| 判据 id | 参数 | 作用 |
|---|---|---|
| `closeout.declarations` | — | 模块声明自证（模块集 / 落点 / 锁定件 / 指纹） |
| `memory.szone` | `minBytes`（默认 300） | 当日记忆文件"像不像占位"的字节下限 |
| `memory.watermark` | `waterGapTolerance`（默认 2） | 收敛点落后多少个提交算"上轮没收完" |
| `pitfalls.placements` | `todoBaseline`（默认取闸门账本） | 坑集里 `todo` 类落点的条数上限 |

> **为什么只列这 4 个**：声明一个**不会被兑现**的 id 等于给一个**假作用** —— 比不写还坏。要放开更多，必须先在代码里把那条判据接到 `SevOf/ParamOf` 上（见 §3.2）。

---

## 2. 🚫 不能动的东西（动了就是破坏稳定性）

| 不能动 | 为什么 | 系统怎么拦 |
|---|---|---|
| **CORE 收尾协议**：先绿才写收敛点 · 收敛点指纹 · 留档 · `--close` 的顺序闸门 | 这是"收尾可信"的地基；它一变，所有判据都失去意义 | 不在声明面里（改它 = 改代码） |
| **锁定件 `memory` 与 `closeout`** | `memory` 是**兜底**（别的模块全坏时远端 AI 还要能干活）；`closeout` 承载声明自证 | 删声明 / 禁用 / 降级 ⇒ **自动按内置补回** + 红 |
| **只读模块的写入** | 「只读」必须是**真写不了**，不是靠纪律 | 收尾工具**写守卫**（fail-closed，退出码 3）；运行时网关**没有该路径的放行项** |
| **`memory` 系统的内容** | 兜底层：不许被收尾改坏 | `<Outside>r</Outside><During>r</During>` ⇒ 两侧都无放行路径 |
| **判据"静默改绿"** | 归一是**症状**，根因是判据写死在 CORE 里（已拆） | Core/Extension 分类 + 生效指纹 |

**三条自检口令**：① 我这次改动**改了哪个文件**？② 它**只有一处**吗？③ `--check` 报告头的**指纹变了吗**？（该变就得变，不该变就没变。）

---

## 3. 正确操作（照做）

> 每一步都**先看再改**；改完**立刻自检**；不对就**回退**（§4）。

### 3.1 加一个要件（4 步）

1. **挑一个已知 id**（不认识就红）。若确实是新概念 ⇒ 先在代码里加 id：`BuiltinChecks.Known`（`tools/closeout/CloseoutRequirements.cs`）。
2. **写声明**：`tools/closeout/requirements/<id>.xml`（照 §1 的形状）。
3. **绑判据**（可选）：在 `BuiltinChecks.Of(id)` 里给它返回判据；**没有判据也是合法的**（本版有 4 个要件只占位：`rules` / `bulletin` / `handoff` / `docs`）。
4. **自检**：`dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --check`
   期望：报告头模块行多出它；`模块声明` 绿；指纹**变了**。

### 3.2 改一条判据的阈值 / 严重度（3 步）

1. **确认该 id 在"可改的 4 个"里**（§1）。不在 ⇒ 先接线：在该判据里把常量换成 `SevOf("<id>", 默认)` / `ParamOf("<id>", "<key>", 默认)`（`CloseoutCli.cs`），再把它登记进 `BuiltinChecks.KnownChecks`。
2. **改声明**：加到对应模块的 `<Checks>` 里。
3. **自检**：`--check` ⇒ 该项**红绿翻面** + **指纹变了**。
   例：`memory.szone` 的 `minBytes` 300→99999 ⇒ `素材区存在性` 变红。

### 3.3 让一个要件"不拦人"（1 步）

`<Class>core</Class>` → `<Class>extension</Class>` ⇒ 它红了只上账（汇总行 `扩展 N 项仅上账`）。
> 用途：**依赖外部世界**的要件（网络 / 仓库 / 第三方）**绝不该拦住收尾** —— 例：`archive`（SVN）出厂即 `extension`，上不了网也能收尾。

### 3.4 换 / 关掉版本库扩展（1 步）

改 `tools/closeout/archive.xml` 的 `<Kind>`：`svn` | `none`。
**「声明即自证」**：声明 `svn` 却证明不了（本仓不是工作副本 / 读不到仓库 URL）⇒ **响亮红**，**绝不静默降级**。

### 3.5 收尾工具与运行时的关系（别踩）

- **收尾能写什么** = 声明里 `During=rw` 的模块的落点。**只读模块的落点，两侧都没有放行路径**。
- 运行时网关（`CloseoutWindow`）与收尾工具（写守卫）读的是**同一份声明** —— 你改一处，两侧同时生效。

---

## 4. 回退（本地 SVN，随时可用）

```bash
cd ~/Documents/WhiteBox
svn diff tools/closeout/requirements/          # 看我改了什么
svn revert tools/closeout/requirements/<id>.xml   # 单件回退
svn revert -R tools/closeout                   # 整块回退
```

- 改坏了 ⇒ **`--check` 会红**（声明读不出来 / 落点越界 / 必达落点缺失 / 锁定件被动）—— **不会静默**。
- 收尾工具另有一份声明备份：`.backup-20261015/`。

---

## 5. 已经踩过的坑（别再踩）

| # | 现象 | 正解 |
|---|---|---|
| 1 | 声明**整件读不出来** | **XML 注释里不能出现两个连续连字符**（`--`）⇒ 用「两个连字符」这类描述代替 |
| 2 | 一读声明就 **NRE** | `DataContractSerializer` **反序列化不跑构造函数** ⇒ **缺的元素是 `null`，不是初始化值** ⇒ 读取处一律 `?? []` / `IsNullOrEmpty` |
| 3 | `svn` 报「尚未纳入版本控制」 | 环境 `LC_ALL=C` 时**中文路径参数被按 latin-1 解** ⇒ 命令前加 `LC_ALL=en_US.UTF-8` |
| 4 | 批量搬文件搬错 | `mv /tmp/*.xml …` 这种通配**先用 `ls` 看一眼**（真机：把 8 个 dump 搬进了声明目录 ⇒ `模块声明` 红） |
| 5 | 给只读模块写文件 | 写卫兵会拒 —— **这不是 bug**。要写就把该落点归给一个「收尾中可写」的模块（例：水位/账本归 `closeout`） |
| 6 | 两个模块都声称同一路径 | **最长前缀优先**（文件级声明压过目录级）⇒ 越具体的声明说了算 |
| 7 | 改了阈值没动静 | 该判据 id **不在"可改清单"里** ⇒ 先接线（§3.2），否则声明的是**假作用** |
| 8 | 删了声明，行为没变 | **声明目录不存在**时才回落到内置默认；**目录在、单件缺** ⇒ 那个要件就**不存在**了 |
| 9 | 只改了一处文字，却连着三处报红 | **同源联动，不是误报**：常驻前缀（改规则区 ⇒ **缓存一次性归零**）· 冻结语料（源变 ⇒ 闸门要求 **bump 版本号**，重跑产物**不会**让它变绿）· 生效指纹（改了判据/权限**必须变**）。照闸门的话术办（例：`AgentRuntime.Cli --bump rules.global`） |

---

## 6. 判定：我这次改动属于哪一档

改完自问三句：

1. **只改了声明吗？**（是 ⇒ 不需要重编；下次跑 `--check`/`--close` 即生效）
2. **`--check` 报告头的指纹变了吗？**（改了判据/权限 ⇒ **必须变**；没变 ⇒ 你的改动没生效）
3. **兜底还站得住吗？** 把其他要件声明全删掉，`memory` 的判据**仍要能出结论**：

```bash
# 只留 closeout + memory，其余声明先移走
--check   ⇒ 期望：模块声明 ✅ · 素材区存在性 ✅ · 水位收敛点 ✅ · 无崩溃 · rc=0
```

> 这一条是**兜底设计**的机械判据：**别的收尾模块全坏，记忆系统仍要能自检**。

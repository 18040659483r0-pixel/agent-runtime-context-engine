# frozen-build —— 确定性语料迁移器

把 **openclaw 工作区**里「有价值的内容」按 AgentRuntime 的 **区 / 层 / 域** 格式，
汇成**冻结语料**（产物默认 `../../frozen-private/`；私有：**SVN 内网入库，GitHub 绝不发布**）。

## 一句话

```bash
cd projects/AgentRuntime
python3 tools/frozen-build/build-frozen-corpus.py --tier A     # 生成 A 档
python3 tools/frozen-build/build-frozen-corpus.py --tier B     # 生成 B 档（A + 项目文档索引）
python3 tools/frozen-build/build-frozen-corpus.py --check       # 幂等校验（CI 用）
python3 tools/frozen-build/build-frozen-corpus.py --spec        # 打印段清单（唯一声明处）
```

## 五条铁则（与 METHODOLOGY §十·7~10 一致）

1. **唯一声明处**：段清单 = `build-frozen-corpus.py` 顶部 `SPEC`（`--spec` 可打印），别处不得再定义结构。
2. **逐字节确定**：LF、无 BOM、无时间戳、索引按 id（NFC）排序 —— 重跑必得同一份字节。
3. **正文与账本分离**：版本头 `<!-- frozen: version=N -->` 与 `_manifest.json` 只服务账本，**不进 prompt**；指纹按**去掉版本头**的正文算。
4. **版本闸门**：源变了而 `versions.json` 没 bump → `--check` 报错。**改内容必须同时改版本号。**
5. **L3 只留索引**：技能 / 踩坑集 / 交接 / 项目文档 / 工具 的**正文不进冻结区**，只留索引；正文将来按需**逐条**进 Append Stream（依赖 V3）。

## 档位

| 档 | 内容 | 用途 |
|---|---|---|
| **A** | 铁则 + 方法论 + 索引 + 记忆索引 | 日常 |
| **B** | A **加**「项目文档索引」段 | 需要项目全景时 |

> A → B 是**尾部之外**的增段（B 的项目索引落在 knowledge 区末尾），**不是纯追加**，切换有缓存代价；两档各自是一套完整前缀。

## 第二区域（Region 2）：他端记忆**不进**冻结区

- 冻结区（Region 1 = `rules` / `knowledge` / `memory-index`）**只装本端内容**。他端个人记忆（如 `workspace/agents/001/`）**不进 SPEC、不进冻结区**。
- 需要时作 **第二区域（Region 2）**：只留**索引**，正文按需**逐条 `--append`** 进流（Append Stream，V3）——与 Region 1 **物理分离**
  ⇒ 他端改动**不动本端前缀字节**，**不需 bump 冻结区版本、不需重跑本工具**。
- 入口：`workspace/agents/001/MEMORY-INDEX.md`（指向 `agents/001/MEMORY.md` 的节级指针）。

## 产物（派生，禁手改）+ 入库口径

`frozen-private/`（A 档）、`frozen-private-b/`（B 档）——**由本工具生成，禁止手改**；
改内容请改**源**（openclaw 工作区）→ 改 `versions.json` → 重跑。

| 去向 | 口径 |
|---|---|
| **SVN（内网 / 家庭局域网）** | ✅ **入库**（主人 2026-09-14 定：内网服务器，无外泄风险） |
| **GitHub 发布物** | ❌ **绝对不允许**（含人名 / 本机路径 / 商业信息） |

发布前**必须**先跑发布扫描（硬红线：私有语料目录 / 本机路径 / 内网地址 / 凭据 / 网盘）：

```bash
cd projects/AgentRuntime
tools/pre-publish-scan.sh <待发布暂存目录>     # exit 0 = 可发布；1 = 禁止发布
```

## 改源的标准动作（顺序别反）

```bash
# 1) 先校验：源改了但没重跑 → 必须报错
python3 tools/frozen-build/build-frozen-corpus.py --check --tier A
# 2) 若报错 → 在 versions.json 里 bump 对应的 version_key
# 3) 再重跑
python3 tools/frozen-build/build-frozen-corpus.py --tier A
python3 tools/frozen-build/build-frozen-corpus.py --tier B --out frozen-private-b
# 4) 再校验一次，必须通过
python3 tools/frozen-build/build-frozen-corpus.py --check --tier A
```

⚠️ **别先重跑再校验**：重跑会覆盖 `_manifest.json`，把「源变未 bump 版本」的告警抹掉（实测踩过）。
⚠️ 源可能被**自动化**改动（如 skill 巡检回写），所以 `--check` 要当成入场仪式，不是一次性验收。

## 为什么要迁移器（而不是手工拼）

- 手工拼的语料**每次都不一样** → 全局指纹变 → 前缀缓存整体失效（实测代价：命中 0、成本 4.6×）。
- 源天然会变（方法论精简、MEMORY 索引化、INDEX 集成）→ 只有「确定性重跑 + 版本闸门」能在变更时**说清变了哪几段**。

## 一键拉起（终端里跑）

```bash
cd projects/AgentRuntime

# A 档（日常）
./run.sh --config src/AgentRuntime.Cli/config.private.json --verbose "你好"

# B 档（含项目文档索引）
./run.sh --config src/AgentRuntime.Cli/config.private-b.json --verbose "你好"
```

实测（2026-09-14，A 档 / 真实端点）：第 1 次 `prompt 24,569 · cached 0`（冷启动自检）；
第 2 次 `cached 24,320 = 64×380（98.99%）· uncached 248`；Runtime 开销 1.1 / 1.3 ms。

> 两个配置只差 `frozen.root` 一行；都只含路径，**不含任何密钥**。

## ⚠️ 分档的两条纪律（2026-09-16 实测）

1. **`--out` 一律显式写全**：`--tier B` **不会**自动改输出目录（默认值是 A 档目录）
   ⇒ 不加 `--out` 会把 **A 档产物覆盖成 B 档内容**（症状：`--check --tier A` 报「产物与重算不一致 + 内容已变但版本未 bump」）。
   ```bash
   python3 tools/frozen-build/build-frozen-corpus.py --tier A                            # A → frozen-private
   python3 tools/frozen-build/build-frozen-corpus.py --tier B --out frozen-private-b     # B → frozen-private-b
   python3 tools/frozen-build/build-frozen-corpus.py --check --tier A
   python3 tools/frozen-build/build-frozen-corpus.py --check --tier B --out frozen-private-b
   ```
2. **源变了先 `--check`（应报错）→ 改 `versions.json` 里**真的变了的那一段**（先重算再改）→ 重建两档 → 再 `--check`（应绿）**。
   本次实例：写坑集（`PITFALLS.md` 是语料源之一）⇒ `knowledge.global` 16 → **17** ⇒ 重建后 A `85a9f89315610b99…` / B `c728b7b2c61459eb…`（均 exit 0）。
   坑集详见 `docs/PITFALLS.md` **#55**。

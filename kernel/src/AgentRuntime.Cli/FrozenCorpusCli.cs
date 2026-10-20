using AgentRuntime.Core.Persistence;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Cli;

/// <summary>
/// **冻结语料构建（C# 版）** —— B4-a：把 <c>tools/frozen-build/build-frozen-corpus.py</c> 搬进运行时。
///
/// <para><b>为什么要搬</b>：收尾流水线里这条是 python ⇒ 只能由远端 AI 点 <c>exec</c> 跑（铁则 R2：运行时唯一能起进程处 = 工具面）。
/// 搬成 C# ⇒ **运行时自己在进程内建语料**，收尾少一步工具面。判据 = **与现产物逐字节等价**。</para>
///
/// <para><b>唯一声明处</b> = 本文件的 <see cref="Spec"/>（与 py 的 <c>SPEC</c> 同形；<c>--spec</c> 可打印）。</para>
/// <para><b>本轮范围（切片 1/3）</b>：<c>rules/global.md</c>（只用到 <c>full</c> / <c>persona</c> / <c>pointer</c> 三个简单生成器）。
/// 其余生成器（<c>knowledge-l12</c> / <c>pitfall-index</c> / <c>project-index</c> / <c>mem-headings</c>）与账本 <c>_manifest.json</c> 待续。
/// </para>
/// </summary>
public static class FrozenCorpusCli
{
    private const string DescMax = "";   // 占位：切片 1 用不到（留给 skill-index）

    /// <summary>段清单（唯一声明处）。<c>Mode</c> 见各生成器。</summary>
    public static readonly (string Out, string VersionKey, (string Title, string Mode, string[] Sources, string Tier)[] Blocks)[] Spec =
    [
        ("rules/global.md", "rules.global",
        [
            ("一、铁则与流程", "full", ["AGENTS.md"], "A"),
            ("二、人格与协作边界", "persona", ["SOUL.md", "IDENTITY.md", "USER.md", "GLOSSARY.md"], "A"),
            ("三、L3 资源指针（正文不在冻结区）", "pointer", [], "A"),
        ]),
        ("knowledge/global.md", "knowledge.global",
        [
            ("一、知识库（L1 法则常驻；L2~L4 按需扩散取）", "knowledge-l1", [], "A"),
            ("二、踩坑集索引（L3：节级）", "pitfall-index", [], "A"),
            ("三、项目文档索引（L3）", "project-index", [], "B"),
        ]),
        ("decision/global.md", "decision.global",
        [
            ("一、取向总纲（L1 · 常驻；决策库全文按需下钻）", "decision-l1", [], "A"),
        ]),
        ("memory/index.md", "memory.index",
        [
            ("一、记忆索引（时间线）", "full", ["MEMORYINDEX.md"], "A"),
            ("二、长期记忆标题索引（索引而非全文）", "mem-headings", ["MEMORY.md"], "A"),
        ]),
    ];

    /// <summary>默认档位（A ⊂ B）：<c>tier</c> 字母序 ⇒ <c>"B" &gt; "A"</c>（与 py 的 <c>b.get("tier","A") &lt;= args.tier</c> 同义）。</summary>
    public const string DefaultTier = "A";

    public static int Run(string workspace, string shared, string outDir, bool check, bool spec, string? versionsFile = null, string? bump = null, string tier = DefaultTier)
    {
        if (tier is not ("A" or "B"))
        {
            Console.Error.WriteLine($"[语料] ❌ 未知档位：{tier}（可选：A / B）");
            return 2;
        }

        if (spec)
        {
            foreach (var (o, key, blocks) in Spec)
            {
                Console.WriteLine($"{o}  version_key={key}");
                foreach (var (title, mode, sources, blockTier) in blocks)
                {
                    Console.WriteLine($"  · {title}  [{mode}/{blockTier}]  {string.Join(", ", sources)}");
                }
            }

            return 0;
        }

        // versions.xml 的落点 = **工具目录**（与 py 的 `here/versions.json` 同）—— 别按 outDir 反推。
        var versionsPath = versionsFile ?? Path.Combine(workspace, "..", "..", "tools", "frozen-build", "versions.xml");
        versionsPath = Path.GetFullPath(versionsPath);
        var versions = ReadVersions(versionsPath);
        var anchors = ReadAnchors(versionsPath);

        // ① 先把三个区都算出来（未落地的 mode ⇒ 如实跳过，不进账本）。
        var rendered = new List<(string Out, string Version, string Text, SectionMeta Meta)>();
        var skipped = new List<string>();
        foreach (var (rel, key, blocks) in Spec)
        {
            var version = versions.TryGetValue(key, out var v) ? v : "1";
            try
            {
                var (text, meta) = RenderWithMeta(workspace, shared, rel, ZoneOf(rel), "global",
                    BlocksFor(tier, blocks), version);
                rendered.Add((rel, version, text, meta));
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"[跳过] {rel}：{ex.Message}");
                skipped.Add(rel);
            }
        }

        // ② --bump：把**源锚**钉到当前正文 sha、版本号 +1，再重写产物。
        if (bump is not null)
        {
            return DoBump(workspace, shared, outDir, versionsPath, rendered, versions, anchors, bump, tier);
        }

        // ③ --check / 写产物。
        var bad = 0;
        foreach (var (rel, version, text, meta) in rendered)
        {
            var target = Path.Combine(outDir, rel);
            if (check)
            {
                if (!File.Exists(target))
                {
                    Console.Error.WriteLine($"[校验] ❌ 缺产物 {rel}");
                    bad++;
                    continue;
                }

                var disk = ReadText(target);
                if (disk != text)
                {
                    Console.Error.WriteLine($"[校验] ❌ 产物与重算不一致（源改过但没重跑）：{rel}");
                    ReportFirstDiff(disk, text);
                    bad++;
                    continue;
                }

                Console.WriteLine($"[校验] ✅ {rel} 与重算逐字节一致（version={version}）");
                continue;
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"[语料] {rel,-22} {Encoding.UTF8.GetByteCount(text) / 1024.0,7:0.0} KB  version={version}");
        }

        var metas = rendered.Select(r => r.Meta).ToArray();
        var keyByOut = Spec.ToDictionary(e => e.Out, e => e.VersionKey, StringComparer.Ordinal);

        if (check)
        {
            // **版本 / 源锚纪律**（与 py 的两条同）：① 产物旧账里「内容变了但版本没动」；② 源与「上次 bump 的锚」不一致。
            var oldManifest = Path.Combine(outDir, "_manifest.xml");
            if (File.Exists(oldManifest))
            {
                foreach (var (relOut, oldVer, oldSha) in ReadOldSections(oldManifest))
                {
                    var now = metas.FirstOrDefault(m => m.Out == relOut);
                    if (now is not null && now.Sha256 != oldSha && now.Version == oldVer)
                    {
                        Console.Error.WriteLine($"[校验] ❌ {relOut} 内容已变但版本未 bump（version={now.Version}）→ 先改 versions.xml");
                        bad++;
                    }
                }

                if (File.Exists(oldManifest) && ReadOldFingerprint(oldManifest) == Fingerprint(metas))
                {
                    Console.WriteLine($"[校验] 指纹未变：{Fingerprint(metas)[..16]}…");
                }
            }

            foreach (var m in metas)
            {
                var key = keyByOut[m.Out];
                if (!anchors.TryGetValue(key, out var was))
                {
                    Console.Error.WriteLine($"[校验] ❌ {m.Out} 源未锚定（key={key}）→ 先跑 --bump {key}（或 --bump all）");
                    bad++;
                }
                else if (was.Tier == tier && was.Sha256 != m.Sha256)
                {
                    Console.Error.WriteLine($"[校验] ❌ {m.Out} 源内容与「上次 bump 的锚」不一致 ⇒ **必须 bump**（改内容就得改版本号）：--bump {key}；重跑产物**不会**让它变绿");
                    bad++;
                }
            }

            if (bad > 0)
            {
                Console.Error.WriteLine($"[校验] ❌ {bad} 项不合格");
                return 1;
            }

            // 主人 2026-10-17 令（只警告不阻断 + 真问题不能被淹没）：**跳过的文件不算绿**。
            // 真机：`knowledge/global.md`（最大的一份语料）因源根缺失被**整份跳过**，汇总却照样印
            // 「✅ 产物与源一致」⇒ 它从未被校验，而所有人以为它是绿的。
            if (skipped.Count > 0)
            {
                Console.Error.WriteLine($"[校验] ⚠️ 仍有 {skipped.Count} 个文件**未被校验**（跳过）：{string.Join("、", skipped)}");
                Console.Error.WriteLine("         ⇒ **这不等于「产物与源一致」**：上述文件本次既没比字节、也没验版本纪律。");
                Console.Error.WriteLine("         修法：把源根指到含 `projects/` 的那一层（本地布局已无该根），或修源根推导。");
                return 0;   // 只警告、不阻断（主人令）；但**绝不印 ✅**
            }

            Console.WriteLine("[校验] ✅ 产物与源一致、版本纪律成立");
            return 0;
        }

        WriteLedger(tier, outDir, metas);
        return 0;
    }

    /// <summary>**声明一次 bump**：源锚钉到当前 sha、版本 +1、产物重写（与 py <c>do_bump</c> 同：拒绝空 bump）。</summary>
    private static int DoBump(string workspace, string shared, string outDir, string versionsPath,
        List<(string Out, string Version, string Text, SectionMeta Meta)> rendered,
        Dictionary<string, string> versions, Dictionary<string, (string Tier, string Sha256)> anchors, string bump, string tier)
    {
        var keyByOut = Spec.ToDictionary(e => e.Out, e => e.VersionKey, StringComparer.Ordinal);
        var chosen = bump == "all" ? keyByOut.Values.ToArray() : [bump];
        var unknown = chosen.Where(k => !keyByOut.ContainsValue(k)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"[bump] ❌ 不认识的 version_key：{string.Join(", ", unknown)}（可选：{string.Join(" / ", keyByOut.Values)} / all）");
            return 2;
        }

        var changed = 0;
        foreach (var (relOut, _, _, meta) in rendered)
        {
            var key = keyByOut[relOut];
            if (!chosen.Contains(key))
            {
                continue;
            }

            var now = meta.Sha256;
            if (anchors.TryGetValue(key, out var was) && was.Tier == tier && was.Sha256 == now)
            {
                Console.WriteLine($"[bump] {key}：源未变（锚已一致）⇒ **拒绝空 bump**");
                continue;
            }

            var oldVersion = versions.TryGetValue(key, out var ov) ? ov : "1";
            versions[key] = (int.TryParse(oldVersion, out var n) ? n + 1 : 2).ToString();
            anchors[key] = (tier, now);
            changed++;
            Console.WriteLine($"[bump] {key}：version {oldVersion} → {versions[key]}（锚 tier={tier} · {now[..12]}）");
        }

        if (changed == 0)
        {
            // `--bump all` = **对齐口径**（收尾流水线 --apply 用）：无变化是正常结果（幂等）⇒ 返回 0。
            // `--bump <key>` = **显式声明**：无变化属错误（拒绝空 bump）⇒ 返回 1。
            return bump == "all" ? 0 : 1;
        }

        WriteVersions(versionsPath, versions, anchors);

        // 产物重写：正文一字不动，只把版本号换成新号（重渲染 = 同一口径，不手改字节）。
        var metas = new List<SectionMeta>();
        foreach (var (relOut, _, _, _) in rendered)
        {
            var key = keyByOut[relOut];
            var blocks = Spec.First(e => e.Out == relOut).Blocks;
            var (text, meta) = RenderWithMeta(workspace, shared, relOut, ZoneOf(relOut), "global",
                BlocksFor(tier, blocks), versions[key]);
            var target = Path.Combine(outDir, relOut);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, new UTF8Encoding(false));
            metas.Add(meta);
        }

        WriteLedger(tier, outDir, [.. metas]);
        Console.WriteLine($"[bump] 已声明 {changed} 处变更；产物已重写（档位 {tier}）。");
        return 0;
    }

    /// <summary>写 <c>versions.xml</c>（A4：JSON → 强类型 XML；键序按 Spec 固定 ⇒ 字节可复现）。</summary>
    private static void WriteVersions(string path, Dictionary<string, string> versions, Dictionary<string, (string Tier, string Sha256)> anchors)
    {
        var keys = Spec.Select(e => e.VersionKey).Distinct(StringComparer.Ordinal).ToArray();
        var doc = new FrozenVersions
        {
            Versions = [.. keys.Select(k => new FrozenVersion
            {
                Key = k,
                Value = versions.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : 1,
            })],
            Sources = [.. keys.Where(anchors.ContainsKey).Select(k => new FrozenAnchor
            {
                Key = k,
                Tier = anchors[k].Tier,
                Sha256 = anchors[k].Sha256,
            })],
        };
        DataContractXml.WriteFile(path, doc);
    }

    /// <summary>读 <c>source_sha256</c> 的锚（key → tier/sha256）。</summary>
    internal static Dictionary<string, (string Tier, string Sha256)> ReadAnchors(string path)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var a in DataContractXml.ReadFile<FrozenVersions>(path).Sources)
        {
            map[a.Key] = (a.Tier, a.Sha256);
        }

        return map;
    }

    /// <summary>读旧账本里每段的 (out, version, sha256)（只认这三项，够判纪律）。</summary>
    internal static IEnumerable<(string Out, string Version, string Sha256)> ReadOldSections(string manifestPath) =>
        File.Exists(manifestPath)
            ? DataContractXml.ReadFile<CorpusManifest>(manifestPath).Sections.Select(s => (s.Out, s.Version, s.Sha256))
            : [];

    internal static string ReadOldFingerprint(string manifestPath) =>
        File.Exists(manifestPath) ? DataContractXml.ReadFile<CorpusManifest>(manifestPath).Fingerprint : string.Empty;

    /// <summary>账本 + `FROZEN.md` + 汇总（与 py 的 <c>write_products</c> 尾部同形）。</summary>
    private static void WriteLedger(string tier, string outDir, IReadOnlyList<SectionMeta> metas)
    {
        var manifest = new CorpusManifest
        {
            Tier = tier,
            Fingerprint = Fingerprint(metas),
            Sections =
            [
                .. metas.Select(m => new CorpusSection
                {
                    Out = m.Out,
                    Zone = m.Zone,
                    Layer = m.Layer,
                    Version = m.Version,
                    Chars = m.Chars,
                    Kb = m.Kb,
                    Sha256 = m.Sha256,
                    Blocks =
                    [
                        .. m.Blocks.Select(b => new CorpusBlock { Title = b.Title, Mode = b.Mode, Chars = b.Chars, Sha256 = b.Sha256 }),
                    ],
                }),
            ],
        };
        DataContractXml.WriteFileAtomic(Path.Combine(outDir, "_manifest.xml"), manifest);
        File.WriteAllText(Path.Combine(outDir, "FROZEN.md"),
            "# 冻结区语料（私有：SVN 内网可入库，**GitHub 绝不发布**）\n\n"
            + "- 本目录由 `dotnet …/AgentRuntime.Cli.dll --frozen-corpus`（C#）**确定性生成**，**不要手改**（原 `tools/frozen-build/build-frozen-corpus.py` 已于 2026-10-12 退役）。\n"
            + "- 改内容请改源（openclaw 工作区）→ 在 `versions.xml` bump 版本 → 重跑 → 收尾。\n"
            + "- 正文与账本分离：`_manifest.xml` 是账本，**不进 prompt**。\n"
            + "- 发布红线：GitHub 发布物**绝不允许**含本目录；发布前先跑 `tools/pre-publish-scan.sh <暂存目录>`。\n",
            new UTF8Encoding(false));

        var total = metas.Sum(m => m.Kb);
        var chars = metas.Sum(m => m.Chars);
        Console.WriteLine($"[语料] 合计 {total:0.0} KB / {chars} 字符 ≈ {chars / 1.88:0} token（档位 {tier}）");
        Console.WriteLine($"[账本] 全局指纹 {Fingerprint(metas)[..16]}…");
    }

    internal static string Fingerprint(IReadOnlyList<SectionMeta> metas) => Sha256(string.Concat(metas.Select(m => m.Sha256)));

    private static string ZoneOf(string rel) =>
        rel.StartsWith("rules/", StringComparison.Ordinal) ? "rules"
        : rel.StartsWith("knowledge/", StringComparison.Ordinal) ? "knowledge"
        : rel.StartsWith("memory/", StringComparison.Ordinal) ? "memory-index"
        : rel.StartsWith("decision/", StringComparison.Ordinal) ? "decision" : "unknown";

    /// <summary>渲染一个区：<c>&lt;!-- frozen: version=N --&gt;</c> + 各块（<c>## 标题</c> + 正文）。</summary>
    /// <summary>一个区的账本条目（与 py 的 meta 同形）。</summary>
    public sealed record SectionMeta(string Out, string Zone, string Layer, string Version, int Chars, double Kb, string Sha256, IReadOnlyList<(string Title, string Mode, int Chars, string Sha256)> Blocks);

    /// <summary>渲染 + 出**账本条目**（两件事同源：正文与账本分离，但都从同一份块文本推出）。</summary>
    public static (string Text, SectionMeta Meta) RenderWithMeta(string ws, string shared, string outRel, string zone, string layer, (string Title, string Mode, string[] Sources, string Tier)[] blocks, string version)
    {
        var parts = new List<string>();
        var metas = new List<(string, string, int, string)>();
        foreach (var (title, mode, sources, _) in blocks)
        {
            var body = BuildBlock(mode, sources, ws, shared);
            if (body is null)
            {
                continue;
            }

            var trimmed = body.TrimEnd();
            parts.Add($"## {Nfc(title)}\n\n{trimmed}\n");
            metas.Add((Nfc(title), mode, Chars(trimmed) + 1, Sha256(trimmed + "\n")));
        }

        var text = $"<!-- frozen: version={version} -->\n" + string.Join('\n', parts);
        var bodyOnly = text[(text.IndexOf("-->\n", StringComparison.Ordinal) + 4)..];
        var meta = new SectionMeta(outRel, zone, layer, version, Chars(bodyOnly),
            Math.Round(Encoding.UTF8.GetByteCount(bodyOnly) / 1024.0, 1), Sha256(bodyOnly), metas);
        return (text, meta);
    }

    /// <summary>**码点**数（与 Python <c>len(str)</c> 同义 —— 代理对按 1 计；<c>string.Length</c> 会按 2 计而在 emoji 上偏大）。</summary>
    internal static int Chars(string s) => s.EnumerateRunes().Count();

    internal static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>档位过滤（A ⊂ B）：<c>b.tier &lt;= tier</c>（字母序，与 py 的 <c>b.get("tier","A") &lt;= args.tier</c> 同义）。</summary>
    internal static (string Title, string Mode, string[] Sources, string Tier)[] BlocksFor(
        string tier, (string Title, string Mode, string[] Sources, string Tier)[] blocks) =>
        [.. blocks.Where(b => string.CompareOrdinal(b.Tier, tier) <= 0)];

    public static string Render(string ws, string shared, (string Title, string Mode, string[] Sources, string Tier)[] blocks, string version)
    {
        var parts = new List<string>();
        foreach (var (title, mode, sources, _) in blocks)
        {
            var body = BuildBlock(mode, sources, ws, shared);
            if (body is null)
            {
                continue;
            }

            parts.Add($"## {Nfc(title)}\n\n{body.TrimEnd()}\n");
        }

        return $"<!-- frozen: version={version} -->\n" + string.Join('\n', parts);
    }

    /// <summary>
    /// **坑集 / 项目文档的源根**：老布局 = <c>&lt;shared&gt;/projects</c>；本地版布局（无 <c>projects/</c>）= 仓根 <c>docs/</c>。
    /// <para>2026-10-17 真机：<c>shared</c> 解析成 <c>~</c> ⇒ <c>~/projects</c> 不存在 ⇒ <c>knowledge/global.md</c> **整份被跳过**
    /// （最大的一份语料从没被校验过）。这里给出**本地布局的回落**，让那份能真正被渲染/校验。</para>
    /// </summary>
    private static string ResolveDocRoot(string shared, string ws)
    {
        var legacy = Path.Combine(shared, "projects");
        if (Directory.Exists(legacy))
        {
            return legacy;
        }

        var project = Path.GetFullPath(Path.Combine(ws, "..", ".."));
        var docs = Path.Combine(project, "docs");
        if (Directory.Exists(docs))
        {
            return docs;   // 本地版布局：坑集与项目文档在仓根 docs/ 下
        }

        throw new InvalidDataException($"找不到文档源根：既无 {legacy}，也无 {docs}");
    }

    private static string? BuildBlock(string mode, string[] sources, string ws, string shared) => mode switch
    {
        "full" => ReadText(Path.Combine(ws, sources[0])).Trim() + "\n",
        "persona" => GenPersona(ws, sources),
        "pointer" => GenPointer(),
        "mem-headings" => GenMemHeadings(ws, sources[0]),
        "knowledge-l1" => GenKnowledgeL1(ws),
        "decision-l1" => GenDecisionL1(ws),
        "pitfall-index" => GenPitfallIndex(ResolveDocRoot(shared, ws)),
        "project-index" => GenProjectIndex(ResolveDocRoot(shared, ws)),
        _ => throw new InvalidDataException($"未知 mode: {mode}（切片 1 之外，待 B4-a 后续）"),
    };

    /// <summary>
    /// **决策库的 L1（常驻）**：从活版 `DECISION-LIBRARY.md` 的 `## L1 …` 段逐行取（不装载 L2~L4）。
    /// <para>批次 3a：渲染器就绪，**尚未登记进 Spec** —— 登记要连带 `versions.xml` 源锚 + 产物 + 提示词接线，
    /// 一次做齐才不破「产物＝重算」这条纪律（见 `DESIGN-DECISION-LIBRARY` §八·一）。</para>
    /// </summary>
    private static string GenDecisionL1(string ws)
    {
        var path = Path.Combine(ws, "DECISION-LIBRARY.md");
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"缺 {path}（决策库活版）");
        }

        var kept = new List<string>();
        var inside = false;
        foreach (var line in File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;   // 到 L2 就停 —— L1 之外一律不常驻（R1 只放 L1）
                }

                inside = line.StartsWith("## L1", StringComparison.Ordinal);
                continue;
            }

            if (!inside || line.StartsWith("> ", StringComparison.Ordinal))
            {
                continue;    // 段首说明是给人看的，不进常驻
            }

            kept.Add(line.TrimEnd());
        }

        var body = string.Join('\n', kept).Trim();
        if (body.Length == 0)
        {
            throw new InvalidDataException("决策库 L1 段为空（`## L1` 下没有任何条目）");
        }

        return body + "\n";
    }

    private static string GenPersona(string ws, string[] sources)
    {
        var parts = new List<string>();
        foreach (var name in sources)
        {
            var path = Path.Combine(ws, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var body = ReadText(path).Trim();
            if (body.Length == 0)
            {
                continue;
            }

            parts.Add($"### {name}\n\n{body}");
        }

        return string.Join("\n\n", parts) + "\n";
    }

    /// <summary>长期记忆**标题级**索引（索引而非全文）—— 与 py <c>gen_mem_headings</c> 同形。</summary>
    private static string GenMemHeadings(string ws, string source)
    {
        var outLines = new List<string>();
        foreach (var line in ReadText(Path.Combine(ws, source)).Split('\n'))
        {
            var m = Regex.Match(line, @"^#{1,3} (.+)$");
            if (m.Success)
            {
                outLines.Add("- " + m.Groups[1].Value.Trim());
            }
        }

        return "长期记忆**是索引而非全文**（论文 §3.4）：以下为条目索引，正文按需 `memory_search` / `memory_get`。\n\n"
             + string.Join('\n', outLines) + $"\n\n共 {outLines.Count} 条。\n";
    }


    /// <summary>知识库的**常驻部分 = 只有 L1**（源 = <c>knowledge/.derived/l1.xml</c>）。</summary>
    private static string GenKnowledgeL1(string ws)
    {
        var resident = AgentRuntime.Core.Knowledge.KnowledgeResident.Load(
            Path.Combine(ws, "knowledge", ".derived"));
        return resident.Text;
    }

    /// <summary>踩坑集**节级**索引（跳公开变体与产物副本；排序键 = 绝对路径的 NFC）。</summary>
    private static string GenPitfallIndex(string root)   // 参数已是**源根本身**（老布局 shared 或本地仓根 docs），不再内部拼 projects
    {
        if (!Directory.Exists(root))
        {
            throw new InvalidDataException($"找不到 {root}");
        }

        var files = Directory.EnumerateFiles(root, "PITFALLS*.md", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(p => !IsSelfProduced(p))   // 自产目录（.stage1 等）**根本不算源**（不是「跳过的副本」）⇒ 不进计数、不进索引
            .OrderBy(p => Nfc(p), StringComparer.Ordinal)
            .ToArray();

        var outLines = new List<string>();
        var total = 0;
        var skipped = 0;
        foreach (var path in files)
        {
            var rel = Nfc(Path.GetRelativePath(root, path));
            if (rel.EndsWith("-public.md", StringComparison.Ordinal) || rel.Contains("/runs/", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            outLines.Add($"### `{rel}`（{new FileInfo(path).Length / 1024} KB）");
            foreach (var line in ReadText(path).Split('\n'))
            {
                if (Regex.IsMatch(line, @"^#{2,3} "))
                {
                    outLines.Add("- " + line.Trim().TrimStart('#', ' ').Trim());
                    total++;
                }
            }

            outLines.Add("");
        }

        var tail = $"共 {files.Length - skipped} 份、{total} 个节条目；正文按需取。";
        if (skipped > 0)
        {
            tail += $"（已跳 {skipped} 份重复/产物副本：公开变体 + benchmark/runs/*）";
        }

        return string.Join('\n', outLines) + "\n" + tail + "\n";
    }

    /// <summary>项目文档索引（B 档；按 py 的多重排除）。</summary>
    private static string GenProjectIndex(string root)   // 参数已是**源根本身**（老布局 shared 或本地仓根 docs），不再内部拼 projects
    {
        if (!Directory.Exists(root))
        {
            throw new InvalidDataException($"找不到 {root}");
        }

        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(p => !p.Contains("/bin/", StringComparison.Ordinal)
                     && !p.Contains("/obj/", StringComparison.Ordinal)
                     && !IsSelfProduced(p)
                     && !Path.GetRelativePath(root, p).Contains("AgentRuntime", StringComparison.Ordinal)
                     && !p.Contains("/runs/", StringComparison.Ordinal)
                     && !p.Contains("/recordings/", StringComparison.Ordinal)
                     && !Path.GetFileName(p).StartsWith("PITFALLS", StringComparison.Ordinal))
            .OrderBy(p => Nfc(p), StringComparer.Ordinal)
            .ToArray();

        var outLines = new List<string>();
        foreach (var path in files)
        {
            var rel = Nfc(Path.GetRelativePath(root, path));
            var m = Regex.Match(ReadText(path), @"^#{1,3} (.+)$", RegexOptions.Multiline);
            var title = m.Success ? Nfc(m.Groups[1].Value.Trim()) : string.Empty;
            outLines.Add($"- `{rel}` — {title}（{new FileInfo(path).Length / 1024} KB）");
        }

        return string.Join('\n', outLines) + $"\n\n共 {outLines.Count} 篇；正文按需取。\n";
    }

    private static string GenPointer() =>
        "以下资源的**正文不在冻结区**（L3），按需取用；索引见 knowledge/global：\n\n"
        + "- 环境 / 部署 / 主机 / 工具：`TOOLS.md`\n"
        + "- 知识库：**`knowledge/knowledge.md`**（只常驻 L1；**L2~L4 用 `[KN] <id>` 按需扩散取**）\n"
        + "- 踩坑集：`projects/*/PITFALLS.md`（清单 = 踩坑集节级索引）\n"
        + "- 跨端交接：`handoff/*.md`（清单 = 交接索引）\n"
        + "- 项目方法论文档：`projects/*/docs/`（清单 = 项目文档索引）\n";

    /// <summary>**自产目录**（§十·1）：运行时/构建/测试自己写出来的东西，一律不进索引（否则「产物↔源」同名互覆/残留假阳性）。
    /// 真机病根（2026-10-12）：影子跑把 <c>.stage1/ms/shadow2/live/docs/PITFALLS.md</c> 落在仓内 ⇒ 踩坑集索引收进
    /// 一份 0 KB 测试残留 ⇒ 语料闸门恒红。</summary>
    internal static bool IsSelfProduced(string fullPath) =>
        fullPath.Contains("/.stage1/", StringComparison.Ordinal)
        || fullPath.Contains("/bin/", StringComparison.Ordinal)
        || fullPath.Contains("/obj/", StringComparison.Ordinal)
        || fullPath.Contains("/.demo/", StringComparison.Ordinal)
        || fullPath.Contains("/dist/", StringComparison.Ordinal);

    /// <summary>报第一处差异（行号 + 两侧原文，截断）—— 判据要说清「改了哪个字节」。</summary>
    private static void ReportFirstDiff(string disk, string want)
    {
        var a = disk.Split('\n');
        var b = want.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<缺行>";
            var y = i < b.Length ? b[i] : "<缺行>";
            if (x != y)
            {
                Console.Error.WriteLine($"  第 {i + 1} 行：");
                Console.Error.WriteLine($"    盘上: {Trunc(x)}");
                Console.Error.WriteLine($"    重算: {Trunc(y)}");
                return;
            }
        }

        Console.Error.WriteLine($"  （行相同但整体不等：长度 {disk.Length} vs {want.Length}）");
    }

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>读文件并归一为 LF、去 BOM（<c>utf-8-sig</c> 同义；.NET 读时会吃掉 BOM）。</summary>
    public static string ReadText(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n');

    internal static string Nfc(string s) => s.Normalize(NormalizationForm.FormC);

    /// <summary>读 <c>versions.xml</c> 的「<c>key: 版本号</c>」对（A4：JSON → 强类型 XML）。
    /// 缺失 ⇒ 空表（调用方回退 <c>"1"</c>）。</summary>
    internal static Dictionary<string, string> ReadVersions(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var v in DataContractXml.ReadFile<FrozenVersions>(path).Versions)
        {
            map[v.Key] = v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return map;
    }

    private static string RepoRootOf(string workspace, string outDir)
    {
        // outDir 默认 = <repo>/frozen-private ⇒ 仓根 = 它上一级。
        return Directory.GetParent(Path.GetFullPath(outDir))?.FullName ?? Path.GetFullPath(outDir);
    }
}

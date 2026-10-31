using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CloseoutTool;

/// <summary>
/// **条目 → L4（机械搬运）**（2026-10-27 主人令）。
/// <para>
/// 口径：坑集 / 决策条目是**扁平条目集**（本仓一份 + **每个项目各一份**）；收尾时每一条都变成对应树的
/// 一条 **L4 节点**（逐字证据），再由远端 AI 从 L4 抽象 L3/L2/L1 并归类。
/// </para>
/// <para>
/// **水位线 = 显式文件**（<c>tools/closeout/l4-watermark.xml</c>），**按源文件各一格**；
/// 与锚点（<c>src=…#id</c>）互为校验：同步写锚 + 抬水位；检查看「水位以上有没有缺锚」。
/// </para>
/// </summary>
internal static class L4Gap
{
    private sealed record Item(string Id, double Key, string Title, string Body);

    private sealed record Source(
        string Label,
        string File,
        string Target,
        string IdPattern,
        string AnchorPattern,
        bool AnchoredByFile);

    private const string PitfallPattern = @"^##\s+(\d[\w.]*)\.\s+(\S.*)$";
    private const string DecisionPattern = @"^##\s+(D-\d{4})\b|^- \*\*\[(D-\d{4})\]";

    private const string PitfallAnchor = @"<!--kn:[^>]*?src=(?<file>[^#>\s]+)#(?<id>[A-Za-z0-9][A-Za-z0-9.]*)(?=-->)";
    private const string DecisionAnchor = @"源\s*L3-[0-9A-Za-z-]+\s*·\s*(D-\d{4})";

    private static string WatermarkPath => Path.Combine(CloseoutCli.Project, "tools", "closeout", "l4-watermark.xml");

    /// <summary>AgentWorkFlow 根（相对路径的统一基准）。</summary>
    private static string RepoParent => Path.GetDirectoryName(CloseoutCli.Project) ?? CloseoutCli.Project;

    /// <summary>所有源：本仓坑集 + 工作区决策 + 每个项目的坑集 / 决策。</summary>
    private static List<Source> Sources()
    {
        var knowledge = Path.Combine(CloseoutCli.Live, "knowledge", "knowledge.md");
        var library = Path.Combine(CloseoutCli.Live, "DECISION-LIBRARY.md");
        var list = new List<Source>
        {
            new("坑集（本仓）", Path.Combine(CloseoutCli.Project, "docs", "PITFALLS.md"), knowledge, PitfallPattern, PitfallAnchor, true),
            new("决策（工作区）", Path.Combine(CloseoutCli.Live, "USER-DECISION-ENTRIES.md"), library, DecisionPattern, DecisionAnchor, false),
        };

        var projects = Path.Combine(RepoParent, "software-company", "projects");
        if (Directory.Exists(projects))
        {
            foreach (var dir in Directory.GetDirectories(projects).OrderBy(static d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var p = Path.Combine(dir, "docs", "PITFALLS.md");
                if (File.Exists(p))
                {
                    list.Add(new Source("坑集（" + name + "）", p, knowledge, PitfallPattern, PitfallAnchor, true));
                }

                var d = Path.Combine(dir, "docs", "DECISIONS.md");
                if (File.Exists(d))
                {
                    list.Add(new Source("决策（" + name + "）", d, library, DecisionPattern, DecisionAnchor, false));
                }
            }
        }

        return list;
    }

    // ================================================================ 水位

    private static Dictionary<string, double> LoadWatermarks()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(WatermarkPath))
        {
            return map;
        }

        try
        {
            foreach (var e in XDocument.Load(WatermarkPath).Root?.Elements("Source") ?? [])
            {
                var file = e.Attribute("file")?.Value;
                if (!string.IsNullOrWhiteSpace(file)
                    && double.TryParse(e.Attribute("last")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    map[file] = v;
                }
            }
        }
        catch
        {
            // 坏文件按「全 0」处理（fail-open 到最低水位；同步时会重写）。
        }

        return map;
    }

    private static void SaveWatermarks(Dictionary<string, double> map)
    {
        var doc = new XDocument(
            new XElement("L4Watermark",
                new XElement("Schema", "2"),
                map.OrderBy(static p => p.Key, StringComparer.Ordinal)
                   .Select(static p => new XElement("Source",
                       new XAttribute("file", p.Key),
                       new XAttribute("last", p.Value.ToString("0.###", CultureInfo.InvariantCulture)))),
                new XElement("Note", "水位 = 已机械搬运进 L4 的最大条目号（含），按源文件各一格。拉到最低 = 0。")));
        Directory.CreateDirectory(Path.GetDirectoryName(WatermarkPath)!);
        doc.Save(WatermarkPath);
    }

    private static string KeyOf(Source source) =>
        Path.GetRelativePath(RepoParent, source.File).Replace('\\', '/');

    // ================================================================ 缺口与同步

    /// <summary>水位**以上**仍缺 L4 的条目（阻断判据）+ 水位以下的存量欠账（只报告）。</summary>
    public static (List<string> AboveWatermark, int Backlog) Gap()
    {
        var marks = LoadWatermarks();
        var above = new List<string>();
        var backlog = 0;

        foreach (var source in Sources())
        {
            var key = KeyOf(source);
            var last = marks.TryGetValue(key, out var v) ? v : 0;
            var anchored = Anchors(source);

            foreach (var item in Items(source))
            {
                if (anchored.Contains(AnchorKey(source, item.Id)))
                {
                    continue;
                }

                if (item.Key > last)
                {
                    above.Add(source.Label + " #" + item.Id);
                }
                else
                {
                    backlog++;
                }
            }
        }

        return (above, backlog);
    }

    /// <summary>机械搬运：把水位以上、且还没有 L4 锚的条目逐字写成 L4 节点；然后抬各源水位。</summary>
    public static int Sync(bool dryRun, int max = 0)
    {
        var marks = LoadWatermarks();
        var budget = max > 0 ? max : int.MaxValue;
        var written = 0;

        Console.WriteLine("[WB] 条目 → L4（机械搬运" + (dryRun ? " · dry-run" : string.Empty) + (max > 0 ? $" · 最多 {max} 条" : string.Empty) + "）");

        foreach (var source in Sources())
        {
            var key = KeyOf(source);
            var last = marks.TryGetValue(key, out var v) ? v : 0;
            var anchored = Anchors(source);
            var pending = Items(source)
                .Where(i => !anchored.Contains(AnchorKey(source, i.Id)) && i.Key > last)
                .OrderBy(i => i.Key)
                .ToList();

            Console.WriteLine($"  {source.Label}：水位 {last:0.###} · 待写 {pending.Count}");
            if (pending.Count == 0 || budget <= 0)
            {
                continue;
            }

            var take = pending.Take(Math.Min(budget, pending.Count)).ToList();
            foreach (var item in take.Take(6))
            {
                Console.WriteLine($"    #{item.Id} {Clip(item.Title, 46)}");
            }

            if (dryRun)
            {
                budget -= take.Count;
                written += take.Count;
                continue;
            }

            var targetPath = Path.IsPathRooted(source.Target) ? source.Target : Path.Combine(RepoParent, source.Target);
            var blocks = new List<string>();
            if (source.AnchoredByFile)
            {
                foreach (var item in take) blocks.Add(ToNode(source, item));
            }
            else
            {
                // 决策：编号接着库里已有的 L4 往下排（同一份库被多个源写，取一次最大值后递增）。
                var seq = NextDecisionSeq(targetPath);
                foreach (var item in take) blocks.Add(ToNode(source, item, seq++));
            }

            AppendBlocks(targetPath, blocks);
            marks[key] = take.Max(i => i.Key);
            budget -= take.Count;
            written += take.Count;
        }

        if (!dryRun)
        {
            // **每个源都有自己的水位线**（哪怕还是 0）：一眼能看出这条源被扫过、没抄过，
            // 也避免「源存在但账本里没有它」时看不出差异（2026-10-31 主人令）。
            foreach (var source in Sources())
            {
                var k = KeyOf(source);
                if (!marks.ContainsKey(k))
                {
                    marks[k] = 0;
                }
            }

            SaveWatermarks(marks);
        }

        Console.WriteLine($"  合计：{(dryRun ? "将写" : "已写")} {written} 条" + (dryRun ? string.Empty : "；水位已抬；下一步让远端 AI 从 L4 抽象 L3/L2/L1 并归类"));
        return 0;
    }

    /// <summary>只读报告（`--l4-gap`）。</summary>
    public static int Run()
    {
        var marks = LoadWatermarks();
        var (above, backlog) = Gap();

        Console.WriteLine("[WB] 条目 → L4 覆盖（水位 = tools/closeout/l4-watermark.xml；按源文件各一格）");
        foreach (var source in Sources())
        {
            var key = KeyOf(source);
            Console.WriteLine($"  {source.Label}：水位 {(marks.TryGetValue(key, out var v) ? v : 0):0.###} · {key}");
        }

        Console.WriteLine($"  水位以上缺 L4：{(above.Count == 0 ? "0 ✅" : above.Count + " ✗ " + string.Join(", ", above.Take(20)) + (above.Count > 20 ? " …" : string.Empty))}");
        Console.WriteLine($"  水位以下存量欠账：{backlog} 条");
        return above.Count == 0 ? 0 : 1;
    }

    // ================================================================ 内部

    /// <summary>列表式坑集：`## 分类` 下的 `1. **标题**`（PhotoSelect / DiskSpaceSaver 的写法）。</summary>
    private const string PitfallListPattern = @"^\s*(\d+)\.\s+\*\*(?<title>.+?)\*\*";

    private static List<Item> Items(Source source)
    {
        if (!File.Exists(source.File))
        {
            return [];
        }

        var lines = File.ReadAllText(source.File).Replace("\r\n", "\n").Split('\n');
        var rx = new Regex(source.IdPattern, RegexOptions.Multiline);
        var items = new List<Item>();

        for (var i = 0; i < lines.Length; i++)
        {
            var m = rx.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var id = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            var title = (m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : lines[i]).Trim();

            var body = new StringBuilder();
            for (var j = i + 1; j < lines.Length && body.Length < 900; j++)
            {
                if (rx.IsMatch(lines[j]))
                {
                    break;
                }

                body.Append(lines[j]).Append('\n');
            }

            var cleanTitle = title.TrimStart('*', ' ', '-');
            if (!source.AnchoredByFile)
            {
                // 决策条目：标题取 `]` 之后的正文（形如 `- **[D-0010] 2026-10-23 上午**`）。
                var after = System.Text.RegularExpressions.Regex.Match(lines[i], @"^-\s*\*\*\[[^\]]+\]\s*(.*)$");
                if (after.Success && after.Groups[1].Value.Trim().Length > 0)
                {
                    cleanTitle = after.Groups[1].Value.Trim().TrimEnd('*', ' ', '\t');
                }
            }

            items.Add(new Item(id, KeyOf(id), cleanTitle, body.ToString().Trim()));
        }

        if (items.Count > 0 || !source.AnchoredByFile)
        {
            return items;
        }

        // **兜底**：整个文件没有 `## N.` 形态时，按编号列表逐条取（序号 = 文件内全局次序号，只追加就稳定）。
        var listRx = new Regex(PitfallListPattern, RegexOptions.Multiline);
        var ordinal = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = listRx.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            ordinal++;
            var body = new StringBuilder();
            for (var j = i + 1; j < lines.Length && body.Length < 900; j++)
            {
                if (listRx.IsMatch(lines[j]) || lines[j].StartsWith("## ", StringComparison.Ordinal))
                {
                    break;
                }

                body.Append(lines[j]).Append('\n');
            }

            items.Add(new Item(ordinal.ToString(CultureInfo.InvariantCulture), ordinal, m.Groups["title"].Value.Trim(), body.ToString().Trim()));
        }

        return items;
    }

    private static HashSet<string> Anchors(Source source)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(source.Target))
        {
            return set;
        }

        var text = File.ReadAllText(source.Target);
        if (source.AnchoredByFile)
        {
            foreach (Match m in new Regex(source.AnchorPattern, RegexOptions.Multiline).Matches(text))
            {
                set.Add(m.Groups["file"].Value.Replace('\\', '/') + "#" + m.Groups["id"].Value);
            }

            return set;
        }

        foreach (Match m in new Regex(source.AnchorPattern, RegexOptions.Multiline).Matches(text))
        {
            set.Add(m.Groups[1].Value.ToUpperInvariant());
        }

        if (!source.AnchoredByFile)
        {
            // 决策库的 L4 行可能暂时没有 `源 L3-…`（待挂），但 D 号一定在行里 ⇒ 以 D 号去重。
            foreach (Match m in new Regex(@"D-\d{4}").Matches(text))
            {
                set.Add(m.Value.ToUpperInvariant());
            }
        }

        return set;
    }

    private static string AnchorKey(Source source, string id) =>
        source.AnchoredByFile ? KeyOf(source) + "#" + id : id.ToUpperInvariant();

    /// <summary>L4 节点 id 的规范形（与 <c>KnowledgeTree.Marker</c> 同一条正则的 id 段）。</summary>
    private static readonly Regex L4IdPattern = new(
        @"^L4-[a-z0-9]{2,16}-[0-9]{3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 生成 L4 节点 id（**规范形**）。
    /// <para>2026-10-29 坑 #372 同族：旧版直接用文件名当短名、用条目号当 seq，
    /// 产生了 <c>L4-pit-digitalabacus-15</c> 这类不合规 id，而解析器会**静默跳过**整条节点
    /// （444 个 L4 只载入 428 个，看着像 next 列表丢末项）。这里显式规范化 + 写入前自检。</para>
    /// </summary>
    private static string L4IdOf(Source source, Item item)
    {
        var rel = KeyOf(source);
        var raw = string.Empty;
        if (!rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = rel.Split('/');
            if (parts.Length > 2) raw = parts[2];
        }

        var shortName = new string(raw.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (shortName.Length > 16) shortName = shortName[..16];

        var seq = item.Id.All(char.IsAsciiDigit) && item.Id.Length < 3
            ? item.Id.PadLeft(3, '0')
            : item.Id;

        var id = shortName.Length >= 2 ? "L4-" + shortName + "-" + seq : "L4-pit-" + seq;

        if (!L4IdPattern.IsMatch(id))
        {
            throw new InvalidDataException(
                $"L4 id 不合规范（{id}，源 {rel}#{item.Id}）——规范形是 L4-<短名[a-z0-9]{{2,16}}>-<seq 三位>；" +
                "宁可不写，也不许写入会被解析器静默跳过的节点。");
        }

        return id;
    }

    private static string ToNode(Source source, Item item, int decisionSeq = 0)
    {
        var rel = KeyOf(source);

        if (!source.AnchoredByFile)
        {
            // **决策条目 → 决策库自己的形态**（2026-10-31 修）：
            // 决策库的链闸门只认 `- **L4-####** …`（`L[1-4]-\d{4}`），旧实现写成 `<!--kn: L4-dcs-D-0010 -->`
            // ⇒ 写进去也**不算 L4**，于是「项目决策永远长不出新 L4」。这里按库的形态逐字落一行，
            // 父（源 L3-####）留给后续抽象/挂接（WB 的自动生长），没有父就是显式的「待挂」。
            var verbatim = string.Join(' ', new[] { item.Title, item.Body }
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Replace("\r", " ").Replace("\n", " ").Trim()));
            return "- **L4-" + decisionSeq.ToString("0000", CultureInfo.InvariantCulture) + "** （待挂）· "
                   + item.Id + "：「" + Clip(verbatim, 240) + "」";
        }

        var id = L4IdOf(source, item);
        var head = "<!--kn: " + id + " src=" + rel + "#" + item.Id + "-->";

        return head + "\n"
               + "**坑 " + item.Id + "**：" + Clip(item.Title, 160) + "\n\n"
               + Clip(item.Body, 700) + "\n";
    }

    private static int NextDecisionSeq(string path)
    {
        if (!File.Exists(path))
        {
            return 1;
        }

        var max = 0;
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\*\*L4-(\d{4})\*\*"))
        {
            if (int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return max + 1;
    }

    private static void AppendBlocks(string path, IEnumerable<string> blocks)
    {
        var text = File.ReadAllText(path);
        var addition = "\n" + string.Join("\n", blocks) + "\n";

        if (path.EndsWith("DECISION-LIBRARY.md", StringComparison.OrdinalIgnoreCase))
        {
            // 插在**最后一条 L4 行**之后（保持 L4 小节形态）；没有 L4 才退回「收敛状态」之前 / 文件末尾。
            var lastL4 = -1;
            var at = 0;
            foreach (var line in text.Split('\n'))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^-\s*\*\*L4-\d{4}\*\*"))
                {
                    lastL4 = at + line.Length;
                }

                at += line.Length + 1;
            }

            if (lastL4 >= 0)
            {
                text = text[..lastL4] + "\n" + string.Join("\n", blocks) + text[lastL4..];
            }
            else
            {
                var mark = text.IndexOf("## 收敛状态", StringComparison.Ordinal);
                text = mark < 0 ? text + addition : text[..mark] + addition + "\n" + text[mark..];
            }
        }
        else
        {
            text += addition;
        }

        File.WriteAllText(path, text);
    }

    private static double KeyOf(string id)
    {
        // **id 里可能带前缀**（决策号 `D-0010`）：旧实现遇到首字母就 break ⇒ 所有决策条目 key=0、
        // 永远不大于水位 0 ⇒ 决策类源一条都搬不出去（2026-10-31 真机）。
        var m = System.Text.RegularExpressions.Regex.Match(id, @"\d+(?:\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string Clip(string text, int max)
    {
        var one = text.Replace("\r", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}

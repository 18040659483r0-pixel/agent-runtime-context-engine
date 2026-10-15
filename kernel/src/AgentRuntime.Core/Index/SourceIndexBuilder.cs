using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Index;

/// <summary>
/// **扫真身、建投影**（批次 1 事项 ①）：只扫 <c>src/</c>，解析 C# 顶层声明（命名空间 / 类型 / 行号）并建三类边。
/// <para>
/// 它是**唯一**把「真身文件」变成「地图」的地方 —— 地图与真身的关系是「可重建」，不是「同步」：
/// 任何时候不一致，就以真身为准重建（<see cref="SourceIndexStore.Check"/>）。
/// </para>
/// <para>
/// <b>解析口径（诚实的边界）</b>：按行做**大括号深度**扫描 + 正则认类型头，跳过原始字符串（<c>"""</c>）与行注释。
/// 它不建语法树 ⇒ 对「宏/字符串里的假类名」「跨行声明」等边界不做保证；
/// 这正符合本批定位：**地图只用来缩小搜索面**，真相永远回真身。
/// </para>
/// </summary>
public static class SourceIndexBuilder
{
    /// <summary>依赖边的上限（防止「一个名字到处出现」把投影撑爆；确定性截断：排序后取前 N）。</summary>
    public const int MaxDependsOnEdges = 5000;

    private static readonly Regex NamespaceRe = new(
        "^\\s*namespace\\s+([A-Za-z_][\\w.]*)",
        RegexOptions.Compiled);

    private static readonly Regex TypeRe = new(
        "^\\s*(?<mods>(?:(?:public|internal|protected|private|sealed|abstract|static|partial|readonly|ref|unsafe|new)\\s+)*)"
        + "(?<kind>class|struct|interface|enum|record)\\s+(?<name>[A-Za-z_]\\w*)",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierRe = new("[A-Za-z_]\\w*", RegexOptions.Compiled);

    private static readonly Regex StringLiteralRe = new("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);

    private static readonly Regex CharLiteralRe = new("'(?:\\\\.|[^'\\\\])*'", RegexOptions.Compiled);

    /// <summary>扫 <paramref name="root"/> 下的 <c>src/</c> 建投影（跳过 <c>bin</c> / <c>obj</c>）。</summary>
    public static SourceIndex Build(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.GetFullPath(root);
        var srcDir = Path.Combine(full, "src");
        if (!Directory.Exists(srcDir))
        {
            throw new DirectoryNotFoundException($"找不到源码根：{srcDir}（SourceIndex 只扫 src/）。");
        }

        var files = new List<IndexedFile>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Enumerate(srcDir))
        {
            var rel = Path.GetRelativePath(full, path).Replace('\\', '/');
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            texts[rel] = text;
            files.Add(new IndexedFile(rel, bytes.LongLength, Sha(bytes), ParseTypes(rel, text)));
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new SourceIndex(full, files, BuildEdges(files, texts));
    }

    /// <summary>正文 sha 的前 <see cref="SourceIndex.ShaChars"/> 位（小写十六进制）。</summary>
    public static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..SourceIndex.ShaChars].ToLowerInvariant();

    private static IEnumerable<string> Enumerate(string srcDir)
    {
        return Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(static p =>
            {
                var s = p.Replace('\\', '/');
                return !s.Contains("/bin/", StringComparison.Ordinal) && !s.Contains("/obj/", StringComparison.Ordinal);
            })
            .OrderBy(static p => p, StringComparer.Ordinal);
    }

    /// <summary>
    /// 解析一个文件的顶层声明。
    /// <para>深度 ≤ 1（0 = 文件作用域命名空间/无命名空间；1 = 传统命名空间内）且在类型关键字行首认声明。</para>
    /// </summary>
    public static List<IndexedType> ParseTypes(string rel, string text)
    {
        var types = new List<IndexedType>();
        var ns = string.Empty;
        var depth = 0;
        var inRaw = false;
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];

            // 原始字符串（"""…"""）里的大括号不算结构：成对出现即进出。
            var triple = CountOccurrences(raw, "\"\"\"");
            if (inRaw)
            {
                if (triple % 2 == 1)
                {
                    inRaw = false;
                }

                continue;
            }

            if (triple % 2 == 1)
            {
                inRaw = true;
                continue;
            }

            var nsMatch = NamespaceRe.Match(raw);
            if (nsMatch.Success)
            {
                ns = nsMatch.Groups[1].Value;
            }

            var stripped = StripLiterals(raw.Split("//", 2)[0]);
            var typeMatch = TypeRe.Match(stripped);
            if (typeMatch.Success && depth <= 1)
            {
                types.Add(new IndexedType(ns, typeMatch.Groups["kind"].Value, typeMatch.Groups["name"].Value, rel, i + 1));
            }

            depth += CountBraces(stripped);
            if (depth < 0)
            {
                depth = 0;
            }
        }

        return types;
    }

    private static IReadOnlyList<IndexEdge> BuildEdges(IReadOnlyList<IndexedFile> files, IReadOnlyDictionary<string, string> texts)
    {
        var edges = new List<IndexEdge>();

        // ① Contains：文件 → 类型。
        foreach (var f in files)
        {
            foreach (var t in f.Types)
            {
                edges.Add(new IndexEdge(IndexEdgeKind.Contains, f.Path, t.FullName));
            }
        }

        // ② DependsOn：文件 → 别处声明的类型（按标识符出现判；排除本文件自己声明的名字）。
        var bySimpleName = new Dictionary<string, List<IndexedType>>(StringComparer.Ordinal);
        foreach (var t in files.SelectMany(static f => f.Types))
        {
            if (!bySimpleName.TryGetValue(t.Name, out var list))
            {
                bySimpleName[t.Name] = list = [];
            }

            list.Add(t);
        }

        var depends = new List<IndexEdge>();
        foreach (var f in files)
        {
            var own = f.Types.Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var id in IdentifierRe.Matches(texts[f.Path]).Select(static m => m.Value).Distinct(StringComparer.Ordinal))
            {
                if (own.Contains(id) || !bySimpleName.TryGetValue(id, out var declared))
                {
                    continue;
                }

                foreach (var t in declared)
                {
                    if (!string.Equals(t.File, f.Path, StringComparison.Ordinal))
                    {
                        depends.Add(new IndexEdge(IndexEdgeKind.DependsOn, f.Path, t.FullName));
                    }
                }
            }
        }

        edges.AddRange(depends
            .Distinct()
            .OrderBy(static e => e.From, StringComparer.Ordinal)
            .ThenBy(static e => e.To, StringComparer.Ordinal)
            .Take(MaxDependsOnEdges));

        // ③ TestedBy：<类型>Tests.cs ⇒ 该类型（**歧义不猜**：简单名对应多个声明时不建边）。
        foreach (var f in files)
        {
            var name = Path.GetFileName(f.Path);
            if (!name.EndsWith("Tests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var target = name[..^"Tests.cs".Length];
            if (target.Length == 0 || !bySimpleName.TryGetValue(target, out var declared) || declared.Count != 1)
            {
                continue;
            }

            edges.Add(new IndexEdge(IndexEdgeKind.TestedBy, declared[0].FullName, f.Path));
        }

        return edges;
    }

    private static int CountOccurrences(string line, string token)
    {
        var n = 0;
        for (var i = line.IndexOf(token, StringComparison.Ordinal); i >= 0; i = line.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private static int CountBraces(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == '{')
            {
                n++;
            }
            else if (c == '}')
            {
                n--;
            }
        }

        return n;
    }

    private static string StripLiterals(string line) =>
        CharLiteralRe.Replace(StringLiteralRe.Replace(line, "\"\""), "''");
}

using System.Text;

namespace AgentRuntime.Cli;

/// <summary>
/// **文档索引区重建**（B4：`whitebox/workspace/scripts/knowledge-index.py` 的 C# 移植，0 依赖、逐字节等价）。
/// <para>
/// 管两处：<c>BULLETIN.md</c> 顶部「🧭 索引」与 <c>memory/收尾水位.md</c> 顶部「⚡ 快速读」。
/// 纪律（与 Python 版逐条对齐）：顶部索引区**脚本生成、不手改**；**正文逐字节不变**（移植成 <c>结尾断言</c>）；
/// 行号锚点二次校准；LF / 无 BOM / 无时间戳 ⇒ 同源重跑字节相同；
/// <c>scripts/index-note.txt</c> / <c>scripts/wl-note.txt</c> 存在时用它们的行逐字替换默认说明。
/// </para>
/// <para>用法：<c>--doc-index &lt;ws&gt;</c> 重建；<c>--doc-index-check &lt;ws&gt;</c> 只检查（有需重建则退 1）。</para>
/// </summary>
public static class IndexZoneCli
{
    public const string MarkBulletin = "## 🧭 索引（先读这里 · 正文在下方按需取）";
    public const string MarkWatermark = "## ⚡ 快速读（只读本节；正文是归档，按需下翻）";

    public static int Run(string ws, bool check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ws);
        var root = System.IO.Path.GetFullPath(ws);

        var changed = new List<string>();
        var skipped = new List<string>();

        foreach (var target in new[]
                 {
                     (Path: System.IO.Path.Combine(root, "BULLETIN.md"), Mark: MarkBulletin, Kind: "bulletin"),
                     (Path: System.IO.Path.Combine(root, "memory", "收尾水位.md"), Mark: MarkWatermark, Kind: "watermark"),
                 })
        {
            if (!File.Exists(target.Path))
            {
                skipped.Add(System.IO.Path.GetRelativePath(root, target.Path));   // 别端可以没有水位文件：跳过，不报错
                continue;
            }

            var text = File.ReadAllText(target.Path);
            var (fresh, body) = target.Kind == "bulletin"
                ? Rebuild(text, target.Mark, (head, b) => BuildBulletin(root, head, b))
                : Rebuild(text, target.Mark, (head, b) => BuildWatermark(root, head, b));

            if (!fresh.EndsWith(body, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{target.Path}: 正文被改动，已中止");   // 与 Python 的 assert 同义
                return 2;
            }

            if (!string.Equals(fresh, text, StringComparison.Ordinal))
            {
                changed.Add(System.IO.Path.GetRelativePath(root, target.Path));
                if (!check)
                {
                    File.WriteAllText(target.Path, fresh, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
        }

        if (check)
        {
            Console.WriteLine(changed.Count == 0
                ? "索引区最新 ✅"
                : $"索引区需重建：{string.Join(", ", changed)}");
            return changed.Count == 0 ? 0 : 1;
        }

        var message = $"已重建索引区：{(changed.Count == 0 ? "无变化" : string.Join(", ", changed))}";
        if (skipped.Count > 0)
        {
            message += $"（跳过不存在：{string.Join("、", skipped)}）";
        }

        Console.WriteLine(message);
        return 0;
    }

    /// <summary>剥离旧索引区（mark … 第一处 <c>\n---\n</c>）⇒ 用 build 重建；返回（新文本，正文）。</summary>
    private static (string Fresh, string Body) Rebuild(string text, string mark, Func<string, string, string> build)
    {
        string head;
        string body;
        var i = text.IndexOf(mark, StringComparison.Ordinal);
        if (i >= 0)
        {
            var j = text.IndexOf("\n---\n", i, StringComparison.Ordinal);
            j = j < 0 ? text.Length : j + "\n---\n".Length;
            head = text[..i];
            body = text[j..].TrimStart('\n');       // Python: lstrip("\n")
        }
        else
        {
            var k = text.IndexOf("## ", StringComparison.Ordinal);
            k = k < 0 ? text.Length : k;
            head = text[..k];
            body = text[k..];
        }

        return (head + build(head, body) + body, body);
    }

    private static string BuildBulletin(string root, string head, string body)
    {
        var lines = body.Split('\n');
        var heads = new List<(string Title, int Line)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                heads.Add((lines[i][3..].Trim(), i + 1));
            }
        }

        var note = NoteLines(root, "index-note.txt") ?? new List<string>
        {
            $"> **读法铁则**：本文件已索引化——**默认只读本节**（下表 {heads.Count} 行）；命中哪条，"
            + "用 `read <本文件> offset=<正文 L> limit=40` 只取那一节正文。**禁止全量 read**。",
            "> 生成方式：`AgentRuntime.Cli --doc-index <ws>` 自动重建（收尾时重跑，不手改本区）。",
        };

        var off = 0;
        var block = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            block = new List<string> { MarkBulletin, "" };
            block.AddRange(note);
            block.Add("");
            block.AddRange(heads.Select(h => $"- {h.Title}  ·  正文 L{h.Line + off}"));
            block.AddRange(["", "---", ""]);

            var joined = head + string.Join('\n', block);
            var newOff = joined.Count(c => c == '\n');
            if (newOff == off)
            {
                break;
            }

            off = newOff;
        }

        return string.Join('\n', block);
    }

    private static string BuildWatermark(string root, string head, string body)
    {
        var lines = body.Split('\n');
        var heads = lines
            .Where(l => l.StartsWith("## 水位更新", StringComparison.Ordinal))
            .Select(l => l[3..].Trim())
            .ToList();

        var note = NoteLines(root, "wl-note.txt") ?? new List<string>
        {
            "> **读法铁则**：收尾/拉起时**只读本节**；`## 规则` 与 `## ⏳ 收尾待办` 各读一次即可，"
            + "历史「水位更新」小节是归档，**除非追溯某天细节，否则不要 read**。",
            "> 生成方式：`AgentRuntime.Cli --doc-index <ws>` 自动重建（收尾时重跑，不手改本区）。",
        };

        var block = new List<string> { MarkWatermark, "" };
        block.AddRange(note);
        block.Add("");
        block.Add("- **当前水位**：见下方 `## 水位（每次收尾后更新）`（紧跟本块之后）");
        block.Add("- **收尾待办**：下方 `## ⏳ 收尾待办（…）` —— 收尾时**必读**");
        block.Add($"- **历史水位索引（{heads.Count} 条，正文在下方）**：");
        block.Add("");
        block.AddRange(heads.Select(h => $"  - `{Strip(h)}`"));
        block.AddRange(["", "---", ""]);
        return string.Join('\n', block);

        // Python: h.replace("水位更新（", "").replace("）", "", 1) —— 去掉前缀，再删**第一个**「）」。
        static string Strip(string h)
        {
            var s = h.Replace("水位更新（", string.Empty, StringComparison.Ordinal);
            var idx = s.IndexOf('）');
            return idx < 0 ? s : s.Remove(idx, 1);
        }
    }

    /// <summary>本区开头的说明行：优先用 <c>scripts/&lt;name&gt;</c>（逐字），否则 null（回落到内置默认）。</summary>
    private static List<string>? NoteLines(string root, string name)
    {
        var path = System.IO.Path.Combine(root, "scripts", name);
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path)
            .Split('\n')
            .Select(l => l.TrimEnd('\n', '\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
    }
}

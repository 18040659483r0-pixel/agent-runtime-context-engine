// 切片 1：`shadow2/metrics.py` → C#。
// 口径（主人 2026-10-11 14:0x 定）：任务轮数/模型时/completion = 走到**最后一个「干活」turn 出结果**为止；
// 若本轮出过 `report-request` Hint ⇒ 最后那个 turn 是**报告轮**，单列、不计入任务口径。
// 判据：对同一对 (stream, usage) 与 metrics.py **逐字节等价**（纪律：判据与盘上产物逐字节等价）。
namespace MsSuite;

internal static class Metrics
{
    public static int Run(string[] a)
    {
        if (a.Length != 4)
        {
            Console.Error.WriteLine("用法：ms-suite metrics <task> <stream.xml> <usage.xml> <wall>");
            return 2;
        }

        var (task, vol, usage, wall) = (a[0], a[1], a[2], a[3]);

        var ev = StreamXml.Events(vol);
        var isReport = ev.Any(e => Get(e, "Kind") == "Hint" && Get(e, "Source") == "report-request");
        var denials = ev.Count(e => Get(e, "Kind") == "ToolDenied");
        var tools = ev.Count(e => Get(e, "Kind") == "ToolResult");

        var turns = StreamXml.Events(usage);
        var n = turns.Count;
        var tr = isReport && n > 1 ? turns.Take(n - 1).ToList() : turns;

        Console.WriteLine(string.Join('\t',
            task,
            $"turns_all={n}",
            $"turns_task={tr.Count}",
            $"tools={tools}",
            $"denials={denials}",
            $"comp_task={(int)Sum("CompletionTokens", tr)}",
            $"comp_all={(int)Sum("CompletionTokens", turns)}",
            $"ms_task={(int)Math.Round(Sum("TotalMs", tr))}",
            $"ms_all={(int)Math.Round(Sum("TotalMs", turns))}",
            $"unc_task={(int)Sum("UncachedTokens", tr)}",
            $"cached_task={(int)Sum("CachedTokens", tr)}",
            $"report={(isReport ? 1 : 0)}",
            $"wall={wall}"));

        return 0;
    }

    private static string? Get(Dictionary<string, string> d, string k) => d.GetValueOrDefault(k);

    private static double Sum(string tag, List<Dictionary<string, string>> rows)
    {
        var tot = 0.0;
        foreach (var r in rows)
        {
            if (double.TryParse(r.GetValueOrDefault(tag) ?? "", out var v))
            {
                tot += v;
            }
        }

        return tot;
    }
}

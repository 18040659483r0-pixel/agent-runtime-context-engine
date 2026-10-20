using System.Globalization;
using System.Text;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;

namespace AgentRuntime.Tui2;

/// <summary>
/// **顶部信息块**（复刻 TUI1 的「会话 / 区栈 / 白板 / 草稿 / 焦点」五行）。
/// <para>
/// TUI2 里它不再常驻：**Tab 切换显示/隐藏**。数据口径与 TUI1 同源：
/// 生命周期 = <see cref="LifecycleAggregator"/> 对事件流的投影；区栈 = <see cref="PromptComposition"/>；
/// 自报三块 = 三个模块的 <c>Current</c>。
/// </para>
/// </summary>
public sealed class TopBandProvider
{
    private readonly RuntimeHost _host;
    private readonly IReadOnlyList<LifecycleTurnUsage> _usages;
    private readonly TaskClock _clock;
    private readonly Dictionary<StackRegion, int> _lastBytes = new();
    private bool _hasBaseline;
    private long _lastRefresh;
    private IReadOnlyList<string> _lines = Array.Empty<string>();

    public TopBandProvider(RuntimeHost host, IReadOnlyList<LifecycleTurnUsage> usages, TaskClock clock)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _usages = usages ?? throw new ArgumentNullException(nameof(usages));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>刷新读数（默认 1 秒节流；按 Tab 的那一下可以 force）。</summary>
    public void RefreshIfDue(int cols, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastRefresh < 1000) return;
        _lastRefresh = now;

        try
        {
            _lines = Build(Math.Max(20, cols));
        }
        catch (Exception ex)
        {
            Utf8InputDriver.Trace("top-band: " + ex);
            _lines = new[] { "ⓘ AgentRuntime TUI", " 会话 | 读数暂不可用" };
        }
    }

    private IReadOnlyList<string> Build(int cols)
    {
        var modules = _host.Modules;
        var moduleCount = modules.Count(static m => m is not ProtocolModule and not IFrozenZoneModule);
        var stream = modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        var life = stream is null
            ? null
            : LifecycleAggregator.Aggregate(stream.Snapshot(), _usages).Current;

        var session = new List<string> { "会话", "protocol v" + ProtocolText.Version, $"{moduleCount} 模块" };
        var taskLabel = _clock.Label(life?.Id);
        if (taskLabel.Length > 0) session.Add(taskLabel);

        if (life?.Usage is { } usage)
        {
            session.Add($"本 task 新增 {Number(life.AppendedChars / 4)} tok");
            session.Add($"缓存命中 {Percent(usage.HitRate)}");
        }
        else
        {
            session.Add("本 task 新增 — tok");
            session.Add("缓存命中 —");
        }

        var stack = new List<string> { "区栈" };
        try
        {
            var composition = _host.PreviewCompositionAsync("（下一轮）").GetAwaiter().GetResult();
            foreach (var layer in composition.Layers.OrderBy(static l => StackPanel.OrderOf(l.Region)))
            {
                var token = layer.Bytes / 4;
                _lastBytes.TryGetValue(layer.Region, out var previous);
                var delta = _hasBaseline ? layer.Bytes - previous : 0;
                _lastBytes[layer.Region] = layer.Bytes;
                stack.Add(delta == 0
                    ? $"{StackPanel.LabelOf(layer.Region)} {Number(token)}"
                    : $"{StackPanel.LabelOf(layer.Region)} {Number(token)} Δ{delta:+#;-#;0}");
            }

            _hasBaseline = true;
            var total = composition.Layers.Sum(static l => l.Bytes) / 4;
            stack.Add($"合计 {Number(total)} ≈token");
        }
        catch (Exception ex)
        {
            Utf8InputDriver.Trace("top-band preview: " + ex);
            stack.Add("—");
        }

        var tail = modules.OfType<CurrentTailModule>().FirstOrDefault()?.Current.Lines ?? Array.Empty<string>();
        var draft = modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.Lines ?? Array.Empty<string>();
        var tags = modules.OfType<FocusModule>().FirstOrDefault()?.CurrentFocus.Tags ?? Array.Empty<string>();

        var lines = new List<string>
        {
            "ⓘ AgentRuntime TUI",
            Line(cols, session),
            Line(cols, stack),
            KeepTail(cols, "白板", CountOf(tail), SummaryOf(tail)),
            KeepTail(cols, "草稿", CountOf(draft), SummaryOf(draft)),
            KeepTail(
                cols,
                "焦点",
                tags.Count == 0 ? "未报" : $"{tags.Count} 个标签",
                tags.Count == 0 ? "（模型本场未报 [FOCUS]）" : string.Join(' ', tags)),
        };

        return lines;
    }

    /// <summary>演示用顶部块（--input-test 没有 RuntimeHost 时仍能验「Tab 切换显示/隐藏」）。</summary>
    public static IReadOnlyList<string> Demo() =>
    [
        "ⓘ AgentRuntime TUI",
        $" 会话 | protocol v{ProtocolText.Version} | 6 模块 | 第1生命周期·00:42 | 本 task 新增 1,234 tok | 缓存命中 95%",
        " 区栈 | R1-P 3,372 | R1 24,751 | R2 8,065 | R4 112 | R5 95 | R3 13 | 合计 36,412 ≈token",
        " 白板 | 3 行 | solve: 方案 B · TUI2",
        " 草稿 | 2 行 | 顶部信息块按 Tab 切换显示",
        " 焦点 | 2 个标签 | E020 E022",
    ];

    private static string CountOf(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "空" : $"{lines.Count} 行";

    private static string SummaryOf(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "（空，零注入）" : lines[0];

    /// <summary>按 ` | ` 拼；放不下从尾部整段丢（宁可少说，不截字）。</summary>
    private static string Line(int cols, IReadOnlyList<string> segments)
    {
        var keep = segments.ToList();
        while (keep.Count > 1 && CellModel.Width(string.Join(" | ", keep)) > cols)
        {
            keep.RemoveAt(keep.Count - 1);
        }

        var text = string.Join(" | ", keep);
        return CellModel.Width(text) <= cols ? text : Truncate(text, cols);
    }

    /// <summary>标签 + 计数 + 摘要：第三段按剩余预算截断（把这一行用满，而不是整段丢掉）。</summary>
    private static string KeepTail(int cols, string label, string count, string summary)
    {
        var prefix = $"{label} | {count} | ";
        var budget = Math.Max(4, cols - CellModel.Width(prefix));
        return prefix + Truncate(summary, budget);
    }

    private static string Truncate(string text, int width)
    {
        if (CellModel.Width(text) <= width) return text;

        var sb = new StringBuilder();
        var used = 0;
        foreach (var element in CellModel.Elements(text))
        {
            var w = CellModel.ElementWidth(element);
            if (used + w > Math.Max(0, width - 1)) break;
            sb.Append(element);
            used += w;
        }

        return sb.Append('…').ToString();
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double rate) =>
        (rate * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

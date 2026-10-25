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
    private readonly SentContext _sent;
    private readonly Dictionary<StackRegion, int> _lastBytes = new();
    private bool _hasBaseline;
    private long _lastRefresh;
    private IReadOnlyList<string> _lines = Array.Empty<string>();

    public TopBandProvider(RuntimeHost host, IReadOnlyList<LifecycleTurnUsage> usages, TaskClock clock, SentContext sent)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _usages = usages ?? throw new ArgumentNullException(nameof(usages));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sent = sent ?? throw new ArgumentNullException(nameof(sent));
    }

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>刷新读数（默认 1 秒节流；按 Tab 的那一下可以 force）。</summary>
    public void RefreshIfDue(int cols, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastRefresh < 3000) return;   // 3s：预览要同步装配整份 prompt，别每秒都做
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
            // **写入** = 本 task 自己往流里追加的正文（继承来的前缀不算）。
            // 「累计消耗」不显示（主人 2026-10-25 10:4x：用户不关心这个数）—— 需要时看账本 / 收尾报告。
            session.Add($"本 task 累计写入 {Number(life.AppendedChars / 4)} tok");
            session.Add($"缓存命中 {Percent(usage.HitRate)}");
        }
        else
        {
            session.Add("本 task 累计写入 — tok");
            session.Add("缓存命中 —");
        }

        var stack = new List<string> { "区栈" };
        try
        {
            // **区栈 = 真正送出去的那一份**（主人 2026-10-24 16:2x 令）。
            // 有「上次送出」的组装结果就用它；还没发过（刚拉起 / 刚 reset）才退回预览，**且必须标「预估」** ——
            // 两个来源不能同名同脸，否则又变成「看着像事实的估计」。
            var sent = _sent.Value;
            var composition = sent ?? _host.PreviewCompositionAsync("（下一轮）").GetAwaiter().GetResult();
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
            // 合计取**请求体字节数**（`PromptComposition.Bytes`）：区栈各行是各段正文，合计是整份 body，
            // 两者同源但不等（框架/分隔），过去用「各段求和」冒充 body ⇒ 与实发对不上。
            var total = composition.Bytes / 4;
            // **两个数不是一回事**（2026-10-24 主人判「明显不对」后定稿）：
            //   · 本 task 累计写入 = 这个 task 自己往流里追加的正文 ÷4（**继承来的前缀不算**）
            //   · 当前上下文合计 = **上一次真送出去的那份 prompt**（不是下一次的预估）
            stack.Add(sent is null
                ? $"当前上下文合计 ~{Number(total)} tok（预估）"
                : $"当前上下文合计 {Number(total)} tok");
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
        $" 会话 | protocol v{ProtocolText.Version} | 6 模块 | T1 · 00:42 | 本 task 累计写入 1,234 tok | 缓存命中 95%",
        " 区栈 | R1-P 3,372 | R1 24,751 | R2 8,065 | R4 112 | R5 95 | R3 13 | 当前上下文合计 36,412 tok",
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

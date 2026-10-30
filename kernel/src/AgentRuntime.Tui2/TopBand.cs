using AgentRuntime.Presentation;
using System.Globalization;
using System.Text;
using AgentRuntime.Core;
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
            _lines = new[] { TuiStrings.T(TuiKey.Title), " " + TuiStrings.T(TuiKey.Session) + " | " + TuiStrings.T(TuiKey.Unavailable) };
        }
    }

    private IReadOnlyList<string> Build(int cols)
    {
        var modules = _host.Modules;
        // **模块数 = 拼进上下文的模块总数**（2026-10-29 主人令）：冻结区那几个也在上下文里，必须算进去。
        // 旧口径「Not ProtocolModule && Not IFrozenZoneModule」只数动态模块（实测 8），会让人以为冻结区没在拼装。
        var moduleCount = modules.Count;
        var stream = modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        var life = stream is null
            ? null
            : LifecycleAggregator.Aggregate(stream.Snapshot(), _usages).Current;

        var session = new List<string> { TuiStrings.T(TuiKey.Session), (cols < 92 ? "v" : "protocol v") + ProtocolText.Version };

        // **用的哪个模型 / 哪个思考档**（2026-10-29 主人令）：写在会话行开头附近，便于理解当前行为。
        var modelLabel = ModelLabel();
        if (modelLabel.Length > 0)
        {
            session.Add(modelLabel);
        }

        session.Add(TuiStrings.F(TuiKey.Modules, moduleCount));
        var taskLabel = _clock.Label(life?.Id);
        if (taskLabel.Length > 0) session.Add(taskLabel);

        // **会话行只放「会话 / 协议 / 模块数 / task / 本 task 写入」**（2026-10-29 主人令）：
        // 缓存命中率已搬到区栈行尾；模块数**不许被丢弃**（它回答「现在挂了多少模块」）。
        if (life is not null)
        {
            session.Add(TuiStrings.F(TuiKey.ThisTask, Number(life.AppendedChars / 4)));
        }
        else
        {
            session.Add(TuiStrings.T(TuiKey.ThisTaskNone));
        }

        // 窄屏只做「压缩写法」，不再整段丢弃（模块数必须留着）。
        if (TerminalText.WidthOf(string.Join(" | ", session)) > Math.Max(20, cols - 2))
        {
            // 窄屏压缩写法（语言包内已足够短，这里保留原样）
        }

        var sessionLine = string.Join(" | ", session);

        PromptComposition? composition = null;
        var stack = new List<string> { TuiStrings.T(TuiKey.Stack) };
        try
        {
            // **区栈 = 真正送出去的那一份**（主人 2026-10-24 16:2x 令）。
            // 有「上次送出」的组装结果就用它；还没发过（刚拉起 / 刚 reset）才退回预览，**且必须标「预估」** ——
            // 两个来源不能同名同脸，否则又变成「看着像事实的估计」。
            var sent = _sent.Value;
            composition = sent ?? _host.PreviewCompositionAsync(TuiStrings.T(TuiKey.NextTurn)).GetAwaiter().GetResult();
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
                ? TuiStrings.F(TuiKey.ContextTotalEst, Number(total))
                : TuiStrings.F(TuiKey.ContextTotal, Number(total)));

            // **整体缓存命中率搬到这里**（2026-10-29 主人令）：紧跟在「当前上下文合计」后面一块。
            stack.Add(life?.Usage is { } u ? TuiStrings.F(TuiKey.CacheHit, Percent(u.HitRate)) : TuiStrings.T(TuiKey.CacheHitNone));
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
            TuiStrings.T(TuiKey.Title),
            Line(cols, [sessionLine]),
        };

        // **点名**：真正拼进上下文的模块（含冻结区），一排放不下就折成 2~3 行（2026-10-29 主人令）。
        lines.AddRange(ContextModulesLines(cols, modules, composition));

        lines.AddRange(new[]
        {
            StackLine(cols, stack),
            KeepTail(cols, TuiStrings.T(TuiKey.Board), CountOf(tail), SummaryOf(tail)),
            KeepTail(cols, TuiStrings.T(TuiKey.Draft), CountOf(draft), SummaryOf(draft)),
            KeepTail(
                cols,
                TuiStrings.T(TuiKey.Focus),
                tags.Count == 0 ? TuiStrings.T(TuiKey.FocusNone) : TuiStrings.F(TuiKey.Tags, tags.Count),
                tags.Count == 0 ? TuiStrings.T(TuiKey.FocusNone) : string.Join(' ', tags)),
        });

        return lines;
    }

    /// <summary>演示用顶部块（--input-test 没有 RuntimeHost 时仍能验「Tab 切换显示/隐藏」）。</summary>
    public static IReadOnlyList<string> Demo() =>
    [
        "ⓘ AgentRuntime TUI2 - WhiteBox",
        " " + TuiStrings.T(TuiKey.Session) + $" | protocol v{ProtocolText.Version} | " + TuiStrings.F(TuiKey.Modules, 12) + " | T1 · 00:42 | " + TuiStrings.F(TuiKey.ThisTask, "1,234"),
        " " + TuiStrings.T(TuiKey.ContextModules) + " | 12 | protocol rules knowledge decision memory-index append-stream task-ledger tool note current-tail dynamic-draft focus",
        " " + TuiStrings.T(TuiKey.Stack) + " | R1-P 3,372 | R1 24,751 | R2 8,065 | R4 112 | R5 95 | R3 13 | " + TuiStrings.F(TuiKey.ContextTotal, "36,412") + " | " + TuiStrings.F(TuiKey.CacheHit, "95%"),
        " " + TuiStrings.T(TuiKey.Board) + " | " + TuiStrings.F(TuiKey.Lines, 3) + " | solve: 方案 B · TUI2",
        " " + TuiStrings.T(TuiKey.Draft) + " | " + TuiStrings.F(TuiKey.Lines, 2) + " | " + TuiStrings.T(TuiKey.TabHint),
        " " + TuiStrings.T(TuiKey.Focus) + " | " + TuiStrings.F(TuiKey.Tags, 2) + " | E020 E022",
    ];

    private static string CountOf(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? TuiStrings.T(TuiKey.FocusNone) : TuiStrings.F(TuiKey.Lines, lines.Count);

    private static string SummaryOf(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? TuiStrings.T(TuiKey.EmptyZero) : lines[0];

    /// <summary>按 ` | ` 拼；放不下从尾部整段丢（宁可少说，不截字）。</summary>
    /// <summary>
    /// **拼进上下文的模块**（2026-10-29 主人令）：列出**每一个**实际参与 prompt 拼装的模块名（按装配次序），
    /// 冻结区（protocol / rules / knowledge / decision）同样在列 —— 它们确实进了上下文。
    /// 放不下时按个数截断并明说「另 N 个」，不静默省略。
    /// </summary>
    private static List<string> ContextModulesLines(int cols, IReadOnlyList<IRuntimeModule> modules, PromptComposition? composition)
    {
        // **三档分类**（2026-10-29 主人令，别多想一层）：
        //   拼进上下文（本轮真加了字节）⇒ `(Cached)`（让人联想到 KV Cache）
        //   活版（按需从活版取，不进上下文）⇒ `(Active)`  —— 目前只有 memory-index
        //   其余挂载的运行时模块 ⇒ `(Runtime)`
        var bytes = composition?.ModuleBytes ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var marked = modules.Count(m => bytes.TryGetValue(m.Name, out var b) && b > 0);
        var names = modules
            .Select(m => m.Name + TuiStrings.T(Classify(m.Name, bytes)))
            .ToList();

        var head = TuiStrings.T(TuiKey.ContextModules) + " | " + marked + "/" + names.Count + " | ";
        var indent = new string(' ', CellModel.Width(head));
        var rows = new List<string> { head };
        var placed = 0;

        foreach (var name in names)
        {
            var row = rows[^1];
            var candidate = row.EndsWith(" | ", StringComparison.Ordinal) ? row + name : row + " " + name;
            if (CellModel.Width(candidate) <= cols)
            {
                rows[^1] = candidate;
                placed++;
                continue;
            }

            if (rows.Count >= MaxModuleRows)   // 三行封顶
            {
                break;
            }

            rows.Add(indent + name);
            placed++;
        }

        if (placed < names.Count)
        {
            var tail = TuiStrings.F(TuiKey.More, names.Count - placed);
            rows[^1] = rows[^1] + " " + tail;
        }

        return rows;
    }

    /// <summary>
    /// 模块三档：`Cached`（拼进上下文）/ `Active`（活版，按需取）/ `Runtime`（其余运行时模块）。
    /// <para>活版清单是**唯一声明处**：新增「按需从活版取」的模块时只改这里。</para>
    /// </summary>
    private static TuiKey Classify(string name, IReadOnlyDictionary<string, int> bytes)
    {
        if (bytes.TryGetValue(name, out var b) && b > 0)
        {
            return TuiKey.Cached;
        }

        return LiveModules.Contains(name) ? TuiKey.Active : TuiKey.RuntimeOnly;
    }

    /// <summary>「活版」模块（不进上下文，按需从活版文件取片）。</summary>
    private static readonly HashSet<string> LiveModules = new(StringComparer.Ordinal) { "memory-index" };

    /// <summary>模块行最多折几行（主人 2026-10-29：允许 2~3 行）。</summary>
    private const int MaxModuleRows = 3;

    /// <summary>会话行里的「模型 + 思考档」：`deepseek-v4-flash, think low`（拿不到就空串）。</summary>
    private string ModelLabel()
    {
        var config = _host.Config;
        var model = (config.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            return string.Empty;
        }

        var effort = (config.ReasoningEffort ?? string.Empty).Trim();
        return effort.Length == 0
            ? model
            : model + ", " + TuiStrings.F(TuiKey.Think, effort);
    }

    /// <summary>
    /// **区栈行**（2026-10-29）：段序 = 区栈 / 各区 / 合计 / 缓存命中。窄屏时**先丢各区明细**（从后往前），
    /// 保住「当前上下文合计」与「缓存命中」——它们是这一行里最该看见的两个数。
    /// </summary>
    private static string StackLine(int cols, List<string> segments)
    {
        var keep = segments.ToList();
        while (keep.Count > 3 && TerminalText.WidthOf(string.Join(" | ", keep)) > cols)
        {
            keep.RemoveAt(keep.Count - 3);   // 去掉最后一个"区"（合计与缓存命中在尾部，不动）
        }

        return Line(cols, keep);
    }

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

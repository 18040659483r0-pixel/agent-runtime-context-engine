using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tail;
using AgentRuntime.Modules;

namespace AgentRuntime.Hosting.Panels;

/// <summary>
/// **P3-a · 白板（R4）面板** —— 只读渲染 + 人工写入口，两者**同处一室**。
/// <para>
/// 为什么读写必须在一起：TUI 的 <c>/tail "…"</c> 与命令行的 <c>--tail "…"</c> **是同一个函数**
/// （T2：面板只能触发既有写入口，不得另写一份）—— 若各写各的，两条链路迟早分叉。
/// </para>
/// <para>存储文件是**唯一真相源**：坏文件在这里就抛错，不做「当没有」的降级。</para>
/// </summary>
public static class TailPanel
{
    /// <summary>
    /// 白板全文 + 来源 + 轮次 + 上限余量 + 存储路径（不调模型、不需密钥、不写盘）。
    /// </summary>
    public static IReadOnlyList<string> Render(RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var lines = new List<string>();
        var directory = config.CurrentTail.StorePath ?? string.Empty;
        var store = new CurrentTailStore(directory);
        var options = new CurrentTailOptions
        {
            StorePath = directory,
            ReportEnabled = config.CurrentTail.ReportEnabled,
        };

        var entry = store.TryLoad(null);
        var state = entry?.ToState() ?? CurrentTailState.Empty;
        var text = CurrentTailService.Render(state);

        lines.Add("[尾部] 当前尾部（R4）：工作台面的白板，排在全部稳定区与焦点之后。");
        lines.Add($"[尾部] 存储路径    : {store.PathFor(null)}（唯一真相源；坏文件报错，不降级）");
        lines.Add($"[尾部] 文件        : {(store.Exists(null) ? "存在" : "不存在（空默认 = 零注入）")}");
        lines.Add($"[尾部] 来源        : {state.Source}");
        lines.Add($"[尾部] 求解        : {SolveHeader.Parse(state.Lines).Describe()}（v9 口径：[TAIL] 首两行 solve / step）");
        lines.Add($"[尾部] 轮次        : {state.Turn}");
        lines.Add($"[尾部] 上限        : ≤{options.MaxLines} 行 / ≤{options.MaxChars} 字符（协议区声明，配置无权改）");
        lines.Add($"[尾部] 余量        : {options.MaxLines - state.LineCount} 行 / {options.MaxChars - state.CharCount} 字符");
        lines.Add($"[尾部] 标签账本    : {(state.Tags.Count == 0 ? "（无）" : string.Join(' ', state.Tags))}");
        lines.Add($"[尾部] 自报开关    : {(options.ReportEnabled ? "on" : "off")}（--tail-report；关自报 ≠ 改协议）");

        if (text.Length == 0)
        {
            lines.Add("[尾部] 白板        : （空，零注入）");
        }
        else
        {
            lines.Add("[尾部] 白板        :");
            foreach (var line in text.Split('\n'))
            {
                lines.Add($"  | {line}");
            }
        }

        return lines;
    }

    /// <summary>
    /// <c>--tail "文本"</c> / <c>--tail-clear</c>：**人工纠偏** —— 直接写唯一真相源（存储文件）。
    /// <para>擦白板**不是**删历史（R2 一个字节不动）；超限同样拒绝（人工也不越协议上限）。</para>
    /// <para><paramref name="live"/>：**长驻宿主**（TUI）传入引擎里那个活着的实例时，落盘与内存一次完成
    /// —— 否则会出现「屏上改了、下一轮 prompt 还是旧白板」这种静默分叉。</para>
    /// </summary>
    public static string Override(RuntimeConfiguration config, string? text, CurrentTailModule? live = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var directory = config.CurrentTail.StorePath ?? string.Empty;
        var store = new CurrentTailStore(directory);
        var options = new CurrentTailOptions
        {
            StorePath = directory,
            ReportEnabled = config.CurrentTail.ReportEnabled,
        };

        // 构造即照读存储区：坏文件在这里就会抛错（不带着「当没有」往下跑）。
        // 有活实例就用它 —— 同一函数、同一次写，屏面与 prompt 不可能分叉。
        var module = live ?? new CurrentTailModule(store, options);

        if (string.IsNullOrWhiteSpace(text))
        {
            var cleared = module.Clear();
            return $"已清空白板（来源 {cleared.Source}）→ {store.PathFor(module.SessionId)}（擦白板不是删历史）";
        }

        var lines = SplitLines(text);
        var state = module.Override(lines);
        return $"已人工覆盖白板：{state.LineCount} 行 / {state.CharCount} 字符（来源 {state.Source}）→ {store.PathFor(module.SessionId)}";
    }

    /// <summary>文本 → 行（CRLF 归一 + 按 LF 切；**与模块内部同一口径**）。</summary>
    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}

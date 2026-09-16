using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Hosting.Panels;

/// <summary>
/// **P4 · 焦点（R3）面板** —— 只读显示权重表 / 当前焦点 / band 文本 / 缓存是否命中。
/// <para>焦点是**流上的纯函数**（本面板不写盘、不改历史、不调模型）：因此「看一眼」不可能影响下一轮的字节（T1）。</para>
/// </summary>
public static class FocusPanel
{
    /// <summary>只读渲染（不调模型、不需密钥、不写盘）。</summary>
    public static IReadOnlyList<string> Render(RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var lines = new List<string>();
        var stream = ResumeSupport.LoadStream(config);
        var options = config.Focus.ToOptions();
        var state = FocusService.Snapshot(stream, options);
        var band = FocusService.Render(state);

        lines.Add("[焦点] 语义焦点（R3）：一行导航，不改历史、不删事件。");
        lines.Add($"[焦点] 策略        : {config.Focus.Policy}（topK={options.TopK} · minWeight={options.MinWeight} · 半衰期={options.HalfLifeTurns} turn · band 行数上限={FocusOptions.MaxBandLines}）");
        lines.Add(string.IsNullOrWhiteSpace(config.Stream.Path)
            ? "[焦点] 事件流      : （未配置 stream.path —— 只能靠 --focus 显式设定）"
            : $"[焦点] 事件流      : {config.Stream.Path}（{stream.Count} 条，游标 {stream.Cursor}）");

        lines.Add($"[焦点] 权重表      : {state.Weights.Count} 个标签");
        foreach (var entry in state.Weights.Top(10))
        {
            lines.Add($"           · {entry.Describe()}");
        }

        lines.Add(state.IsEmpty
            ? "[焦点] 当前焦点    : （空 —— 不产生任何消息）"
            : $"[焦点] 当前焦点    : {string.Join(' ', state.Tags)}（来源 {state.Source}）");
        lines.Add(band.Length == 0
            ? "[焦点] band        : （空，零注入）"
            : $"[焦点] band        : {band}");

        // P4 悬空标签告警：焦点引用了上下文中不存在的事件 id ⇒ 明确报警，不静默。
        foreach (var warning in FocusService.Verify(state.Tags, stream.Events))
        {
            lines.Add($"[焦点] ⚠️ {warning}");
        }

        // Q3 混合模式：流是真相源；缓存只能加速，不一致必须报（I16）。
        var cache = new FocusCache(config.Focus.Path ?? string.Empty);
        var cached = cache.Load();
        var reconciled = FocusService.Reconcile(cached?.Tags, state);

        if (cached is null)
        {
            lines.Add($"[焦点] 缓存        : 无（{config.Focus.Path}）—— 以流重算为准");
        }
        else if (reconciled.CacheUsed)
        {
            lines.Add($"[焦点] 缓存        : 命中（与流重算一致，游标 {cached.StreamCursor}）");
        }

        foreach (var warning in reconciled.Warnings)
        {
            lines.Add($"[焦点] ⚠️ {warning}");
        }

        lines.Add($"[焦点] 自报协议    : 由协议区声明（FrozenZone.Protocol · v{ProtocolText.Version}，代码常量；用户无权改）");
        lines.Add($"[焦点] 提示词      : {ProtocolText.Text.Split('\n').First(l => l.Contains($"{ProtocolText.FocusPrefix} E###", StringComparison.Ordinal))}");
        lines.Add($"[焦点] 缓存文件    : {config.Focus.Path}（写缓存失败不影响正确性）");
        return lines;
    }
}

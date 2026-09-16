using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Modules;

namespace AgentRuntime.Hosting.Panels;

/// <summary>
/// **P3-b · 草稿（R5）面板** —— 与 <see cref="TailPanel"/> 完全同构（只读渲染 + 人工写入口同处）。
/// <para>TUI 的 <c>/draft "…"</c> 与命令行的 <c>--draft "…"</c> 是同一个函数（T2）。</para>
/// <para>草稿是**唯一允许大删大改**的区（无局部编辑入口）：删 / 改 / 重排一律由整块覆盖表达。</para>
/// </summary>
public static class DraftPanel
{
    /// <summary>草稿全文 + 来源 + 轮次 + 上限余量 + 存储路径（不调模型、不需密钥、不写盘）。</summary>
    public static IReadOnlyList<string> Render(RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var lines = new List<string>();
        var directory = config.DynamicDraft.StorePath ?? string.Empty;
        var store = new DraftStore(directory);
        var options = new DraftOptions
        {
            StorePath = directory,
            ReportEnabled = config.DynamicDraft.ReportEnabled,
        };

        var entry = store.TryLoad(null);
        var state = entry?.ToState() ?? DraftState.Empty;
        var text = DraftService.Render(state);

        lines.Add("[草稿] 动态草稿区（R5）：唯一允许大删大改的区域（讨论中的构想 / 未定稿需求 / 未验证假设）。");
        lines.Add($"[草稿] 存储路径    : {store.PathFor(null)}（唯一真相源；坏文件报错，不降级）");
        lines.Add($"[草稿] 文件        : {(store.Exists(null) ? "存在" : "不存在（空默认 = 零注入）")}");
        lines.Add($"[草稿] 来源        : {state.Source}");
        lines.Add($"[草稿] 轮次        : {state.Turn}");
        lines.Add($"[草稿] 上限        : ≤{options.MaxLines} 行 / ≤{options.MaxChars} 字符（协议区声明，配置无权改）");
        lines.Add($"[草稿] 余量        : {options.MaxLines - state.LineCount} 行 / {options.MaxChars - state.CharCount} 字符");
        lines.Add($"[草稿] 标签账本    : {(state.Tags.Count == 0 ? "（无）" : string.Join(' ', state.Tags))}");
        lines.Add($"[草稿] 自报开关    : {(options.ReportEnabled ? "on" : "off")}（--draft-report；关自报 ≠ 改协议）");
        lines.Add("[草稿] 写入口      : **整块覆盖**（无 Remove/Move/Patch；删/改/重排都由整块新内容表达）");

        if (text.Length == 0)
        {
            lines.Add("[草稿] 草稿        : （空，零注入）");
        }
        else
        {
            lines.Add("[草稿] 草稿        :");
            foreach (var line in text.Split('\n'))
            {
                lines.Add($"  | {line}");
            }
        }

        return lines;
    }

    /// <summary>
    /// <c>--draft "文本"</c> / <c>--draft-clear</c>：**人工覆盖** —— 直接写唯一真相源（存储文件）。
    /// <para>清草稿**不是**删历史（R2 一个字节不动）；超限同样拒绝（人工也不越协议上限）。</para>
    /// <para><paramref name="live"/> 口径与 <see cref="TailPanel.Override"/> 完全一致。</para>
    /// </summary>
    public static string Override(RuntimeConfiguration config, string? text, DynamicDraftModule? live = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var directory = config.DynamicDraft.StorePath ?? string.Empty;
        var store = new DraftStore(directory);
        var options = new DraftOptions
        {
            StorePath = directory,
            ReportEnabled = config.DynamicDraft.ReportEnabled,
        };

        // 构造即照读存储区：坏文件在这里就会抛错（不带着「当没有」往下跑）。
        // 有活实例就用它 —— 同一函数、同一次写，屏面与 prompt 不可能分叉。
        var module = live ?? new DynamicDraftModule(store, options);

        if (string.IsNullOrWhiteSpace(text))
        {
            var cleared = module.Clear();
            return $"已清空草稿（来源 {cleared.Source}）→ {store.PathFor(module.SessionId)}（清草稿不是删历史）";
        }

        var lines = TailPanel.SplitLines(text);
        var state = module.Override(lines);
        return $"已人工覆盖草稿：{state.LineCount} 行 / {state.CharCount} 字符（来源 {state.Source}）→ {store.PathFor(module.SessionId)}";
    }
}

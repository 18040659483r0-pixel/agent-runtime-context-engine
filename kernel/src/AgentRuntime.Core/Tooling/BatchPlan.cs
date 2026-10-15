using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **一次回复内多调用的记账计划**（P1-2 §12.3 减法 2）—— 同批同形状的动作共用一条 `risk:` 自判。
/// <para>
/// 本类只回答两个问题：① 第 i 个调用属于哪个**形状**；② 它是不是该形状的**代表**、那一组共几项。
/// 判定与记账留在 <see cref="ToolRunner"/>（本类**不动语义、不写流**）。
/// </para>
/// <para>三条硬约束（规格 §12.4 的反面判据，由调用侧执行）：</para>
/// <list type="number">
/// <item>**形状不同 ⇒ 不继承**（各自记一条）；</item>
/// <item>**must-ask / 声明与事实不符 ⇒ 不许合并**（那是“撒谎”账，必须逐条可见）；</item>
/// <item>**单调用批 ⇒ 与旧行为逐字节相同**（组大小 ≤1 时本类什么都不改）。</item>
/// </list>
/// </summary>
public sealed class BatchPlan
{
    /// <summary>复合命令的切分点（与 <see cref="ExecCommandRisk"/> 同口径：只看形状，不解释语义）。</summary>
    private static readonly Regex SegmentSplit = new(@"&&|\|\||;|\n", RegexOptions.Compiled);

    private readonly Dictionary<int, int> _representative = [];   // 代表 index → 组大小
    private readonly Dictionary<int, int> _member = [];           // 任一成员 index → 组大小

    private BatchPlan(int count) => Count = count;

    /// <summary>本批调用数。</summary>
    public int Count { get; }

    /// <summary>第 i 个调用**是它那一组的代表**吗（是 ⇒ 给出组大小）。
    /// <para>**不在任何组里**（单调用 / 单形状）⇒ 返回 <c>true</c> 且组大小 1 —— 与旧行为逐字节相同：照记。</para></summary>
    public bool IsRepresentative(int index, out int groupSize)
    {
        if (_member.TryGetValue(index, out groupSize))
        {
            return _representative.ContainsKey(index);
        }

        groupSize = 1;
        return true;   // 组外 ⇒ 这一条就是它自己的代表（**不许静默丢审计**）
    }

    /// <summary>第 i 个调用**在某个组里**吗（是 ⇒ 给出组大小；单调用 / 组外 ⇒ false）。</summary>
    public bool InGroup(int index, out int groupSize) => _member.TryGetValue(index, out groupSize);

    /// <summary>
    /// **按形状分组**（纯解析，不执行、不写流）。
    /// <para>解析不了 / 不合法的调用**不参与分组** —— 它走拒绝路径，而拒绝**逐条记、不合并**。</para>
    /// </summary>
    public static BatchPlan Build(IReadOnlyList<ToolCall> calls, IReadOnlyDictionary<string, ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(tools);

        var plan = new BatchPlan(calls.Count);
        var representatives = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < calls.Count; i++)
        {
            var shape = ShapeOfCall(calls[i], tools);
            if (shape is null)
            {
                continue;
            }

            if (!members.TryGetValue(shape, out var list))
            {
                list = [];
                members[shape] = list;
                representatives[shape] = i;
            }

            list.Add(i);
        }

        foreach (var (shape, list) in members)
        {
            if (list.Count <= 1)
            {
                continue;   // 单条批：与旧行为逐字节相同（什么都不登记）
            }

            foreach (var index in list)
            {
                plan._member[index] = list.Count;
            }

            plan._representative[representatives[shape]] = list.Count;
        }

        return plan;
    }

    /// <summary>
    /// **动作形状**（唯一声明处）：同形状 = 可共用一条 `risk:` 自判。
    /// <para>规格 §12.3 的口径：**同程序 + 同子命令**（`exec`），或**同目录写**（`write` / `edit`）。</para>
    /// <para>为什么 `exec` 要**先分段再取子命令**：`cd repo &amp;&amp; svn commit` 与 `svn status` 不是同一个程序 ——
    /// 不分段就会把它们误判成同形状（那正是“不同形状却合并”的反例）。</para>
    /// </summary>
    public static string ShapeOf(string tool, ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (string.Equals(tool, ToolNames.Exec, StringComparison.OrdinalIgnoreCase))
        {
            return ExecShape(args.OptionalString("command"));
        }

        var path = args.OptionalString("path") ?? args.OptionalString("target");
        return string.IsNullOrWhiteSpace(path) ? tool : tool + ':' + DirectoryOf(path);
    }

    private static string? ShapeOfCall(ToolCall call, IReadOnlyDictionary<string, ITool> tools)
    {
        if (!tools.TryGetValue(call.Name?.Trim() ?? string.Empty, out var tool))
        {
            return null;
        }

        try
        {
            var args = ToolArgs.Parse(call.ArgumentsJson, call.Name);
            tool.Validate(args);
            return ShapeOf(tool.Name, args);
        }
        catch (ToolUsageException)
        {
            return null;
        }
    }

    /// <summary>`exec` 的形状：**最后一段**命令的「程序名 + 第一个非 flag 词」。</summary>
    private static string ExecShape(string? command)
    {
        var segments = SegmentSplit.Split(command ?? string.Empty);
        var last = string.Empty;
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                last = segment.Trim();
            }
        }

        var tokens = last.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return "exec:";
        }

        var sub = tokens.Skip(1).FirstOrDefault(static t => !t.StartsWith('-'));
        return string.IsNullOrEmpty(sub) ? "exec:" + tokens[0] : $"exec:{tokens[0]} {sub}";
    }

    /// <summary>路径所在的目录（形状只看目录：同目录的多次写算同形状）。</summary>
    private static string DirectoryOf(string path)
    {
        var p = path.Replace('\\', '/');
        var cut = p.LastIndexOf('/');
        return cut <= 0 ? p : p[..cut];
    }
}

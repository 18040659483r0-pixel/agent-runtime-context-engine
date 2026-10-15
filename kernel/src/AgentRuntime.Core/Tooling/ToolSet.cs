namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **本版本的工具集**：<c>read</c> / <c>list</c> / <c>write</c> / <c>edit</c> / <c>exec</c>。
/// <para>名单与 <see cref="ToolNames.All"/> 必须一致（有闸门测试钉住）—— 否则「协议说能点，运行时说没这工具」。</para>
/// <para>
/// <b><c>exec</c> 回来了</b>（主人 2026-09-16 12:58 定）：[WB] 要**自持**就得能自己跑 <c>svn</c> 与语料重建脚本。
/// 03:00 那条「论文里没有这一项」的判断**作废**；**安全框架后补**（白名单 / 沙箱 / 围栏都还没做，见 <see cref="ExecTool"/>）。
/// </para>
/// </summary>
public static class ToolSet
{
    /// <summary>造默认工具集。</summary>
    public static IReadOnlyList<ITool> Default() =>
    [
        new ReadTool(),
        new ListTool(),
        new WriteTool(),
        new EditTool(),
        new ExecTool(),
        new IndexTool(),
        new ScopeTool(),
    ];

    /// <summary>
    /// **按角色取面**（**唯一入口**）：面本身声明在 <see cref="ToolNames.MasterFace"/> /
    /// <see cref="ToolNames.WorkerFace"/>，本方法只把名字翻成工具实例 ——
    /// 「剥一把」= 在那份清单里删一个名字，装配 / 拒绝话术 / 协议正文一律自动跟随（不许两处各写一套）。
    /// </summary>
    public static IReadOnlyList<ITool> For(ToolNames.Role role) => Subset(ToolNames.FaceFor(role));

    /// <summary>
    /// 按名字取**子集** —— 受限工具面（最小 Worker 只拿它被允许的那几个）。
    /// <para>
    /// <b>不认识的名字 ⇒ 抛错</b>（fail-closed）：配置写错时“静默少给工具”的后果是
    /// **它干不了活却看起来在干**（最难查的一种）；报错的后果则是**人立刻知道**。
    /// 空名单 = 取默认全集（“不限制”用空表达，不用“偷偷给全部”表达）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ITool> Subset(IEnumerable<string> names)
    {
        var want = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToArray();

        var all = Default().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        if (want.Length == 0)
        {
            return all.Values.ToArray();
        }

        var unknown = want.Where(n => !all.ContainsKey(n)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"未知工具名：{string.Join("、", unknown)}（可用：{string.Join("、", all.Keys)}）");
        }

        return want.Select(n => all[n]).ToArray();
    }
}

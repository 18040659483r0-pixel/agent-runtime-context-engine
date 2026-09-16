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
    ];
}

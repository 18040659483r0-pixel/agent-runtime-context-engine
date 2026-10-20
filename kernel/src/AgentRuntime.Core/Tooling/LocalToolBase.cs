namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **本机工具**（<c>read</c> / <c>list</c> / <c>write</c> / <c>edit</c> / <c>exec</c>）的中间基类。
/// <para>
/// 它只做一件事：**把「本机能力」与「网络能力」分成两条派生线**（铁则 8：新增一类能力 = 新增一个类型），
/// 让网络工具不可能误用本机工具的路径/工作区语义，反之亦然。
/// </para>
/// <para>
/// ⚠️ 本批**不新增任何行为**：5 个既有工具的行为必须**逐字节不变**（由全量回归钉住）。
/// 将来若需要「本机路径围栏 / 工作区校验」，落点在这里 —— 不许在每个子类里各写一套。
/// </para>
/// </summary>
public abstract class LocalToolBase : ToolBase
{
}

namespace AgentRuntime.Core.Draft;

/// <summary>
/// 草稿区域（R5）的**标记接口** —— 动态区次序闸门（<c>EnsureDynamicRegionOrder</c>）据此识别「谁是 R5」。
/// <para>
/// 与 <c>ITailRegionModule</c>（R4）/ <c>IFocusRegionModule</c>（R3）是**互补**关系：
/// 冻结区标记 = 「我是稳定前缀的一部分」；本接口 = 「我是动态区里的 R5」。
/// </para>
/// <para>
/// 硬约束（有测试钉住）：<c>DynamicDraftModule</c>（Modules 层）**不得**实现
/// <c>IFrozenZoneModule</c> —— 草稿是「唯一允许大删大改」的区，它不是冻结区。
/// </para>
/// </summary>
public interface IDraftRegionModule : IRuntimeModule
{
}

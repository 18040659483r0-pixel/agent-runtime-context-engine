namespace AgentRuntime.Core.Focus;

/// <summary>
/// **焦点区域（R3）模块**的标记接口 —— 第三道闸门据此识别「谁是焦点」。
/// <para>
/// 与 <see cref="Frozen.IFrozenZoneModule"/> 是**互补**关系，不是同类：
/// 冻结区标记 = 「我是稳定前缀的一部分」；本接口 = 「我是**动态区里最靠后**的那一块」。
/// </para>
/// <para>
/// 硬约束（有测试钉住）：<c>FocusModule</c>（Modules 层）**不得**实现
/// <see cref="Frozen.IFrozenZoneModule"/> —— 焦点每轮都变，它不是冻结区。
/// </para>
/// </summary>
public interface IFocusRegionModule : IRuntimeModule
{
}

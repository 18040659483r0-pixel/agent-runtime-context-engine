namespace AgentRuntime.Core;

/// <summary>
/// Runtime 侧的模型调用参数（与厂商无关）。
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>模型 ID（原样透传给 Provider，Runtime 不做任何映射）。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>采样温度；null = 不发送该字段，用上游默认值。</summary>
    public double? Temperature { get; init; }

    /// <summary>输出上限；null = 不发送。</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// **推理档位**（<c>reasoning_effort</c>；null/空 = 不发该字段）。
    /// <para>⚠️ 2026-10-14：本字段过去**只由 Provider 在发送前就地注入** ⇒ 预览哈希算在注入**前**、
    /// 实发字节是注入**后** ⇒ **每轮都「T1 破线」**（真机落盘日志当场证实：消息数相同、字节不同）。
    /// 现在**组装器就是唯一来源** ⇒ Provider 那次注入变 no-op ⇒ 预览字节 == 实发字节。</para>
    /// </summary>
    public string? ReasoningEffort { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("RuntimeOptions.Model 不能为空（模型 ID 必填）。", nameof(Model));
        }
    }
}

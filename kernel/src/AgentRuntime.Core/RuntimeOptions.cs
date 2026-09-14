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

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("RuntimeOptions.Model 不能为空（模型 ID 必填）。", nameof(Model));
        }
    }
}

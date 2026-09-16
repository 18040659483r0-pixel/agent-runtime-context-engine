using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>判定端的一次请求（**一行 JSON**；没有多余语法，便于审计与调试）。</summary>
/// <param name="Op">操作：<c>list</c> / <c>issue</c> / <c>revoke</c> / <c>ping</c>。</param>
/// <param name="Capability">能力名（<c>issue</c> 用）。</param>
/// <param name="Target">确切目标（<c>issue</c> 用）。</param>
/// <param name="Minutes">有效期分钟（<c>issue</c> 用）。</param>
/// <param name="Note">理由（<c>issue</c> 用）。</param>
/// <param name="Id">编号（<c>revoke</c> 用）。</param>
public sealed record GateRequest(
    string Op,
    string? Capability = null,
    string? Target = null,
    int? Minutes = null,
    string? Note = null,
    string? Id = null);

/// <summary>判定端的回应（**一行 JSON**）。</summary>
/// <param name="Ok">成功与否。</param>
/// <param name="Error">失败原因（人话；<see cref="Ok"/> 为 true 时为空）。</param>
/// <param name="Lines">人类可读的结果行（list / issue / revoke 的输出）。</param>
/// <param name="Active">当前有效的预授权条数（runtime 侧读它做诊断）。</param>
public sealed record GateResponse(
    bool Ok,
    string? Error = null,
    IReadOnlyList<string>? Lines = null,
    int Active = 0,
    IReadOnlyList<GatePreAuth>? Entries = null)
{
    public static GateResponse Fail(string error) => new(false, error);

    public static GateResponse Succeed(IReadOnlyList<string> lines, int active = 0, IReadOnlyList<GatePreAuth>? entries = null) =>
        new(true, null, lines, active, entries);
}

/// <summary>一条预授权的**结构化**形式（runtime 侧据此装载 Grant；人话行只用于显示）。</summary>
public sealed record GatePreAuth(string Id, string Capability, string Target, string ExpiresAt);

/// <summary>判定端线协议的序列化口径（**唯一声明处**：两端共用，避免"各写一份 JSON 选项"）。</summary>
public static class GateProtocol
{
    /// <summary>单条消息的行上限（防"一行 JSON 撑爆内存"）。</summary>
    public const int MaxLineBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Decode<T>(string line) => JsonSerializer.Deserialize<T>(line, Options);
}

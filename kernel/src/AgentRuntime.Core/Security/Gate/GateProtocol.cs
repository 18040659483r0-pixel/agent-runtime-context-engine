using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>判定端的一次请求（**一行 XML 片段**；一行 = 一条，便于审计与调试）。</summary>
/// <param name="Op">操作：<c>list</c> / <c>issue</c> / <c>revoke</c> / <c>ping</c>。</param>
/// <param name="Capability">能力名（<c>issue</c> 用）。</param>
/// <param name="Target">确切目标（<c>issue</c> 用）。</param>
/// <param name="Minutes">有效期分钟（<c>issue</c> 用）。</param>
/// <param name="Note">理由（<c>issue</c> 用）。</param>
/// <param name="Id">编号（<c>revoke</c> 用）。</param>
[DataContract(Name = "GateRequest", Namespace = "")]
public sealed record GateRequest(
    [property: DataMember(Order = 0)] string Op,
    [property: DataMember(Order = 1, EmitDefaultValue = false)] string? Capability = null,
    [property: DataMember(Order = 2, EmitDefaultValue = false)] string? Target = null,
    [property: DataMember(Order = 3, EmitDefaultValue = false)] int? Minutes = null,
    [property: DataMember(Order = 4, EmitDefaultValue = false)] string? Note = null,
    [property: DataMember(Order = 5, EmitDefaultValue = false)] string? Id = null);

/// <summary>判定端的回应（**一行 XML 片段**）。</summary>
/// <param name="Ok">成功与否。</param>
/// <param name="Error">失败原因（人话；<see cref="Ok"/> 为 true 时为空）。</param>
/// <param name="Lines">人类可读的结果行（list / issue / revoke 的输出）。</param>
/// <param name="Active">当前有效的预授权条数（runtime 侧读它做诊断）。</param>
/// <param name="Entries">结构化条目（runtime 据此装载 Grant；人话行只用于显示）。</param>
[DataContract(Name = "GateResponse", Namespace = "")]
public sealed record GateResponse(
    [property: DataMember(Order = 0)] bool Ok,
    [property: DataMember(Order = 1, EmitDefaultValue = false)] string? Error = null,
    [property: DataMember(Order = 2, EmitDefaultValue = false)] string[]? Lines = null,
    [property: DataMember(Order = 3)] int Active = 0,
    [property: DataMember(Order = 4, EmitDefaultValue = false)] GatePreAuth[]? Entries = null)
{
    public static GateResponse Fail(string error) => new(false, error);

    public static GateResponse Succeed(string[] lines, int active = 0, GatePreAuth[]? entries = null) =>
        new(true, null, lines, active, entries);
}

/// <summary>一条预授权的**结构化**形式（runtime 侧据此装载 Grant；人话行只用于显示）。</summary>
[DataContract(Name = "GatePreAuth", Namespace = "")]
public sealed record GatePreAuth(
    [property: DataMember(Order = 0)] string Id,
    [property: DataMember(Order = 1)] string Capability,
    [property: DataMember(Order = 2)] string Target,
    [property: DataMember(Order = 3)] string ExpiresAt);

/// <summary>
/// 判定端线协议的序列化口径（**唯一声明处**：两端共用）。
/// <para>
/// 2026-10-14：**一行 JSON → 一行 XML 片段**（铁则 R1「自有格式一律强类型 XML」；用
/// <see cref="DataContractXml.ToLineFragment{T}"/> ⇒ 一份 = 一行，正文里的换行转字符引用）。
/// 两端（Agent 侧 <see cref="GateClient"/> ↔ 判定端 <see cref="GateServer"/>）**必须同版本**：
/// 判定端是独立 uid 的常驻服务（自包含发布），改协议要**两端同时换 + 重装**（<c>wb-cli install-t3</c>）。
/// </para>
/// </summary>
public static class GateProtocol
{
    /// <summary>单条消息的行上限（防"一行撑爆内存"）。</summary>
    public const int MaxLineBytes = 64 * 1024;

    public static string Encode<T>(T value) where T : class => DataContractXml.ToLineFragment(value);

    /// <summary>解一行；不是合法片段 ⇒ <c>null</c>（调用方按 fail-closed 处理）。</summary>
    public static T? Decode<T>(string line) where T : class
    {
        try
        {
            return DataContractXml.FromFragment<T>(line);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or SerializationException)
        {
            return null;
        }
    }
}

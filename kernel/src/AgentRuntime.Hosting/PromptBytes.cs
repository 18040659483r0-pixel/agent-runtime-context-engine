using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Models;
using AgentRuntime.Providers;

namespace AgentRuntime.Hosting;

/// <summary>
/// **请求字节的唯一口径** —— 「送进模型的 prompt 字节」在这里被序列化与哈希。
/// <para>
/// 序列化器直接取 Provider 的那一份（<see cref="OpenAICompatibleClient.RequestSerializerOptions"/>），
/// 不另配一套：否则「面板说不改字节」可能只是**两把尺子量出来的假相等**。
/// </para>
/// <para>用途：T1 不变量的取证（面板调用前后哈希相等）、T3 可复算记录、宿主面板的指纹列。</para>
/// </summary>
public static class PromptBytes
{
    /// <summary>真要发出去的请求体字节（UTF-8，与 Provider 逐字节同源）。</summary>
    public static byte[] Of(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, OpenAICompatibleClient.RequestSerializerOptions));
    }

    /// <summary>sha256 十六进制（全长 64 位小写）。</summary>
    public static string Sha256(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>请求体的 sha256（全长；面板里只显示前 12 位）。</summary>
    public static string Sha256Of(ChatRequest request) => Sha256(Of(request));

    /// <summary>字节数（= 真发出去的 body 长度）。</summary>
    public static int CountOf(ChatRequest request) => Of(request).Length;
}

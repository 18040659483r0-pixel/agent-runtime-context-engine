using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 冻结区**账本类**文件的统一 JSON 口径：缩进、中文不转义、枚举可读。
/// <para>集中一处，保证「同一快照 → 逐字节相同的账本」在多个类型间一致。</para>
/// </summary>
internal static class FrozenJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文原样（PITFALLS #2）
        Converters = { new JsonStringEnumConverter() },
    };
}

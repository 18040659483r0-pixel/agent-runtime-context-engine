using System.Text;

namespace AgentRuntime.Tests;

/// <summary>
/// 事件流落盘夹具：**手写 XML 片段**（把落盘格式本身钉进测试）。
/// <para>
/// 元素顺序 = <c>[DataMember(Order)]</c> 的顺序（<c>Seq → Tag → Kind → Source → Text</c>）——
/// <see cref="System.Runtime.Serialization.DataContractSerializer"/> 读时**按序匹配**，
/// 所以手写夹具顺带替我们把「顺序显式」这条纪律守住。
/// </para>
/// </summary>
internal static class StreamFixtures
{
    /// <summary>一条事件片段（无 XML 声明；正文里的换行原样保留，不转义）。</summary>
    public static string Fragment(long seq, string kind, string text, string? tag = null, string? source = null)
    {
        var sb = new StringBuilder();
        sb.Append("<Event>");
        sb.Append($"<Seq>{seq}</Seq>");
        if (tag is not null)
        {
            sb.Append($"<Tag>{tag}</Tag>");
        }

        sb.Append($"<Kind>{kind}</Kind>");
        if (source is not null)
        {
            sb.Append($"<Source>{source}</Source>");
        }

        sb.Append($"<Text>{text}</Text>");
        sb.Append("</Event>");
        return sb.ToString();
    }

    /// <summary>把若干片段写成一份流文件（UTF-8 无 BOM，片段之间一个 LF）。</summary>
    public static void Write(string path, params string[] fragments) =>
        File.WriteAllText(path, string.Join('\n', fragments) + "\n", new UTF8Encoding(false));
}

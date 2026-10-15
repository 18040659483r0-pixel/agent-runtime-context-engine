using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace AgentRuntime.Core.Persistence;

/// <summary>
/// **线上用的字符串清单**（主人 2026-10-07 令：解析走 XML 序列化）。
/// <para>为什么要一个专门的类型：<see cref="DataContractSerializer"/> 对裸 <c>string[]</c> 会写出
/// <c>&lt;d2p1:string&gt;</c> 这类**带命名空间的项名**（还带 <c>xmlns:d2p1</c> 声明）——
/// 那是机器好读、模型难写的形状。包一层 <see cref="CollectionDataContractAttribute"/>
/// 且 <c>ItemName = "item"</c>、<c>Namespace = ""</c> ⇒ 线上就是干净的 <c>&lt;evidence&gt;&lt;item&gt;…&lt;/item&gt;&lt;/evidence&gt;</c>。</para>
/// </summary>
[CollectionDataContract(Name = "list", Namespace = "", ItemName = "item")]
public sealed class XmlList : Collection<string>
{
    public XmlList()
    {
    }

    public XmlList(IEnumerable<string> items) : base([.. items])
    {
    }

    /// <summary>由普通数组造一个（写侧方便用）。</summary>
    public static XmlList Of(IEnumerable<string>? items) => items is null ? [] : new XmlList(items);
}

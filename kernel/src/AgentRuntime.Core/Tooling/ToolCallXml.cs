using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具调用 = 一个类型**（主人 2026-10-07 令：解析走 XML 序列化，不手写行扫描）。
///
/// <para><b>为什么换成它</b>：旧路是"行扫描 + 切段 + 拼 JSON 再解析" —— 段落**没有自定界**，
/// 于是抄来的上下文行会被一起切进去（真机：回声里的 <c>timeoutSeconds: 30</c> ⇒ 判"参数重复" ⇒ 整条命令被拒）。</para>
///
/// <para><b>线上形状</b>（块头 + **一个元素**；元素名 = <c>call</c>）：</para>
/// <code>
/// [TOOL] &lt;call&gt;&lt;name&gt;exec&lt;/name&gt;&lt;command&gt;wc -c /abs/f.txt&lt;/command&gt;&lt;timeoutSeconds&gt;30&lt;/timeoutSeconds&gt;&lt;/call&gt;
/// </code>
/// <para>成员**只作为元素**（不用属性：想改就改一处、字节可复现），顺序由 <c>Order</c> 显式钉住。</para>
/// </summary>
[DataContract(Name = "call", Namespace = "")]
public sealed class ToolCallXml
{
    [DataMember(Name = "name", EmitDefaultValue = false)] public string Name { get; init; } = string.Empty;
    [DataMember(Name = "path", EmitDefaultValue = false)] public string? Path { get; init; }
    [DataMember(Name = "symbol", EmitDefaultValue = false)] public string? Symbol { get; init; }
    [DataMember(Name = "content", EmitDefaultValue = false)] public string? Content { get; init; }
    [DataMember(Name = "command", EmitDefaultValue = false)] public string? Command { get; init; }
    [DataMember(Name = "timeoutSeconds", EmitDefaultValue = false)] public int TimeoutSeconds { get; init; }
    [DataMember(Name = "oldText", EmitDefaultValue = false)] public string? OldText { get; init; }
    [DataMember(Name = "newText", EmitDefaultValue = false)] public string? NewText { get; init; }
    [DataMember(Name = "maxLines", EmitDefaultValue = false)] public int MaxLines { get; init; }
    [DataMember(Name = "offset", EmitDefaultValue = false)] public int Offset { get; init; }
    [DataMember(Name = "limit", EmitDefaultValue = false)] public int Limit { get; init; }
    [DataMember(Name = "risk", EmitDefaultValue = false)] public string? Risk { get; init; }
    [DataMember(Name = "task", EmitDefaultValue = false)] public string? Task { get; init; }

    /// <summary>本类型的**块头**（唯一声明处；与协议区同一份）。</summary>
    public const string Marker = "[TOOL]";

    /// <summary>工具 XML 的**已知标签集**（根 + 成员）—— 归一取块时只转义**不在其中**的 `<`，并修**裸 `&`**。
    /// <para>与派发侧 <c>DispatchParser.IntentTags</c> / 回执侧 <c>ReceiptXml.ReceiptTags</c> 同一条纪律（§十·47）：</para></summary>
    public static readonly string[] Tags =
    [
        "call", "name", "path", "symbol", "content", "command", "timeoutSeconds",
        "oldText", "newText", "maxLines", "offset", "limit", "risk", "task",
    ];

    /// <summary>本族元素的**根标签**（`call` 或工具名本身）—— 判「漏写块头」用。</summary>
    public static readonly string[] RootTags = ["call", .. ToolNames.All];

    /// <summary>
    /// **漏写块头**的本族元素数（0 = 没有）。
    /// <para>块头是**唯一**认块信号。模型漏写时元素照样在文本里，"没有块 ⇒ 零事件" 的旧路会**静默丢**
    /// （2026-10-11 真机：四个 <c>&lt;call&gt;…&lt;/call&gt;</c> 全裸发 ⇒ 模型以为工具通道坏了）。
    /// 调用方据此**响亮拒**（与 E1「逐块归一 + 取不出整篇拒」同一条纪律：不许静默丢）。</para>
    /// </summary>
    public static int CountHeaderless(string? text)
    {
        var n = 0;
        foreach (var span in XmlBlock.PairedElements(text, RootTags))
        {
            try
            {
                var root = XmlBlock.Root(span);
                if (!string.IsNullOrWhiteSpace(XmlBlock.Member(root, "name")))
                {
                    n++;   // <call><name>read</name>…</call>
                    continue;
                }

                // 根标签就是工具名（<read>…</read>）且带了参数 ⇒ 也是一次**真调用**
                if (ToolNames.IsKnown(root.Name.LocalName)
                    && (XmlBlock.Text(root, "path") ?? XmlBlock.Text(root, "command") ?? XmlBlock.Text(root, "content")) is { Length: > 0 })
                {
                    n++;
                }

                // 其它（含正文里引述的 `<call>…</call>` 示例）：**不算** —— 判据要能区分「引述」与「真调用」
            }
            catch (System.Xml.XmlException)
            {
                // 取不出 ⇒ 不算（别把无关文本升级成拒）
            }
        }

        return n;
    }

    /// <summary>从一轮回复里取本类型；没有该块 ⇒ <c>null</c>；形状不符 ⇒ 抛（响亮）。</summary>
    public static ToolCallXml? Take(string? text) =>
        XmlBlock.Take(text, Marker) is { } element ? FromElement(element) : null;

    /// <summary>从一个**元素原文**读成本类型（成员按**名**取，顺序无关 —— 模型怎么写都行）。</summary>
    public static ToolCallXml FromElement(string element)
    {
        var r = XmlBlock.Root(element);
        return new ToolCallXml
        {
            Name = XmlBlock.Member(r, "name") ?? string.Empty,
            Path = XmlBlock.Text(r, "path"),
            Symbol = XmlBlock.Text(r, "symbol"),
            Content = XmlBlock.Text(r, "content"),
            Command = XmlBlock.Text(r, "command"),
            TimeoutSeconds = XmlBlock.Int(r, "timeoutSeconds"),
            OldText = XmlBlock.Text(r, "oldText"),
            NewText = XmlBlock.Text(r, "newText"),
            MaxLines = XmlBlock.Int(r, "maxLines"),
            Offset = XmlBlock.Int(r, "offset"),
            Limit = XmlBlock.Int(r, "limit"),
            Risk = XmlBlock.Text(r, "risk"),
            Task = XmlBlock.Text(r, "task"),
        };
    }

    /// <summary>线上形状（写回用；与 <see cref="Take"/> 成对）。</summary>
    public string ToXml() => XmlBlock.Wire(DataContractXml.ToFragment(this));
}

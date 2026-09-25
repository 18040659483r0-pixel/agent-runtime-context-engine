namespace AgentRuntime.Presentation;

/// <summary>
/// **呈现层的门面**（宿主只拿这一个东西）—— 把「源文本 → 富文本 → 落屏 / 纯文本」这条链收在一处。
/// <para>
/// 两个出口，**同一条投影**：
/// <see cref="Rich"/>（带角色，交落屏上色）与 <see cref="Plain"/>（<c>Rich(x).Text</c>，纯文本产物 / 非 TTY）。
/// </para>
/// </summary>
public sealed class Presenter
{
    public Presenter(StyleTable? style = null) => Style = style ?? StyleTable.Standard;

    /// <summary>当前配色（关掉即纯文本）。</summary>
    public StyleTable Style { get; }

    /// <summary>源文本 → 富文本（解析标记 + 保守标注）。</summary>
    public RichText Rich(string? text) => Annotator.Annotate(Markup.Parse(text));

    /// <summary>源文本 → 纯文本（标记被吃掉；**与屏上正文逐字节同**）。</summary>
    public string Plain(string? text) => Markup.Strip(text);

    /// <summary>富文本 → 一行可落屏字符串（上色时插零宽 SGR）。</summary>
    public string Render(RichText text) => Style.Render(text);

    /// <summary>不上色的门面（<c>--color never</c> / 非 TTY / <c>NO_COLOR</c>）。</summary>
    public static Presenter PlainOnly { get; } = new(StyleTable.Disabled);
}

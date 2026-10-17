using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **派发物的字节装配**（用户 2026-09-29 13:5x 令）：全程 <c>byte[]</c> —— 不拼字符串、不 <c>Trim</c>、
/// 不 <c>Split</c>、不转义，只在**最后一个边界解码一次**（<see cref="Decode"/>）。
///
/// <para><b>为什么</b>：派发物此前是「多段字符串拼出来」的 ⇒「中途有没有谁动过文本」**无法证伪**。
/// 而 <c>string.Trim()</c> 会吃掉全角空格（U+3000）与 NBSP、<c>Substring</c> 可能劈开代理对、
/// 平台还可能做 CRLF 转换 —— 这些都是**静默**改变送出去的内容。字节装配把这件事变成可复算：
/// **组装字节 = 真身份**（sha256）。</para>
///
/// <para><b>边界（远端能力，已核）</b>：远端 AI 走 HTTP + JSON（<c>ChatMessage.Content</c> 是 <c>string</c>，
/// 见 <c>Models/ChatModels.cs</c>；最终 <c>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(...))</c> 上线）
/// ⇒ **最后一跳必然是文本**，bytes 塞不进 JSON。所以本类把「解码」收成**唯一一处**，
/// 并给出 <see cref="RoundTrips"/>（解码再编码必须逐字节相等）当自证。</para>
///
/// <para><b>帧形状</b>（自描述：每段字节长度进帧头；段名与worker 协议区逐个对齐 —— <b>不另立段名</b>）：</para>
/// <code>
/// FRAME: 4821B sha256:3f9a2c81d4e7 | TASK=1732 KNOWLEDGE=0 base=58 WINDOW=61 CONTRACT=64
/// TASK: …任务原文，一个字节都不动…
/// KNOWLEDGE:
/// base: /abs/path
/// WINDOW:
///   /abs/one
/// CONTRACT: …
/// </code>
///
/// <para><b>纪律</b>：① 段与段之间只插一个 LF；② 自由文本**原样**进出，不改一个字节；
/// ③ 解码用**严格 UTF-8**（非法字节响亮抛错，绝不静默替成 U+FFFD）；④ 切片只在段边界，绝不劈多字节序列。</para>
/// </summary>
public static class DispatchFrame
{
    /// <summary>严格 UTF-8：无 BOM、非法字节抛错（不静默 U+FFFD）。</summary>
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>帧头名字（<c>FRAME: &lt;字节数&gt;B sha256:&lt;12 位&gt;</c>）——「整体送入」的身份证。</summary>
    public const string HeaderName = "FRAME";

    /// <summary>一段：名字 + **原始字节**（名字只用于人读 / 定位；定界靠标签里的字节长度）。</summary>
    public readonly record struct Section(string Name, ReadOnlyMemory<byte> Bytes);

    /// <summary>把字符串变成**字节** —— 进装配的唯一入口（此后不再碰 string）。</summary>
    public static ReadOnlyMemory<byte> Of(string? text) => Utf8.GetBytes(text ?? string.Empty);

    /// <summary>由字符串直接造一段。</summary>
    public static Section Text(string name, string? text) => new(name, Of(text));

    /// <summary>
    /// **装配**（帧头 + 各段）。全程字节操作：段名 → <c>:</c> → 空格 → 段体字节 → LF。**不做任何文本加工**。
    /// <para>段名保持worker 协议区声明的形状（<c>TASK:</c> / <c>KNOWLEDGE:</c> / <c>base:</c> / <c>WINDOW:</c> / <c>CONTRACT:</c>）——
    /// 自描述的字节长度放在**帧头**里，不污染段名（否则又是一次契约↔prompt 漂移）。</para>
    /// </summary>
    public static byte[] Compose(IReadOnlyList<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var body = new MemoryStream();
        var sizes = new List<string>(sections.Count);
        foreach (var s in sections)
        {
            var label = Utf8.GetBytes(s.Name + ": ");
            body.Write(label, 0, label.Length);
            if (s.Bytes.Length > 0)
            {
                body.Write(s.Bytes.Span);
            }

            body.WriteByte((byte)'\n');
            sizes.Add($"{s.Name}={s.Bytes.Length}");
        }

        var bodyBytes = body.ToArray();
        var header = Utf8.GetBytes(
            $"{HeaderName}: {bodyBytes.Length}B sha256:{Sha256Of(bodyBytes)[..12]} | {string.Join(" ", sizes)}\n");

        var frame = new byte[header.Length + bodyBytes.Length];
        header.CopyTo(frame, 0);
        bodyBytes.CopyTo(frame, header.Length);
        return frame;
    }

    /// <summary>组装字节的 sha256（小写十六进制，全长）—— 即派发物的**真身份**（进 <c>ContractSha</c>）。</summary>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>帧头声明的 sha（前 12 位）—— 只显示用，判等请用 <see cref="Sha256Of"/>。</summary>
    public static string HeaderShaOf(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var nl = Array.IndexOf(frame, (byte)'\n');
        var header = Utf8.GetString(frame, 0, nl < 0 ? frame.Length : nl);
        var i = header.IndexOf("sha256:", StringComparison.Ordinal);
        return i < 0 ? string.Empty : header[(i + 7)..].Trim();
    }

    /// <summary>**唯一解码点**（发送前最后一步）：字节 → 文本。</summary>
    public static string Decode(ReadOnlyMemory<byte> bytes) => Utf8.GetString(bytes.Span);

    /// <summary>
    /// **自证**：解码再编码必须**逐字节相等** —— 不相等 ⇒ 中途有谁动过文本（Trim / 重编码 / CRLF 转换）。
    /// 这条是「不再出现字符串拼装问题」的可复算判据。
    /// </summary>
    public static bool RoundTrips(ReadOnlyMemory<byte> bytes) =>
        Utf8.GetBytes(Decode(bytes)).AsSpan().SequenceEqual(bytes.Span);
}

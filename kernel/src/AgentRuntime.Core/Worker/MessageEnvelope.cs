using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **消息信封**（主人 2026-10-07 令）：master 与 worker 之间的每一次往返都带一封**可对号**的信封 ——
/// 谁发的 / 发给谁（**指纹**）、什么时候（**datetime**）、哪一条（**id** = 指纹+时刻的 hash）、
/// 以及**答的是哪一条**（<c>re=</c>）。收方不必靠上下文猜「这是哪一问的回话」。
///
/// <para><b>id 的算法（唯一处）</b>：<c>id = "d-" + SHA256(from|at) 前 12 位小写</c> ——
/// 与全仓指纹同一口径（<see cref="TaskDecisionReport"/> 那族的 <c>SHA256 → 前 N 位小写</c>）。</para>
/// </summary>
/// <param name="Mode">消息模式（封闭 5 类，见 <see cref="MessageMode"/>）。</param>
/// <param name="Id">本条的唯一 id（见类注的算法）。</param>
/// <param name="At">发出时刻（datetime；进信封，可读可复算）。</param>
/// <param name="From">发出方的**指纹**（master = 协议区指纹；worker = worker 协议区指纹 + 档）。</param>
/// <param name="To">收方的指纹。</param>
/// <param name="Re">**答的是哪一条**（被回复的那条的 <see cref="Id"/>；首条 = null）。</param>
public sealed record MessageEnvelope(
    MessageMode Mode,
    string Id,
    DateTimeOffset At,
    string From,
    string To,
    string? Re)
{
    /// <summary>信封行的块头（唯一声明处）。</summary>
    public const string Marker = "[ENVELOPE]";

    /// <summary>**id 的唯一算法**：<c>d-</c> + <c>SHA256(utf8(from|at:O))</c> 前 12 位小写。</summary>
    public static string IdOf(string from, DateTimeOffset at) =>
        "d-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{from}|{at:O}")))[..12].ToLowerInvariant();

    /// <summary>造一条（id 由 <see cref="IdOf"/> 算出 —— 不由调用方编）。</summary>
    public static MessageEnvelope Compose(
        MessageMode mode, string from, string to, DateTimeOffset at, string? re = null) =>
        new(mode, IdOf(from, at), at, from, to, string.IsNullOrWhiteSpace(re) ? null : re);

    /// <summary>机器读的那**一行**（进派发物 / 进回复头；与其余块同一条纪律：key=value）。</summary>
    public string Line =>
        $"{Marker} mode={MessageModes.Name(Mode)} id={Id} at={At:yyyy-MM-ddTHH:mm:sszzz} from={From} to={To}"
        + (Re is null ? string.Empty : $" re={Re}");

    /// <summary>一行痕。</summary>
    public string Describe() =>
        $"[信封] {MessageModes.Name(Mode)} · {Id} · {From} → {To}" + (Re is null ? string.Empty : $" · re={Re}");

    /// <summary>从一段文本里**取信封**（找不到 / 缺关键格 ⇒ null，由调用方按形状报事实）。</summary>
    public static MessageEnvelope? Parse(string? text)
    {
        if (text is null || !text.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var seg in l[Marker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = seg.IndexOf('=');
                if (eq > 0)
                {
                    fields[seg[..eq]] = seg[(eq + 1)..];
                }
            }
        }

        if (!fields.TryGetValue("mode", out var m) || MessageModes.Parse(m) is not { } mode)
        {
            return null;
        }

        if (!fields.TryGetValue("id", out var id) || !fields.TryGetValue("from", out var from)
            || !fields.TryGetValue("to", out var to))
        {
            return null;
        }

        var at = fields.TryGetValue("at", out var atRaw)
                 && DateTimeOffset.TryParse(atRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : default;

        return new MessageEnvelope(mode, id, at, from, to, fields.TryGetValue("re", out var re) ? re : null);
    }
}

/// <summary>
/// **一方身份**（主人 2026-10-07 令：启动 master / worker 时**一并给出两边的指纹**）——
/// 名字 + 指纹；进信封的 <c>from</c>/<c>to</c>，也让「谁跟谁在对话」在任何一条流里可查。
/// </summary>
/// <param name="Role">角色（<c>master</c> / <c>worker</c>）。</param>
/// <param name="Fingerprint">指纹（master = 协议区正文指纹；worker = worker 协议区指纹）—— 拉起那一刻定格。</param>
/// <param name="Detail">人读补充（worker 例：<c>provider/model</c>）。</param>
public sealed record PeerIdentity(string Role, string Fingerprint, string Detail = "")
{
    /// <summary>一行痕（启动时打出来）。</summary>
    public string Describe() =>
        $"{Role}@{Fingerprint}" + (Detail.Length == 0 ? string.Empty : $" ({Detail})");
}

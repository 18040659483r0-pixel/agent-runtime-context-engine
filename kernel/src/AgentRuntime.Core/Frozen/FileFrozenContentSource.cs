using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **磁盘冻结语料来源**：把「区 / 层 / 领域」映射成 <c>frozen/</c> 下的文件。
/// <para>目录布局（相对 <c>root</c>）：</para>
/// <code>
/// rules/global.md                rules/expert/&lt;domain&gt;.md     rules/project/&lt;project&gt;.md
/// knowledge/global.md            knowledge/expert/&lt;domain&gt;.md   knowledge/project/&lt;project&gt;.md
/// memory/index.md
/// </code>
/// <para>
/// **版本从文件头取**（不进 prompt）：<c>&lt;!-- frozen: version=8 --&gt;</c>；
/// 没有头则用正文哈希（前 8 位）兜底 —— 版本号永不为空，且内容变必变。
/// </para>
/// <para>文件不存在 = 该段不存在（返回 <c>null</c>，不贡献消息）——这是「切域省 token」的实现基础。</para>
/// </summary>
public sealed class FileFrozenContentSource : IFrozenContentSource
{
    private static readonly Regex VersionHeader = new(
        @"\A(?:\uFEFF)?\s*<!--\s*frozen:\s*version\s*=\s*(?<v>[^\s>]+)\s*-->\s*\r?\n?",
        RegexOptions.Compiled);

    private readonly string _root;

    public FileFrozenContentSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>语料根目录（绝对路径）。</summary>
    public string Root => _root;

    public FrozenContent? TryGet(FrozenSlot slot)
    {
        var relative = RelativePath(slot);
        if (relative is null)
        {
            return null;
        }

        var path = Path.Combine(_root, relative);
        if (!File.Exists(path))
        {
            return null;
        }

        var raw = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var match = VersionHeader.Match(raw);
        var version = match.Success ? match.Groups["v"].Value : ShortHash(raw);
        var text = match.Success ? raw[match.Length..] : raw;

        return new FrozenContent(version, text);
    }

    /// <summary>槽位 → 相对路径；未知组合或非法标识返回 null（防目录穿越）。</summary>
    private static string? RelativePath(FrozenSlot slot) => slot switch
    {
        { Zone: FrozenZone.Rules, Layer: FrozenLayer.Global } => "rules/global.md",
        { Zone: FrozenZone.Rules, Layer: FrozenLayer.Expert, DomainId: { } rd } when IsSafe(rd) => $"rules/expert/{rd}.md",
        { Zone: FrozenZone.Rules, Layer: FrozenLayer.Project, ProjectId: { } rp } when IsSafe(rp) => $"rules/project/{rp}.md",
        { Zone: FrozenZone.Knowledge, Layer: FrozenLayer.Global } => "knowledge/global.md",
        { Zone: FrozenZone.Knowledge, Layer: FrozenLayer.Expert, DomainId: { } kd } when IsSafe(kd) => $"knowledge/expert/{kd}.md",
        { Zone: FrozenZone.Knowledge, Layer: FrozenLayer.Project, ProjectId: { } kp } when IsSafe(kp) => $"knowledge/project/{kp}.md",
        { Zone: FrozenZone.MemoryIndex, Layer: FrozenLayer.Global } => "memory/index.md",
        _ => null,
    };

    /// <summary>标识只允许字母 / 数字 / 连字符 / 下划线（杜绝 <c>../</c> 等路径穿越）。</summary>
    private static bool IsSafe(string id) =>
        id.Length > 0 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
}

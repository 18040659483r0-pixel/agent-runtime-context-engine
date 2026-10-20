using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// <c>web_download</c> —— 受控下载到授权目录（P1）。
/// <para>
/// 先校验最终 URL（网关逐跳 SSRF）、Content-Type 白名单、大小上限，再落盘；
/// 路径逃逸一律拒绝；默认**不覆盖**（冲突三选一：fail / rename / overwrite）。
/// </para>
/// </summary>
public sealed class WebDownloadTool : NetworkToolBase
{
    /// <summary>默认下载上限（20 MB）。</summary>
    public const long DefaultMaxBytes = 20L * 1024 * 1024;

    /// <summary>硬上限（100 MB；模型写多大都不能越过）。</summary>
    public const long HardMaxBytes = 100L * 1024 * 1024;

    public WebDownloadTool(NetworkGateway? gateway = null) : base(gateway)
    {
    }

    public override string Name => ToolNames.WebDownload;

    public override NetworkOperation Operation => NetworkOperation.Write;

    /// <summary>覆盖已有文件 = 不可逆 ⇒ 升到 must-ask 的最高档；其余按对外动作。</summary>
    public override ToolRisk RiskFor(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            return NetworkToolArguments.ParseConflict(args.OptionalString("conflict")) == DownloadConflict.Overwrite
                ? ToolRisk.Critical
                : ToolRisk.Outbound;
        }
        catch (ToolUsageException)
        {
            return ToolRisk.Critical;   // 判不出 ⇒ 最高档（fail-closed）
        }
    }

    protected override string[] AllowedArguments => ["url", "path", "maxBytes", "conflict"];

    public override string Describe(ToolArgs args) =>
        $"web_download {args.OptionalString("url")} → {args.OptionalString("path") ?? "(自动命名)"}";

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("url");

        var limits = Gateway.DefaultLimits;
        if (limits.DownloadRoots.Count == 0)
        {
            throw new ToolUsageException("未配置下载目录：请在 config.xml 的 Network/DownloadRoots 里给出授权根目录。");
        }

        _ = NetworkToolArguments.ParseConflict(args.OptionalString("conflict"));

        if (args.OptionalInt("maxBytes") is { } max && (max <= 0 || max > HardMaxBytes))
        {
            throw new ToolUsageException($"maxBytes 必须在 1~{HardMaxBytes} 之间。");
        }

        // 路径当场判真身（不给审批一个「判不出」的目标）。
        _ = ResolveTarget(args, limits);
    }

    protected override NetworkRequest BuildRequest(ToolArgs args) => new()
    {
        Method = "GET",
        Url = args.RequireString("url").Trim(),
        Binary = true,
    };

    protected override NetworkLimits LimitsFor(ToolArgs args)
    {
        var max = args.OptionalInt("maxBytes") is { } requested
            ? Math.Clamp(requested, 1, HardMaxBytes)
            : DefaultMaxBytes;
        return Gateway.DefaultLimits.CapBytes(max);
    }

    protected override ToolOutcome Interpret(NetworkResponse response, ToolArgs args)
    {
        if (response.Error is not null)
        {
            return Failed(response);
        }

        if (response.Status is < 200 or >= 300)
        {
            return ToolOutcome.Failure($"下载失败：HTTP {response.Status}（{response.FinalUrl}）");
        }

        if (response.Truncated)
        {
            return ToolOutcome.Failure(
                $"下载超过大小上限（{Gateway.DefaultLimits.MaxResponseBytes} 字节），已中止，未落盘。");
        }

        var limits = Gateway.DefaultLimits;
        var contentType = (response.ContentType ?? string.Empty).Split(';')[0].Trim();
        if (!IsAllowedType(contentType, limits.AllowedDownloadTypes))
        {
            return ToolOutcome.Failure(
                $"Content-Type \"{contentType}\" 不在允许列表里（允许：{string.Join(", ", limits.AllowedDownloadTypes)}）。");
        }

        var content = response.Content ?? [];
        if (content.Length == 0)
        {
            return ToolOutcome.Failure("下载结果为空（0 字节），未落盘。");
        }

        var target = ResolveTarget(args, limits);
        var conflict = NetworkToolArguments.ParseConflict(args.OptionalString("conflict"));
        if (File.Exists(target))
        {
            if (conflict == DownloadConflict.Fail)
            {
                return ToolOutcome.Failure($"目标已存在，按 conflict=fail 不覆盖：{target}");
            }

            if (conflict == DownloadConflict.Rename)
            {
                target = NextFreeName(target);
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = target + ".part";
            File.WriteAllBytes(temporary, content);

            if (conflict == DownloadConflict.Overwrite)
            {
                File.Move(temporary, target, overwrite: true);
            }
            else
            {
                File.Move(temporary, target, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolOutcome.Failure($"写盘失败：{ex.Message}");
        }

        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var output = new StringBuilder();
        output.AppendLine(Trace(response));
        output.AppendLine($"path={target}");
        output.AppendLine($"bytes={content.Length}");
        output.AppendLine($"contentType={contentType}");
        output.AppendLine($"sha256={sha}");
        return ToolOutcome.Success(output.ToString());
    }

    private static bool IsAllowedType(string contentType, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(contentType) || allowed.Count == 0)
        {
            return false;
        }

        foreach (var item in allowed)
        {
            var candidate = item.Trim();
            if (candidate.EndsWith("/*", StringComparison.Ordinal))
            {
                if (contentType.StartsWith(candidate[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (contentType.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveTarget(ToolArgs args, NetworkLimits limits)
    {
        var requested = args.OptionalString("path");
        var roots = limits.DownloadRoots
            .Select(ExpandRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .ToArray();

        if (roots.Length == 0)
        {
            throw new ToolUsageException("未配置下载目录（Network/DownloadRoots 为空）。");
        }

        string full;
        if (string.IsNullOrWhiteSpace(requested))
        {
            var name = "download-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
            full = Path.GetFullPath(Path.Combine(roots[0], name));
        }
        else if (Path.IsPathRooted(requested))
        {
            full = Path.GetFullPath(requested);
        }
        else
        {
            full = Path.GetFullPath(Path.Combine(roots[0], requested));
        }

        var inside = roots.Any(root =>
            full.Equals(root, StringComparison.Ordinal)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (!inside)
        {
            throw new ToolUsageException(
                $"路径逃逸：{full} 不在授权下载目录内（{string.Join("、", roots)}）。");
        }

        return full;
    }

    private static string ExpandRoot(string root)
    {
        var value = root.Trim();
        if (value.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = Path.Combine(home, value.TrimStart('~', '/', '\\'));
        }

        return value;
    }

    private static string NextFreeName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }
}

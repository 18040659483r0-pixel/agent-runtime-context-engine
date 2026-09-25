using System.Security.Cryptography;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// 写入类工具的**落点与体量证据**（2026-09-23）。
/// <para>
/// 写成功的判据 = <b>绝对落点 + 字节数 + 前后 sha256</b>：光看「成功」两字分不出
/// 「写进了仓库」还是「写进了 /tmp」—— 2026-09-23 01:15 那次收尾就是这样丢了整整一层 L4
/// （正文全落在 <c>/tmp/{sec10,sec53,pf137,wl_add}.md</c>，仓库一处没进）。
/// </para>
/// <para>
/// 「共享临时区」只认 <c>/tmp</c> / <c>/var/tmp</c>（含 macOS 上 <c>/private</c> 真身）：
/// 那是**不属于任何人**的地方；每用户临时目录（<c>Path.GetTempPath()</c>）是运行时正常的中转区，不警告。
/// </para>
/// </summary>
internal static class FileEvidence
{
    /// <summary>共享临时区根（命中判定用；顺序无碍）。</summary>
    private static readonly string[] SharedTempRoots = ["/private/tmp", "/tmp", "/private/var/tmp", "/var/tmp"];

    /// <summary>文件 sha256（小写十六进制）；文件不存在 ⇒ <c>null</c>。</summary>
    public static string? Sha256(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>取前 12 位（够看「变没变」，又不把话术撑爆）；无 ⇒ <c>(无)</c>。</summary>
    public static string Short(string? hex) =>
        string.IsNullOrEmpty(hex) ? "(无)" : hex[..Math.Min(12, hex.Length)];

    /// <summary>是不是写进了**共享临时区**（写正文到这儿几乎一定是「先暂存、回头再搬」的错位）。</summary>
    public static bool IsTempArea(string path) => TempRootOf(path).Length > 0;

    /// <summary>命中的共享临时区根（没有 ⇒ 空串）。</summary>
    public static string TempRootOf(string path)
    {
        foreach (var root in SharedTempRoots)
        {
            if (path.StartsWith(root + "/", StringComparison.Ordinal)
                || string.Equals(path, root, StringComparison.Ordinal))
            {
                return root;
            }
        }

        return string.Empty;
    }
}

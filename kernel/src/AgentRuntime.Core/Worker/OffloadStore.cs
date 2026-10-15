using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **凝结逃生门**（第 1 条 · ㈡㈢㈣，2026-09-27）—— 被凝结（或凝结失败）的工具结果**原文落盘**，
/// 并给出**可兑现的指针**。
///
/// <para><b>为什么必须在闸门之外</b>：原来的话术写「原文在 worker 落点里可查」，而那条路
/// **本身也走 ToolResult** ⇒ 被同一道闸门吃掉 ⇒ 逐字正文永远读不回来（**假指针**）。
/// 判据只有一个：**顺着指针一步能不能拿到全文**。</para>
///
/// <para><b>落点</b>：<c>&lt;directory&gt;/&lt;seq&gt;.txt</c>（默认 <c>.stage1/offload</c>）。
/// 文件名用**序号**（不是哈希）—— 指针要能被人一眼读出来、<c>/raw 3</c> 能直接敲；
/// 哈希仍逐条记进指针，供**兑现校验**（读回来对不上 ⇒ 当场判失败，绝不静默给错内容）。</para>
///
/// <para><b>自己绝不抛</b>：逃生门不能变成第二个崩溃源 —— 落盘失败返回 <c>null</c>，
/// 由调用方据此走 **fail-open**（原样放行）。</para>
/// </summary>
public sealed class OffloadStore
{
    /// <summary>取回通道的 <c>source</c> 标记：按这个标记送回的读法**豁免凝结**（㈡）。</summary>
    public const string RawSource = "offload";

    private readonly string _directory;
    private int _seq;

    public OffloadStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(".stage1", "offload")
            : directory;
    }

    /// <summary>落点目录（诊断 / 测试用）。</summary>
    public string Directory => _directory;

    /// <summary>已落盘条数（诊断 / 测试用）。</summary>
    public int Count => _seq;

    /// <summary>正文 sha256（小写十六进制）。</summary>
    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// 把**完整正文**落盘并返回一条记录（序号 / 落点 / 字节 / 哈希）。
    /// 失败返回 <c>null</c>（**不抛**）⇒ 调用方走 fail-open。
    /// </summary>
    public OffloadRecord? TryLand(string text, string? source = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var seq = _seq + 1;
            var path = Path.Combine(_directory, seq.ToString(CultureInfo.InvariantCulture) + ".txt");
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _seq = seq;
            return new OffloadRecord(seq, path, Encoding.UTF8.GetByteCount(text), Sha256(text), source);
        }
        catch (Exception ex) when (IsLandingFailure(ex))
        {
            return null;
        }
    }

    /// <summary>按序号读回**逐字正文**；读不到返回 <c>null</c> 并给出原因（绝不回落到「猜一个」）。</summary>
    public string? TryRead(int seq, out string why)
    {
        var path = Path.Combine(_directory, seq.ToString(CultureInfo.InvariantCulture) + ".txt");
        try
        {
            if (!System.IO.File.Exists(path))
            {
                why = "落点不存在：" + path;
                return null;
            }

            var text = System.IO.File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            why = string.Empty;
            return text;
        }
        catch (Exception ex) when (IsLandingFailure(ex))
        {
            why = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>**当场证明指针能兑现**（㈢）：读回来 + 哈希对上，才算数。</summary>
    public bool CanHonor(OffloadRecord record)
    {
        var text = TryRead(record.Seq, out _);
        return text is not null && string.Equals(Sha256(text), record.Sha256, StringComparison.Ordinal);
    }

    /// <summary>这条路径是不是本店的落点（供「豁免凝结的读法」判定）。</summary>
    public bool IsInside(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.Ordinal);
        }
        catch (Exception ex) when (IsLandingFailure(ex))
        {
            return false;
        }
    }

    private static bool IsLandingFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException or PathTooLongException;
}

/// <summary>一条逃生门记录：**指针四要素**（序号 / 落点 / 字节 / 哈希）。</summary>
public sealed record OffloadRecord(int Seq, string Path, int Bytes, string Sha256, string? Source)
{
    /// <summary>可兑现的指针话术：一条能**一步走通**的取回路径 + 兑现校验基准。</summary>
    public string Pointer() =>
        $"原文落点 {Path}（{Bytes} 字节 · sha256 {Sha256}）";
}

using System;
using System.IO;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>master 侧回执复核结论（2026-09-27 用户令①）。</summary>
public sealed record EvidenceVerdict
{
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";
    public string Missing { get; init; } = "";
    public bool Retryable { get; init; }

    public static EvidenceVerdict Pass() => new() { Ok = true };

    public static EvidenceVerdict Fail(string reason, string missing, bool retryable = true)
        => new() { Ok = false, Reason = reason, Missing = missing, Retryable = retryable };
}

/// <summary>
/// 回执的**产物复核**：ok/evidence 必须由 master 侧真实产物复算得出。
/// worker 自称“已写入/成功”不算证据（本会话实测：9 次派发仅 1 次真落盘）。
/// 判据 = 路径存在 + 字节数 + 内容（逐字）；不符 ⇒ ok=false、Reason 给病因、Retryable=true（走带修正补发）。
/// </summary>
public static class EvidenceVerifier
{
    public const string ReasonUncorroborated = "EvidenceUncorroborated";
    public const string ReasonPathMissing = "PathNotReachable";

    public static EvidenceVerdict VerifyFile(string? path, string? expectedContent = null, long? expectedBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return EvidenceVerdict.Fail(ReasonPathMissing, "evidence 未给出路径");
        if (!File.Exists(path)) return EvidenceVerdict.Fail(ReasonPathMissing, "产物不存在: " + path);
        long len = new FileInfo(path).Length;
        if (expectedBytes is long eb && len != eb)
            return EvidenceVerdict.Fail(ReasonUncorroborated, "字节数不符: 期望 " + eb + " 实测 " + len);
        if (expectedContent is not null)
        {
            string actual = File.ReadAllText(path, Encoding.UTF8).Trim();
            string want = expectedContent.Trim();
            if (!string.Equals(actual, want, StringComparison.Ordinal))
                return EvidenceVerdict.Fail(ReasonUncorroborated, "内容不符: 期望 " + want + " 实测 " + actual);
        }
        return EvidenceVerdict.Pass();
    }

    /// <summary>从 evidence 文本里抠出 path= / content= / bytes= 三个键（空格/逗号分隔）。</summary>
    public static (string? Path, string? Content, long? Bytes) ParseEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, null);
        string? path = null, content = null; long? bytes = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int p = line.IndexOf("path=", StringComparison.OrdinalIgnoreCase);
            if (p >= 0) path = Take(line, p + 5);
            int c = line.IndexOf("content=", StringComparison.OrdinalIgnoreCase);
            if (c >= 0) content = Take(line, c + 8);
            int b = line.IndexOf("bytes=", StringComparison.OrdinalIgnoreCase);
            if (b >= 0 && long.TryParse(Take(line, b + 6), out long v)) bytes = v;
        }
        return (path, content, bytes);
    }

    /// <summary>
    /// 回执里声明的**三个可复算读数**：字节数 / 行数 / sha256（缺失 = null）。
    /// <para><b>2026-09-30（批 2/3）</b>：真机实测 —— 一份编造的回执（声称 2084 行 / 271226 字节）被放行，
    /// 因为旧闸门只验「路径存在」。读数必须能被**逐项复算**，所以先把它们变成结构化键。</para>
    /// </summary>
    public static (long? Bytes, long? Lines, string? Sha256) ParseReadings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, null);
        long? bytes = null, lines = null; string? sha = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (bytes is null && TryTakeLong(line, "bytes=", out long b)) bytes = b;
            if (lines is null && TryTakeLong(line, "lines=", out long l)) lines = l;
            if (sha is null)
            {
                int s = line.IndexOf("sha256=", StringComparison.OrdinalIgnoreCase);
                if (s >= 0)
                {
                    var v = Take(line, s + 7);
                    if (v.Length >= 16) sha = v;
                }
            }
        }

        return (bytes, lines, sha);
    }

    private static bool TryTakeLong(string line, string key, out long value)
    {
        value = 0;
        int i = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && long.TryParse(Take(line, i + key.Length), out value);
    }

    private static string Take(string line, int start)
    {
        int end = line.Length;
        foreach (char sep in new[] { ' ', '\t', ',', ';' })
        {
            int k = line.IndexOf(sep, start);
            if (k >= 0 && k < end) end = k;
        }

        // 归一：XML 时代的**渲染行**把值写成 `key="…"`（带引号）⇒ 剥掉，否则 TryParse/哈希比较恒不中
        // （2026-10-07 真机：回执声称 sha256=b37b40a3… 而盘上是 5ff9e9a9…，闸门却因解析不出而产生假绿）。
        return line.Substring(start, end - start).Trim().Trim('"');
    }
}

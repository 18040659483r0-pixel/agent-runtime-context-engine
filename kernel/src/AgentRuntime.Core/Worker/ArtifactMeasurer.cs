using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **读数的真值派生器（批 3/3，2026-09-30）** —— 把「产物读数」从**模型自述**改成**运行时实测**。
///
/// <para><b>为什么要有它</b>（真机 2026-09-30，同一份回执两次踩）：worker 模型会把读数**编出来**
/// —— 报告「214 字节 / shasum 3f2a1b9c…」而盘上是 20 字节；报告「2084 行 / 271226 字节 / 最大编号 262」
/// 而盘上是 2078 行 / 270974 字节。它甚至能给编造的数套一层命令外壳（<c>printf … rc=0</c> + 虚报 stdout）。
/// 只要读数**由模型写**，闸门（<see cref="ReceiptGate"/>）就只能在它自己给的字面里打转。</para>
///
/// <para><b>做法</b>：worker 的运行时**手上就有盘**。所以 ——</para>
/// <list type="number">
/// <item><see cref="MeasureLine"/>：对回执自称的候选里**第一个真实存在的文件**实测，生成一行
/// <c>measured: path=… bytes=… lines=… sha256=…</c> —— 这一行是**运行时写的**，模型编不了；
/// 它会随回执回到 master（master 侧闸门随后逐项复算，见 <c>ReceiptGate.Judge</c>）。</item>
/// <item><see cref="CrossCheck"/>：把**模型自述**的读数与这行真值对账 —— 冲突 ⇒ 判失败（不采信自述）。</item>
/// <item>另有一条**形状判据**：正文里出现 <c>64 位十六进制</c> 却没按 <c>sha256=</c> 键给 ⇒ 判失败
/// （那条 sha 不可复算 —— 真机那串 <c>3f2a1b9c…</c> 正是十六进制逐位递降的假串）。</item>
/// </list>
/// <para><b>边界（说清，不冒充实核）</b>：模型用**散文**写的数字（「2084 行」）不带键，本类**不**去猜它 ——
/// 那是契约层的事（教它用键给）。本类保证的是：**读数一旦可复算，就必须对得上**。</para>
/// </summary>
public static class ArtifactMeasurer
{
    /// <summary>真值行的键前缀（唯一声明处；闸门与测试都读它）。</summary>
    public const string MeasuredPrefix = "measured:";

    /// <summary>像 sha256 的裸串（正文里没按键给的那种）。
    /// <para><b>{64,} 而不是 {64}</b>：真机那串假摘要是 **65 位**十六进制（连长度都不对）——
    /// 只认恰好 64 位会把它放过去（夹具当场抓到）。</para></summary>
    private static readonly Regex BareSha = new(@"\b[0-9a-fA-F]{64,}\b", RegexOptions.Compiled);

    /// <summary>
    /// 对候选里**第一个真实存在的文件**生成本次实测行；一个都不存在 ⇒ null（不凭空造）。
    /// <para>候选来源：调用方给的额外候选（如派发 <c>window:</c> 里的文件）优先，其后是回执自称的路径
    /// （<see cref="ReceiptGate.ExtractClaimedPaths"/>）。</para>
    /// </summary>
    public static string? MeasureLine(string? modelText, IEnumerable<string>? extraCandidates = null)
    {
        var candidates = new List<string>();
        if (extraCandidates is not null)
        {
            candidates.AddRange(extraCandidates.Where(static p => !string.IsNullOrWhiteSpace(p)));
        }

        candidates.AddRange(ReceiptGate.ExtractClaimedPaths(modelText));

        var hit = candidates
            .Select(static p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(p => ReceiptGate.Resolve(p))
            .FirstOrDefault(File.Exists);
        return hit is null ? null : Line(hit);
    }

    /// <summary>对**指定文件**实测出真值行（存在性由调用方保证）。</summary>
    public static string Line(string path)
    {
        var fi = new FileInfo(path);
        return $"{MeasuredPrefix} path={path} bytes={fi.Length} lines={CountLines(path)} sha256={Sha256(path)}";
    }

    /// <summary>行数（不以换行收尾的末行也算一行 —— 与 <c>wc -l</c> 故意不同：后者会把「无换行末行」漏掉）。</summary>
    public static long CountLines(string path)
    {
        using var sr = new StreamReader(path);
        long n = 0;
        while (sr.ReadLine() is not null)
        {
            n++;
        }

        return n;
    }

    /// <summary>小写十六进制 sha256。</summary>
    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// **对账**：模型自述的读数 vs 运行时实测行。
    /// <para>判据三条：① 自述的 bytes/lines/sha256 与实测冲突 ⇒ 失败（附「声称 vs 实测」）；
    /// ② 正文里有裸 64 位十六进制却没用 <c>sha256=</c> 键 ⇒ 失败（那条摘要不可复算 —— 真机那串逐位递降的假 sha）；
    /// ③ 都没有 ⇒ 通过（「没声明读数」不是失败，但也不冒充「已核」）。</para>
    /// </summary>
    public static EvidenceVerdict CrossCheck(string? modelText, string? measuredLine)
    {
        // 实测行本身由运行时写 ⇒ 从对账文本里排除它，免得「自己跟自己比」。
        var text = StripMeasured(modelText);
        if (string.IsNullOrWhiteSpace(text)) return EvidenceVerdict.Pass();

        var (realBytes, realLines, realSha) = EvidenceVerifier.ParseReadings(measuredLine);
        if (realBytes is null)
        {
            return EvidenceVerdict.Pass();   // 没有实测行（无产物）⇒ 无可对账
        }

        var (claimBytes, claimLines, claimSha) = EvidenceVerifier.ParseReadings(text);

        if (claimBytes is long cb && cb != realBytes)
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"回执自述字节数 {cb} / 运行时实测 {realBytes}（读数必须由实测得出）");
        }

        if (claimLines is long cl && realLines is long rl && cl != rl)
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"回执自述行数 {cl} / 运行时实测 {rl}（读数必须由实测得出）");
        }

        if (!string.IsNullOrEmpty(claimSha) && realSha is not null
            && !string.Equals(claimSha, realSha, StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"回执自述 sha256 {Short(claimSha)} / 运行时实测 {Short(realSha)}（读数必须由实测得出）");
        }

        // 形状判据：裸 64 位十六进制（没按 sha256= 键给）⇒ 那条摘要不可复算。
        var bare = BareSha.Match(text);
        if (bare.Success && claimSha is null)
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"正文里出现 64 位十六进制 {Short(bare.Value)} 却没用 sha256= 键 ⇒ 该摘要不可复算（读数一律用键给：bytes=/lines=/sha256=）");
        }

        return EvidenceVerdict.Pass();
    }

    /// <summary>把运行时自己写的实测行从文本里剔除（对账时不自己跟自己比）。</summary>
    public static string StripMeasured(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return string.Join('\n', text.Split('\n').Where(static l => !l.TrimStart().StartsWith(MeasuredPrefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Short(string s) => s[..Math.Min(12, s.Length)] + "…";
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// 回执闸门（2026-09-27，主人 B 项）：worker 回执**必须**由 master 按盘复算后才算数。
/// 用法：拿到 worker 结果后调用 <see cref="Judge"/>，它按 `evidence.path` / `changes` 路径做存在性三验；
/// 不符即把该次结果改判为：ok=false · reason=EvidenceUncorroborated · retryable=true（④ 必带修正补发）。
/// 与 `EvidenceVerifier` 同一判据（盘上事实），不采信回执自述。
/// </summary>
public static class ReceiptGate
{
    /// <summary>
    /// 从回执文本里找第一个像「路径」的自称（<c>path=</c> / <c>changes=</c> / <c>created </c>，
    /// **也认 [WORKER] 块里的冒号形** <c>path:</c> / <c>changes:</c>）。
    /// <para><b>取到哪为止</b>：到本字段值的末尾（下一个 <c>|</c> / 换行）—— 不能直奔行尾，否则会把
    /// <c>| handoff:</c> 等**下一个字段**里的路径当成 changes 的产物。</para>
    /// <para><b>什么叫「自称」</b>（判据的唯一处）：字段值里第一个**像路径**的 token（含 <c>/</c> 或 <c>.</c>）且不是
    /// <c>none</c> / <c>无</c> 这类空值。为什么要扫整段值而不是头一个 token：真机 2026-09-29 13:3x 的回执是
    /// <c>changes: wrote 4 lines to /tmp/…</c>（头一个 token 是 “wrote”）—— <b>只看头一个 token 就会静默放过假 ✅</b>。</para>
    /// </summary>
    public static string? ExtractClaimedPath(string? receiptText)
    {
        var t = receiptText ?? string.Empty;
        if (t.Length == 0) return null;
        foreach (var key in new[] { "path=", "changes=", "created ", "addr=", "path:", "changes:", "created:", "addr:" })
        {
            int i = t.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int stop = i + key.Length;
            while (stop < t.Length && t[stop] is not ('\n' or '\r' or '|')) stop++;
            var rest = t[(i + key.Length)..stop];
            foreach (var token in rest.Split([' ', '\t', ',', ';', '"', '\'', '<', '>'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (LooksLikePath(token)) return token;
            }
        }
        return null;
    }

    /// <summary>
    /// **全部候选产物路径**（按出现次序、去重；每个键值只取第一个像路径的 token —— 与旧口径一致）。
    /// <para><b>2026-09-30 修（假红）</b>：旧实现只试第一个候选 —— 抽到 <c>file:line</c> 指针或带后缀的描述串时，
    /// 会把「<b>抽错</b>」误报成「<b>没产出</b>」（真机：产物 236 B 真在，闸门却报「产物不存在」，决策者据此误判「未落盘」）。</para>
    /// </summary>
    public static IReadOnlyList<string> ExtractClaimedPaths(string? receiptText)
    {
        var t = receiptText ?? string.Empty;
        if (t.Length == 0) return [];
        var seen = new List<string>();
        foreach (var key in new[] { "path=", "changes=", "created ", "addr=", "path:", "changes:", "created:", "addr:" })
        {
            int start = 0;
            while (true)
            {
                int i = t.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                start = i + key.Length;
                int stop = start;
                while (stop < t.Length && t[stop] is not ('\n' or '\r' or '|')) stop++;
                var rest = t[start..stop];
                foreach (var token in rest.Split([' ', '\t', ',', ';', '"', '\'', '<', '>'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!LooksLikePath(token)) continue;
                    if (!seen.Contains(token, StringComparer.Ordinal)) seen.Add(token);
                    break;
                }
            }
        }

        return seen;
    }

    /// <summary>【闸门判据·唯一处】值像不像一个**产物路径**（含分隔符或扩展名点；排除空值词）。</summary>
    private static bool LooksLikePath(string v)
    {
        if (v.Length == 0) return false;
        var low = v.ToLowerInvariant();
        if (low is "none" or "n/a" or "na" or "-" or "—" or "无" or "没有" or "未改" or "unchanged") return false;
        // 2026-10-07（真机）：**带省略号的显式截断值不是路径** —— 渲染行把长值缩成 `…/acc-…txt`，
        // 扫描把它当候选 ⇒ `File.Exists` 必假 ⇒ **假「路径不存在」补偿**（E009）。
        if (v.Contains('…') || v.Contains("...")) return false;
        return v.Contains('/') || v.Contains('\\') || v.Contains('.');
    }

    /// <summary>
    /// **回执的三段合成**（conclusion + evidence + <c>changes:</c>）—— 闸门只看前两段就会漏掉 <c>changes:</c> 里自称的产物。
    /// <para>真机 2026-09-29 13:2x 实测：worker 只读了 34/1881 行、**一个文件都没写**，却在 <c>changes:</c>
    /// 里报了产物路径 ⇒ 闸门没拦、渲染成 ✅（**假 ✅**）。本方法的唯一目的就是把它送进闸门。</para>
    /// <para>为什么补回字段名：闸门是**按键**找自称的（<c>changes:</c> / <c>path=</c>）—— 只拼裸值则它找不到键、
    /// 又退回 fail-open。evidence 保持裸值（它多数是 <c>文件:行</c> 的指针，带上键反会把「指针」当成「产物」去盘上找）。</para>
    /// </summary>
    public static string ReceiptText(WorkerResult r) => ReceiptText(r.Conclusion, r.Evidence, r.Changes);

    /// <summary>三段合成的**字符串重载**（运行时侧还没组装出 <see cref="WorkerResult"/> 时用它 ——
    /// <c>WorkerResult</c> 有 required 成员，为了拼文本去构造它是本末倒置）。</summary>
    public static string ReceiptText(string? conclusion, IReadOnlyList<string>? evidence, IReadOnlyList<string>? changes)
        => string.Join(" ", new[]
        {
            conclusion ?? string.Empty,
            string.Join(" ", evidence ?? Array.Empty<string>()),
            changes is { Count: > 0 } ? "changes: " + string.Join(" ", changes) : string.Empty,
        });

    /// <summary>
    /// 把候选解析成**绝对路径**：已绝对 ⇒ 原样；相对 ⇒ 按 <paramref name="basePath"/>（默认**仓根**）解析。
    /// <para><b>为什么不能按进程 cwd</b>（真机 2026-09-30）：worker 的回执里 `changes: created .stage1/acc/dir-<t>/verify.txt`
    /// 是**仓库相对路径**，而 TUI 的 cwd = `src/AgentRuntime.Tui` ⇒ `File.Exists` 落到错树 ⇒ 报「候选路径都不存在」
    /// ⇒ **假红 ⇒ 触发一次补发**（同族：坑 #219 / #184）。</para>
    /// </summary>
    public static string Resolve(string candidate, string? basePath = null)
    {
        if (Path.IsPathRooted(candidate)) return candidate;
        var b = string.IsNullOrWhiteSpace(basePath)
            ? (WindowResolver.FindRepoRoot() ?? Directory.GetCurrentDirectory())
            : basePath;
        return Path.GetFullPath(Path.Combine(b, candidate));
    }

    /// <summary>
    /// 按盘复算：给定回执文本，返回裁决（Ok=false 时带 reason/missing/retryable）。
    /// <para><b>三态（2026-09-30）</b>：不存在 / 存在但值与形状不符（**附声称 vs 实测**）/ 存在且相符 ——
    /// 「值与形状不符」不再被写成「不存在」（旧口径把两个病因混成一个，决策者据此把方向找错）。</para>
    /// <para><b>候选全试（2026-09-30）</b>：只要有一个候选真存在，就不是「没产出」。</para>
    /// <para><b>相对路径按 base 解析（2026-09-30）</b>：见 <see cref="Resolve"/>。</para>
    /// </summary>
    public static EvidenceVerdict Judge(string? receiptText, string? basePath = null)
    {
        var candidates = ExtractClaimedPaths(receiptText);
        if (candidates.Count == 0)
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated, "回执未给出可核的产物路径");

        var resolved = candidates.Select(c => Resolve(c, basePath)).ToList();
        var hit = resolved.FirstOrDefault(File.Exists);
        if (hit is null)
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonPathMissing,
                $"候选路径（{resolved.Count} 个）都不存在：" + string.Join(" | ", resolved));

        // 存在 ⇒ 逐项复算**回执自己声明的读数**（bytes= / lines= / sha256=）；
        // 声明了就必须对得上 —— 没声明就老老实实说「只核了存在性」，不冒充实核。
        var (declaredBytes, declaredLines, declaredSha) = EvidenceVerifier.ParseReadings(receiptText);
        long realBytes = new FileInfo(hit).Length;
        if (declaredBytes is long db && db != realBytes)
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"字节数不符（回执声称 {db} / 盘上实测 {realBytes}）：{hit}");

        if (declaredLines is long dl)
        {
            long realLines = File.ReadLines(hit).Count();
            if (dl != realLines)
                return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                    $"行数不符（回执声称 {dl} / 盘上实测 {realLines}）：{hit}");
        }

        if (!string.IsNullOrEmpty(declaredSha))
        {
            string realSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hit))).ToLowerInvariant();
            if (!string.Equals(declaredSha, realSha, StringComparison.OrdinalIgnoreCase))
                return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                    $"sha256 不符（回执声称 {Short(declaredSha)} / 盘上实测 {Short(realSha)}）：{hit}");
        }

        return EvidenceVerdict.Pass();
    }

    /// <summary>
    /// **按盘复算声明的读数**（读类回执也走这条路 · 2026-10-07）：地址 = <c>addr</c> / <c>path</c>（成员或渲染行），
    /// 声明的 <c>bytes</c>/<c>lines</c>/<c>sha256</c> 与盘上实测逐项对照；不符 ⇒ Fail（附**声称 vs 实测**）。
    /// <para>为什么需要它：旧闸门只在回执**自称产物**（<c>path=</c>/<c>changes=</c>/<c>created</c>）时才核 ⇒
    /// <b>读类回执的数值声明从来没被核过</b>。真机 2026-10-07：worker 读了新值却声明了一个陈旧的 sha
    /// （与盘上不符） ⇒ 形状层全绿、事实层假绿。
    /// <para>没给地址 / 没声明任何可复算读数 ⇒ <c>null</c>（fail-open，不翻 ok）。</para>
    /// </summary>
    public static EvidenceVerdict? JudgeDeclaredReadings(
        string? address, string? bytes, string? lines, string? sha256, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var hasBytes = long.TryParse(Clean(bytes), out var declaredBytes);
        var hasLines = long.TryParse(Clean(lines), out var declaredLines);
        var rawSha = Clean(sha256);
        // sha 判据（2026-10-07 再收紧）：
        //   ① 空 / `unknown` / `none` / `-` / `无` ⇒ **没声明**（诚实标记，不判红）
        //   ② 前导十六进制 ≥ 8 位（可带尾 `…`）= 声明了 ⇒ 按**前缀**核（容缩略、堵编造）
        //   ③ 其余（如 `5534879…` —— 真机：worker **照抄协议范例**的值）⇒ **形状缺陷**：不是可复算的读数。
        var shaHonest = rawSha.Length == 0
            || rawSha.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || rawSha is "none" or "-" or "无";
        var hexLen = 0;
        while (hexLen < rawSha.Length && Uri.IsHexDigit(rawSha[hexLen]))
        {
            hexLen++;
        }

        var declaresSha = hexLen >= 8;
        var shaGarbled = !shaHonest && !declaresSha;
        if (!hasBytes && !hasLines && !declaresSha && !shaGarbled)
        {
            return null;   // 没声明可复算的读数 ⇒ 这不是本闸门的活
        }

        var path = Resolve(Clean(address)!, basePath);
        if (!File.Exists(path))
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonPathMissing, $"回执声明的地址不存在：{path}");
        }

        long realBytes = new FileInfo(path).Length;
        if (hasBytes && declaredBytes != realBytes)
        {
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated, $"字节数不符（回执声称 {declaredBytes} / 盘上实测 {realBytes}）：{path}");
        }

        if (hasLines)
        {
            long realLines = File.ReadLines(path).Count();
            if (declaredLines != realLines)
            {
                return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated, $"行数不符（回执声称 {declaredLines} / 盘上实测 {realLines}）：{path}");
            }
        }

        if (shaGarbled)
        {
            // 真机 2026-10-07：worker 把**协议范例**的 `5534879…` 当读数交回；它既非真值也不可复算。
            return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated,
                $"sha256 不是可复算的值（回执声称 {rawSha} —— 疑似照抄示范/截断占位）：{path}");
        }

        if (declaresSha)
        {
            string realSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            // **前缀比对**：全值 / 缩略值都认，只要它是真 sha 的前缀（不符即不符）。
            if (!realSha.StartsWith(rawSha[..hexLen], StringComparison.OrdinalIgnoreCase))
            {
                return EvidenceVerdict.Fail(EvidenceVerifier.ReasonUncorroborated, $"sha256 不符（回执声称 {Short(rawSha)} / 盘上实测 {Short(realSha)}）：{path}");
            }
        }

        return EvidenceVerdict.Pass();
    }

    /// <summary>剥掉值周围的引号 / 空白 / 尾标点（渲染行 `key="值"` 与成员值都归一）。</summary>
    private static string Clean(string? s) => (s ?? string.Empty).Trim().Trim('"').Trim();

    private static string Short(string s) => s[..Math.Min(12, s.Length)] + "…";

    /// <summary>
    /// **只在回执自称产物时才核**（fail-open）：文本里没有 path= / changes= / created 声明 ⇒ 返回 null
    /// （语义 =「没有产物要核」），**不翻 ok**。调用点用它，避免把一份本就没有产物的合法回答误判为失败。
    /// </summary>
    public static EvidenceVerdict? JudgeIfClaimed(string? receiptText)
        => ExtractClaimedPath(receiptText) is null ? null : Judge(receiptText);

    /// <summary>把裁决拼成回执行（0 缓存代价；与 2 项的三字段口径一致）。</summary>
    public static string Describe(EvidenceVerdict v) => v.Ok
        ? " receipt=corroborated"
        : $" receipt=ok=false reason={v.Reason} missing={v.Missing} retryable={v.Retryable.ToString().ToLowerInvariant()}";
}

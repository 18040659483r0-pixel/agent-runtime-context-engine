using System.Text;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **语料写入口（运行时自带）** —— 收尾要**覆盖那些 MD 文件本身**，这是**文件 IO，不是起进程**。
///
/// <para><b>为什么要有它（2026-10-11 用户定）</b>：现行口径是「内核**只记账 + 推进水位，不改写语料**；
/// 抽象由模型按协议第 7 条跑 CLI 完成」⇒ 收尾被推给远端 AI **一轮一轮点工具**（M10：25 轮 / 47 工具）。
/// 但**处理内容 ≠ 工具面的事**：铁则 R2 管的是「起进程」（<c>Process.Start</c>），**不管写文件** ——
/// 收尾写语料运行时本来就该自己做。</para>
///
/// <para><b>三条硬性质</b>：</para>
/// <list type="number">
/// <item><b>原子</b>：临时文件 + 改名 —— 半篇写一半不算收尾（与 <see cref="HandoverSnapshot"/> 同纪律）。</item>
/// <item><b>字节口径</b>：UTF-8 **无 BOM**、**LF 结尾**（与全仓一致；否则每次收尾都产生假 diff）。</item>
/// <item><b>白名单**必须显式给**</b>：只允许写传进来的那几个落点（收尾的工作区 / 坑集）——
/// 协议区是**代码常量**、没有文件，任何「像协议」的落点一律**拒**（<see cref="CloseoutService.EnsurePromotionTarget"/> 同纪律）。</item>
/// </list>
/// </summary>
public static class CorpusWriter
{
    /// <summary>在 <paramref name="allowedRoots"/> 之外 / 空写路径 ⇒ 拒（fail-closed，不猜）。</summary>
    public static void EnsureWritable(string path, IEnumerable<string> allowedRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(allowedRoots);

        var full = Path.GetFullPath(path);
        var roots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();

        if (roots.Length == 0)
        {
            throw new InvalidDataException("语料写入被拒：没有给白名单落点（收尾只写它被授权的那几处）。");
        }

        if (!roots.Any(r => full.StartsWith(r, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"语料写入被拒：{full} 不在白名单落点内（{string.Join("、", roots)}）。");
        }
    }

    /// <summary>整篇写入（原子 + 无 BOM + LF）。</summary>
    public static void Write(string path, string content, IEnumerable<string> allowedRoots)
    {
        EnsureWritable(path, allowedRoots);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// 把 <paramref name="anchor"/> 行**之后的那一段**（到 <paramref name="anchor"/>+<paramref name="spanLines"/> 行）换成 <paramref name="newText"/>。
    /// <para>用途：水位 MD 里「每次收尾后更新」那一段由运行时**整段覆盖**（旧段落归档不动）。**锚找不到 ⇒ 抛**（不猜位置、不追加到结尾）。</para>
    /// </summary>
    public static void ReplaceAfterAnchor(string path, string anchor, int spanLines, string newText, IEnumerable<string> allowedRoots)
    {
        EnsureWritable(path, allowedRoots);

        var full = Path.GetFullPath(path);
        var lines = File.ReadAllText(full).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        var at = lines.FindIndex(l => l.Trim().StartsWith(anchor.Trim(), StringComparison.Ordinal));
        if (at < 0)
        {
            throw new InvalidDataException($"语料写入被拒：锚「{anchor}」在 {full} 里找不到 —— 不猜位置（先修锚，别追加到结尾）。");
        }

        var remove = Math.Clamp(spanLines, 0, lines.Count - at - 1);
        lines.RemoveRange(at + 1, remove);
        lines.Insert(at + 1, newText.Replace("\r\n", "\n").Replace('\r', '\n'));

        Write(full, string.Join('\n', lines), allowedRoots);
    }

    /// <summary>
    /// 把 <paramref name="anchor"/> 之后**第一条非空行**换成 <paramref name="newLine"/>。
    /// <para>用途：收尾水位线**由运行时提高到最新位置** —— 锚之后那一条就是「收敛点」，不靠固定跨度（粗心会吃掉邻行）。</para>
    /// </summary>
    public static void ReplaceFirstNonEmptyAfter(string path, string anchor, string newLine, IEnumerable<string> allowedRoots)
    {
        EnsureWritable(path, allowedRoots);

        var full = Path.GetFullPath(path);
        var lines = File.ReadAllText(full).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        var at = lines.FindIndex(l => l.Trim().StartsWith(anchor.Trim(), StringComparison.Ordinal));
        if (at < 0)
        {
            throw new InvalidDataException($"语料写入被拒：锚「{anchor}」在 {full} 里找不到 —— 不猜位置。");
        }

        var target = -1;
        for (var i = at + 1; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            throw new InvalidDataException($"语料写入被拒：锚「{anchor}」之后没有非空行可替换（结构变了 ⇒ 先修锚，别追加）。");
        }

        lines[target] = newLine.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        Write(full, string.Join('\n', lines), allowedRoots);
    }

    /// <summary>**带标签的机读行**（唯一来源）：已有该标签行 ⇒ 就地替换（幂等）；否则**插到锚行之后**（不覆盖锚后已有的行 —— 别踩别人的机读行）。</summary>
    public static void ReplaceLabeledOrInsertAfter(string path, string label, string anchor, string newLine, IEnumerable<string> allowedRoots)
    {
        EnsureWritable(path, allowedRoots);

        var full = Path.GetFullPath(path);
        var lines = File.ReadAllText(full).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var clean = newLine.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

        var at = lines.FindIndex(l => l.Contains(label, StringComparison.Ordinal));
        if (at >= 0)
        {
            lines[at] = clean;
        }
        else
        {
            var anchorAt = lines.FindIndex(l => l.Trim().StartsWith(anchor.Trim(), StringComparison.Ordinal));
            if (anchorAt < 0)
            {
                throw new InvalidDataException($"语料写入被拒：锚「{anchor}」在 {full} 里找不到 —— 不猜位置。");
            }

            lines.Insert(anchorAt + 1, clean);
        }

        Write(full, string.Join('\n', lines), allowedRoots);
    }
}

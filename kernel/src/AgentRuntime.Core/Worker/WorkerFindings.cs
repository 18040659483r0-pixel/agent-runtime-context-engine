using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **worker 产出的收尾汇流**（用户 2026-10-06 令）：收尾时 master 读 worker 的**踩坑集**
/// （<c>worker.pitfallsInbox</c>）+ **memory**，把 worker 遇到的新问题**收敛进公共 L1/L2 与铁则区**。
///
/// <para><b>为什么要机械读</b>：worker 不自收尾（契约第 8 条），它报的坑只进回执就**等于没记**；
/// 而"收尾时读它"此前**只写在注释里** —— 没有任何一处真的去读（2026-10-06 按盘核实：
/// <c>CloseoutChecklist</c> 里对 worker 的引用 = 0）。</para>
///
/// <para><b>吸收账本与正文同在一份文件</b>（不引入第二种存储）：收敛完成 ⇒ 追加一行
/// <c>[absorbed] yyyy-MM-dd HH:mm &lt;条数&gt;</c>；未吸收 = 总条数 − 各 absorbed 行计数之和。
/// 判据可复算、可 diff，**不猜**（也不改闸门来过关）。</para>
/// </summary>
public static class WorkerFindings
{
    /// <summary>吸收账本行的前缀（唯一声明处）。</summary>
    public const string AbsorbedPrefix = "[absorbed]";

    /// <summary>读一次（**只读**）。</summary>
    public sealed record Digest(
        int Total,
        int Absorbed,
        IReadOnlyList<string> Pending,
        string? MemoryRoot,
        int MemoryFiles)
    {
        /// <summary>还差几条没收敛（= 收尾件该红几条）。</summary>
        public int PendingCount => Math.Max(0, Total - Absorbed);
    }

    /// <summary>
    /// 读 worker 的坑集收件箱与 memory。
    /// <para><paramref name="inboxPath"/> = <c>worker.pitfallsInbox</c>；<paramref name="workerRoot"/> = worker 落点根
    /// （memory 按 provider 分家：<c>&lt;root&gt;/&lt;provider&gt;/memory</c>，与 <see cref="WorkerWatermark.MemoryFor"/> 同一条纪律）。</para>
    /// </summary>
    public static Digest Read(string? inboxPath, string? workerRoot)
    {
        var total = 0;
        var absorbed = 0;
        var content = new List<string>();

        if (!string.IsNullOrWhiteSpace(inboxPath) && File.Exists(inboxPath))
        {
            foreach (var raw in File.ReadLines(inboxPath))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(AbsorbedPrefix, StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[^1], out var n))
                    {
                        absorbed += n;
                    }

                    continue;
                }

                total++;
                content.Add(line);
            }
        }

        // 账本按**条数**记账 ⇒ 顺序即身份：前 Absorbed 条已收敛，其余的才是待收敛。
        var pending = content.Skip(absorbed).ToList();

        string? memoryRoot = null;
        var files = 0;
        if (!string.IsNullOrWhiteSpace(workerRoot) && Directory.Exists(workerRoot))
        {
            foreach (var providerDir in Directory.EnumerateDirectories(workerRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var memory = Path.Combine(providerDir, "memory");
                if (!Directory.Exists(memory))
                {
                    continue;
                }

                memoryRoot ??= memory;
                files += Directory.EnumerateFiles(memory, "*", SearchOption.AllDirectories).Count();
            }
        }

        return new Digest(total, absorbed, pending, memoryRoot, files);    }

    /// <summary>
    /// 记一笔「已收敛」—— 收敛动作本身由 master 做（把新问题抽象进公共 L1/L2 与铁则区），这里**只记账**。
    /// <para>写不进去 ⇒ 抛错（不静默）：账本没写 = 下一轮收尾还会报同样的红。</para>
    /// </summary>
    public static void MarkAbsorbed(string? inboxPath, int count, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(inboxPath) || count <= 0)
        {
            return;
        }

        var dir = Path.GetDirectoryName(inboxPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.AppendAllText(
            inboxPath,
            $"{AbsorbedPrefix} {now:yyyy-MM-dd HH:mm} {count}{Environment.NewLine}",
            System.Text.Encoding.UTF8);
    }
}

using System.Security.Cryptography;
using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;

namespace AgentRuntime.Hosting.Panels;

/// <summary>prompt 纵向栈里的六个区（问法：**这段字节属于哪一层**）。</summary>
public enum StackRegion
{
    /// <summary>R1-P：协议区（内核契约，Rank 0，用户不可摘）。</summary>
    R1P,

    /// <summary>R1：冷冻稳定前缀（rules / knowledge / memory-index）。</summary>
    R1,

    /// <summary>R2：事件流 / 会话历史（只追加）。</summary>
    R2,

    /// <summary>R4：当前尾部（白板）。</summary>
    R4,

    /// <summary>R5：动态草稿（唯一允许大删大改）。</summary>
    R5,

    /// <summary>R3：语义焦点（每轮必变 ⇒ 居末）。</summary>
    R3,
}

/// <summary>区里的一个最小单元（冻结区 = 一个段，有账本版本；动态区 = 一条贡献消息，无版本）。</summary>
public sealed record StackSegment(string Id, string Version, int Bytes, string Fingerprint);

/// <summary>一个区的逐字节账（字节数 / 指纹 / 版本 / 次序 / 明细）。</summary>
public sealed record StackLayer(
    StackRegion Region,
    int Order,
    string Id,
    string Title,
    IReadOnlyList<string> ModuleNames,
    int Bytes,
    string Fingerprint,
    string VersionLabel,
    IReadOnlyList<StackSegment> Segments,
    int MessageCount,
    string RegionText)
{
    /// <summary>零注入（该区此刻一个字节都不进 prompt）。</summary>
    public bool IsEmpty => Bytes == 0;

    /// <summary>该区正文（段间空行拼接）—— 合计与测试用，不进屏面。</summary>
    public string Text => RegionText;
}

/// <summary>
/// **P1 · 区栈逐字节面板** —— 把「这一轮的 prompt 由哪些区、各多少字节、指纹是什么」摊开来看。
/// <para>
/// 为什么值得做成面板：缓存失效的**唯一**判据是字节（<c>PITFALLS #8</c>：上游按 64-token 块对齐），
/// 而「哪个区变了」肉眼看不出来 —— 每次显示都给出各区指纹，变没变一眼可比（可落盘、可 diff，T3）。
/// </para>
/// <para>
/// 口径：字节 = 该区**贡献正文**的 UTF-8 字节，段间以空行（<c>\n\n</c>）拼接 ——
/// 与冻结前缀（<c>FrozenSnapshot</c>）的拼接口径一致，因此 R1 的字节数就是稳定前缀的真实规模。
/// 指纹 = <c>sha256(该区正文)</c> 前 12 位。
/// </para>
/// <para>
/// 只读保证：冻结区直接读 <c>Sections()</c>（= 它 <c>ContributeAsync</c> 会加的同一批文本）；
/// 动态区把 <c>ContributeAsync</c> 的产物收进**临时列表**。全程不调用 <c>ObserveAsync</c>、
/// 不写盘、不推进轮次（T1：面板调用前后 prompt 字节哈希必须相同）。
/// </para>
/// </summary>
public static class StackPanel
{
    /// <summary>指纹取前 12 个十六进制字符（够短够看，对「变了没」这个用途足够）。</summary>
    public const int FingerprintChars = 12;

    /// <summary>上游前缀缓存的块粒度（token）—— P2 面板的「块对齐余数」用它。</summary>
    public const int CacheBlockTokens = 64;

    /// <summary>规范次序（唯一排序依据）：R1-P → R1 → R2 → R4 → R5 → R3。</summary>
    public static readonly IReadOnlyList<StackRegion> Ordered =
        [StackRegion.R1P, StackRegion.R1, StackRegion.R2, StackRegion.R4, StackRegion.R5, StackRegion.R3];

    private static readonly Dictionary<StackRegion, string> Titles = new()
    {
        [StackRegion.R1P] = "R1-P 协议区（内核契约 · 收尾不可改 · 用户不可摘）",
        [StackRegion.R1] = "R1 冷冻区（稳定前缀：rules / knowledge / memory-index）",
        [StackRegion.R2] = "R2 事件流 / 会话历史（只追加，永不擦除）",
        [StackRegion.R4] = "R4 当前尾部（白板，大概率变）",
        [StackRegion.R5] = "R5 动态草稿（完全可变，唯一允许大删大改）",
        [StackRegion.R3] = "R3 语义焦点（每轮必变 ⇒ 居末）",
    };

    /// <summary>标准区名（R1-P / R1 / …）。</summary>
    public static string LabelOf(StackRegion region) => region switch
    {
        StackRegion.R1P => "R1-P",
        StackRegion.R1 => "R1",
        StackRegion.R2 => "R2",
        StackRegion.R4 => "R4",
        StackRegion.R5 => "R5",
        StackRegion.R3 => "R3",
        _ => region.ToString(),
    };

    /// <summary>该区在规范序里的位次（1 起）。</summary>
    public static int OrderOf(StackRegion region)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (Ordered[i] == region)
            {
                return i + 1;
            }
        }

        return Ordered.Count;
    }

    /// <summary>模块 → 区（问法：**这段字节属于哪一层**；动态区按标记接口归类）。</summary>
    public static StackRegion RegionOf(IRuntimeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return module switch
        {
            IFrozenZoneModule zone when zone.Zone == FrozenZone.Protocol => StackRegion.R1P,
            IFrozenZoneModule => StackRegion.R1,
            ITailRegionModule => StackRegion.R4,
            IDraftRegionModule => StackRegion.R5,
            IFocusRegionModule => StackRegion.R3,
            _ => StackRegion.R2,
        };
    }

    /// <summary>UTF-8 字节数（口径与「前缀指纹」一致：UTF-8 / LF）。</summary>
    public static int BytesOf(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>sha256 前 12 个十六进制字符（小写）。</summary>
    public static string FingerprintOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..FingerprintChars].ToLowerInvariant();

    /// <summary>
    /// 逐区盘点（只读）。<paramref name="context"/> 决定「此刻」是哪一轮（焦点 band 会随之变）。
    /// <para>走的是**与真发出去的请求同一段贡献代码**（<c>ContributeAsync</c>），产物交给
    /// <see cref="Compose"/> 合成 —— 所以它与引擎的 prompt 不可能各说各话。</para>
    /// </summary>
    public static async Task<IReadOnlyList<StackLayer>> InspectAsync(
        IReadOnlyList<IRuntimeModule> modules,
        RuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(context);

        var trace = new List<ContributedMessage>();
        var scratch = new List<ChatMessage>();
        foreach (var module in modules)
        {
            scratch.Clear();
            await module.ContributeAsync(context, scratch, cancellationToken).ConfigureAwait(false);
            foreach (var message in scratch)
            {
                trace.Add(new ContributedMessage(module, message));
            }
        }

        return Compose(trace);
    }

    /// <summary>
    /// **由真实组装产物**合成区栈（trace = <c>RequestAssembler</c> 在组装那一刻记下的「谁加了哪条消息」）。
    /// <para>这是 T1 的结构性保证：右栏 / 面板显示的字节就是**那一次请求里真实存在的消息**，
    /// 不是事后另拼的一份。</para>
    /// </summary>
    public static IReadOnlyList<StackLayer> Compose(IReadOnlyList<ContributedMessage> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var buckets = new Dictionary<StackRegion, Bucket>();
        foreach (var region in Ordered)
        {
            buckets[region] = new Bucket();
        }

        // 冻结区：段的账本信息（id / 版本）按序与消息一一对应（FrozenZoneModuleBase 保证多段即多条）。
        var frozenCounters = new Dictionary<IRuntimeModule, int>();

        // 按 trace 次序走（= prompt 贡献次序）：同一区里的多个模块也保持这个相对次序。
        foreach (var (module, message) in trace)
        {
            var bucket = buckets[RegionOf(module)];
            if (!bucket.Modules.Contains(module.Name, StringComparer.Ordinal))
            {
                bucket.Modules.Add(module.Name);
            }

            bucket.Parts.Add(message.Content);
            bucket.MessageCount++;

            if (module is IFrozenZoneModule zone)
            {
                frozenCounters.TryGetValue(module, out var index);
                frozenCounters[module] = index + 1;

                var sections = zone.Sections();
                var id = index < sections.Count ? sections[index].SectionId : module.Name;
                var version = index < sections.Count ? sections[index].Version : "—";
                bucket.Segments.Add(new StackSegment(
                    id, version, BytesOf(message.Content), FingerprintOf(message.Content)));
                continue;
            }

            bucket.Segments.Add(new StackSegment(
                module.Name, "—", BytesOf(message.Content), FingerprintOf(message.Content)));
        }

        var layers = new List<StackLayer>(Ordered.Count);
        foreach (var region in Ordered)
        {
            var bucket = buckets[region];
            var text = string.Join("\n\n", bucket.Parts);
            layers.Add(new StackLayer(
                region,
                OrderOf(region),
                LabelOf(region),
                Titles[region],
                bucket.Modules,
                BytesOf(text),
                FingerprintOf(text),
                VersionLabelOf(bucket),
                bucket.Segments,
                bucket.MessageCount,
                text));
        }

        return layers;
    }

    /// <summary>
    /// 各区正文按规范序拼接的**合计**（零注入区不贡献字节，也不贡献分隔符）——
    /// 这样「摘掉一个模块」时合计的变化就等于它那一区的字节数（可当场核对）。
    /// </summary>
    public static string TotalText(IReadOnlyList<StackLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        return string.Join("\n\n", layers.Where(l => !l.IsEmpty).Select(l => l.Text));
    }

    /// <summary>合计字节数（屏面上的「合计 N B」就是这个口径）。</summary>
    public static int TotalBytes(IReadOnlyList<StackLayer> layers) => BytesOf(TotalText(layers));

    /// <summary>绘制区栈表（纯文本、可落盘、可 diff）。</summary>
    public static async Task<IReadOnlyList<string>> RenderAsync(
        IReadOnlyList<IRuntimeModule> modules,
        RuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        var layers = await InspectAsync(modules, context, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "[区栈] 规范序 R1-P → R1 → R2 → R4 → R5 → R3（动态区越易变越靠后；R3 必变 ⇒ 其后不留任何字节）",
            $"[区栈] 字节口径：各区贡献正文 UTF-8 字节（段间空行拼接，与冻结前缀同口径）；指纹 = sha256 前 {FingerprintChars} 位",
            "次序  区     字节      指纹          版本      段数  模块",
        };

        foreach (var layer in layers)
        {
            lines.Add(string.Join("  ",
                layer.Order.ToString().PadRight(4),
                layer.Id.PadRight(5),
                layer.Bytes.ToString().PadRight(9),
                layer.Fingerprint.PadRight(13),
                layer.VersionLabel.PadRight(9),
                layer.Segments.Count.ToString().PadRight(5),
                layer.ModuleNames.Count == 0 ? "（无）" : string.Join("+", layer.ModuleNames)));
        }

        var total = TotalText(layers);
        lines.Add($"[区栈] 合计：{BytesOf(total)} 字节 / 指纹 {FingerprintOf(total)}（= 各非空区正文按规范序拼接、段间空行；不含本轮用户消息）");

        var empty = layers.Where(l => l.IsEmpty).Select(l => l.Id).ToArray();
        lines.Add(empty.Length == 0
            ? "[区栈] 零注入区：无（六个区都有字节）"
            : $"[区栈] 零注入区：{string.Join("、", empty)}（此刻不进 prompt —— 摘掉该模块也会得到同一份字节）");

        lines.Add("[区栈] 分段明细（段 id @ 版本 → 字节 / 指纹）：");
        foreach (var layer in layers)
        {
            if (layer.Segments.Count == 0)
            {
                lines.Add($"  · {layer.Id}:（空，零注入）");
                continue;
            }

            foreach (var segment in layer.Segments)
            {
                lines.Add($"  · {layer.Id} {segment.Id} @ {segment.Version} → {segment.Bytes} 字节 / {segment.Fingerprint}");
            }
        }

        lines.Add($"[区栈] 协议区版本：v{ProtocolText.Version}（代码常量，只随内核版本演进；版本号只进账本、不进 prompt）");
        lines.Add($"[区栈] 冻结前缀指纹（R1-P + R1 全段，FrozenPrefix.Assemble）：{FrozenPrefix.Assemble(modules).Id}");

        return lines;
    }

    /// <summary>把区栈表落盘（与屏上逐字节相同 —— T3：可复算、可 diff）。</summary>
    public static async Task<string> DumpAsync(
        IReadOnlyList<IRuntimeModule> modules,
        RuntimeContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        var lines = await RenderAsync(modules, context, cancellationToken).ConfigureAwait(false);
        var full = Path.GetFullPath(RuntimeConfiguration.ExpandHome(path));
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, Join(lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    /// <summary>行列表 → 单块文本（LF，无 BOM：账本类文件口径）。</summary>
    public static string Join(IReadOnlyList<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    private static string VersionLabelOf(Bucket bucket)
    {
        var versions = bucket.Segments
            .Where(s => !string.Equals(s.Version, "—", StringComparison.Ordinal))
            .Select(s => s.Version)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return versions.Length switch
        {
            0 => "—",
            1 => $"v{versions[0]}",
            _ => $"多版本({versions.Length})",
        };
    }

    private sealed class Bucket
    {
        public List<string> Modules { get; } = [];

        public List<string> Parts { get; } = [];

        public List<StackSegment> Segments { get; } = [];

        public int MessageCount { get; set; }
    }
}

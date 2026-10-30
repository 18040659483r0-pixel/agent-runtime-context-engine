using AgentRuntime.Core;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Models;

namespace AgentRuntime.Hosting;

/// <summary>
/// **一次组装的完整产物**：真实请求 + 逐区归属 + 字节指纹。
/// <para>
/// 三件东西**来自同一遍组装**（<c>RequestAssembler.AssembleAsync</c> 的 trace），
/// 因此「显示的那份」与「发出去的那份」是同一个对象 —— T1（界面不改字节）由此从纪律变成结构。
/// </para>
/// </summary>
public sealed class PromptComposition
{
    public PromptComposition(
        ChatRequest request,
        IReadOnlyList<StackLayer> layers,
        string sha256,
        int bytes,
        IReadOnlyDictionary<string, int>? moduleBytes = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Layers = layers ?? throw new ArgumentNullException(nameof(layers));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Bytes = bytes;
        ModuleBytes = moduleBytes ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>
    /// **每个模块这一轮实际拼进上下文的字节数**（2026-10-29 主人令）。
    /// <para>来源 = 同一遍组装的 trace（`RequestAssembler` 逐条记「这条消息是谁加的」）；
    /// 没出现的模块 = 这一轮没拼装 ⇒ 界面据此标 `(Cached)`，让人一眼看出谁真进了上下文。</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ModuleBytes { get; }

    /// <summary>从 trace 汇总「模块名 → 字节数」（唯一实现处）。</summary>
    public static IReadOnlyDictionary<string, int> ModuleBytesOf(IReadOnlyList<ContributedMessage> trace)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in trace)
        {
            var name = item.Module?.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var size = item.Message?.Content is { Length: > 0 } text
                ? System.Text.Encoding.UTF8.GetByteCount(text)
                : 0;
            map[name] = map.TryGetValue(name, out var current) ? current + size : size;
        }

        return map;
    }

    /// <summary>真发出去的那一份请求（预览时 = 下一次会发的那一份）。</summary>
    public ChatRequest Request { get; }

    /// <summary>六个区（规范序 R1-P → R1 → R2 → R4 → R5 → R3），由 <see cref="StackPanel.Compose"/> 从 trace 合成。</summary>
    public IReadOnlyList<StackLayer> Layers { get; }

    /// <summary>请求体完整 sha256（<c>PromptBytes</c>，与线上字节逐字节同源）。</summary>
    public string Sha256 { get; }

    /// <summary>请求体字节数（= 真要发出去的 body 长度）。</summary>
    public int Bytes { get; }

    /// <summary>指纹前 12 位（屏面上显示的就是它）。</summary>
    public string Fingerprint12 => Sha256[..12];

    /// <summary>某区的字节数（不存在的区 = 0）。</summary>
    public int BytesOf(StackRegion region) => Layers.FirstOrDefault(l => l.Region == region)?.Bytes ?? 0;

    /// <summary>六个区的字节表（Δ 计算与显示用）。</summary>
    public IReadOnlyDictionary<StackRegion, int> RegionBytes()
    {
        var map = new Dictionary<StackRegion, int>(Layers.Count);
        foreach (var layer in Layers)
        {
            map[layer.Region] = layer.Bytes;
        }

        return map;
    }
}

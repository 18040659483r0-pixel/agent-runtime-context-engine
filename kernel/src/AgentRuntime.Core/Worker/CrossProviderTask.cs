using System;
using System.Collections.Generic;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// 跨 provider 任务：派发物从「一行文本」升级为「对象」（2026-09-27）。
/// <para><b>2026-10-06（主人令）</b>：<b>知识不再随派发物走</b> —— worker 自带 R1（铁则区 + L1+L2，
/// 与 master **逐字节同一份**）与自持语料（L3/L4 按 id 自己下探）。事由：提前预判「该带哪几条知识」
/// 很难准，来回反复传反而增加消耗。本对象现在只装配：任务 / provider / 窗口 / 基准 / 应有集合 /
/// 要什么结论 / **退回修正语（NOTE）**。</para>
/// </summary>
public sealed record CrossProviderTask
{
    public string Task { get; init; } = string.Empty;
    public string TargetProvider { get; init; } = string.Empty;

    /// <summary>**退回修正语**（自愈回路）：上一次回执不过退时写入，随下一次派发原样带过去。
    /// <para>它**不是知识**（知识已由 worker 自持）—— 它只是「这一次哪里不合形状」的一句修正。</para></summary>
    public string Note { get; init; } = string.Empty;

    public IReadOnlyList<string> Window { get; init; } = Array.Empty<string>();
    public string Mode { get; init; } = "sync";

    /// <summary>**工具基准**（A1）：worker 的工具（list/read/write）以此为根。
    /// <para>为什么必须随派发物走：master 基准 = 它自己那一层，worker 的 cwd 在别处 ——
    /// 同一个相对路径两边解析不同（2026-09-26 夜起 4/4 派发因此空手）。</para></summary>
    public string Base { get; init; } = string.Empty;
    public DispatchContract Contract { get; init; } = new();

    /// <summary>
    /// **应有集合**（v29）：这一步**什么算做完** —— master 在派发里写 <c>must:</c>，运行时原样带进 <c>MUST:</c> 段。
    /// <para>可核的三种形状（<c>full</c> / <c>lines A-B</c> / 空）由 <see cref="DispatchMust"/> 判；其余当自由文本只给 worker 看。</para>
    /// </summary>
    public string Must { get; init; } = string.Empty;

    /// <summary>
    /// **要什么结论**（主人 2026-09-29 19:14 令）：master 写 <c>ask:</c>，运行时原样带进 <c>ASK:</c> 段。
    /// <para>与 <see cref="Must"/> 的分工：<c>ask:</c> = 交什么（结论），<c>must:</c> = 交到什么程度算够（应有集合）。
    /// 两者都不许写「怎么做」—— 用什么工具、走几步由 Worker 自己决定。</para>
    /// </summary>
    public string Ask { get; init; } = string.Empty;

    /// <summary>**信封**（主人 2026-10-07 令）：这一条派发的 <c>[ENVELOPE]</c> 行（模式 / id / at / from / to）。
    /// <para>让收方知道「这是哪一条、谁发的、什么时候」—— 回执据此带 <c>re=</c> 对号。</para></summary>
    public string Envelope { get; init; } = string.Empty;

    /// <summary>**裁定回信**（主人 2026-10-07 令）：master 对 worker 临时回执的裁定（<c>[RULING-ANSWER]</c> 块原文）。
    /// <para>worker 上一轮卡在需人裁、链停住；下一次派发把裁定带过去 ⇒ 它接着干（默认续）。</para></summary>
    public string Ruling { get; init; } = string.Empty;

    /// <summary>**本意图的交付键**（主人 2026-10-07 令 · ⑤）：写成**动手前的目标** ——
    /// worker 在开工前就知道这次要交哪几个**成员**（而不是等被退回才知道）。空 = 用通用那句。</summary>
    public string ContractText { get; init; } = string.Empty;

    /// <summary>
    /// **装配成 worker prompt（字节级）** ── 全程 <c>byte[]</c>，不拼字符串、不 Trim、不转义（见 <see cref="DispatchFrame"/>）。
    /// <para>段名与 <c>WorkerProtocolText</c> 第 1 条**逐个对齐**（<c>TASK:</c> / <c>TARGET-PROVIDER:</c> / <c>NOTE:</c> /
    /// <c>base:</c> / <c>WINDOW:</c> / <c>MUST:</c> / <c>ASK:</c> / <c>CONTRACT:</c>）—— 契约说什么段名，这里就造什么，不另立。</para>
    /// <para>首行是帧头（总字节数 + sha）⇒ 「整体送入」与「中途没被谁动过」都能回查。</para>
    /// </summary>
    public byte[] ComposeBytes()
    {
        var window = new MemoryStream();
        foreach (var w in Window)
        {
            WriteLineBytes(window, "  " + w);
        }

        var sections = new List<DispatchFrame.Section> { DispatchFrame.Text("TASK", Task) };
        // ENVELOPE（v48）：身份 + 时刻 + id + re —— 放在 TASK 后，收方先知道「这是哪一条」。
        if (!string.IsNullOrWhiteSpace(Envelope))
        {
            sections.Add(DispatchFrame.Text("ENVELOPE", Envelope));
        }

        if (!string.IsNullOrWhiteSpace(TargetProvider))
        {
            sections.Add(DispatchFrame.Text("TARGET-PROVIDER", TargetProvider));
        }

        sections.Add(DispatchFrame.Text("NOTE", string.IsNullOrWhiteSpace(Note) ? "(none)" : Note));
        // RULING-ANSWER（v48）：上一次“需人裁”的裁定 —— 带过来它就能接着干。
        if (!string.IsNullOrWhiteSpace(Ruling))
        {
            sections.Add(DispatchFrame.Text("RULING-ANSWER", Ruling));
        }
        sections.Add(DispatchFrame.Text("base",
            string.IsNullOrWhiteSpace(Base) ? "(none -- refuse rather than guess)" : Base));
        sections.Add(new DispatchFrame.Section("WINDOW", window.ToArray()));
        // v29：**应有集合**总是出现（没声明写 `(none)`）—— 段形状稳定，worker 一眼看得出「这次有没有判据」。
        sections.Add(DispatchFrame.Text("MUST", string.IsNullOrWhiteSpace(Must) ? "(none)" : Must));
        sections.Add(DispatchFrame.Text("ASK", string.IsNullOrWhiteSpace(Ask) ? "(none)" : Ask));
        sections.Add(DispatchFrame.Text("CONTRACT", string.IsNullOrWhiteSpace(ContractText)
            ? "交回一个合格的 <receipt> 元素：<kind> + 该类要求的成员"
            : ContractText));
        return DispatchFrame.Compose(sections);
    }

    /// <summary>
    /// 装配成 prompt **文本**（薄壳：<b>唯一解码点</b> = <see cref="DispatchFrame.Decode"/>）。
    /// <para>供人读 / 日志 / 旧调用；真正送出去的那一跳也走这里 —— 于是「解码只发生一次」。</para>
    /// </summary>
    public string ComposePrompt() =>
        DispatchFrame.Decode(ComposeBytes());

    private static void WriteLineBytes(MemoryStream ms, string text)
    {
        var b = DispatchFrame.Of(text);
        ms.Write(b.Span);
        ms.WriteByte((byte)'\n');
    }
}

/// <summary>派发回执契约。</summary>
public sealed record DispatchContract
{
    public bool RequireBlock { get; init; } = true;
    public int MaxRetries { get; init; } = 1;
}

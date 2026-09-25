using AgentRuntime.Hosting.Panels;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui;

/// <summary>左/右栏焦点（Tab 切换）。</summary>
public enum PaneFocus
{
    /// <summary>输入行 / 对话流（左）。</summary>
    Input,

    /// <summary>状态块 + 详细块（右）。</summary>
    Right,
}

/// <summary>对话流的一行。Kind：<c>you</c> / <c>ai</c> / <c>turn</c>（↳ 记账行）/ <c>info</c>。</summary>
public sealed record ConversationEntry(string Kind, string Text);

/// <summary>
/// 一帧的**任务用量**（顶部框「会话」行：本 task **新增** + 缓存命中率；未知 = null ⇒ 写破折号）。
/// <para><b>「本 task 新增」的口径（主人 2026-09-22 03:4x 定）</b>：它是**本次 task 往流里追加的正文字节 ÷ 4**
/// （模型输出 + 工具结果 + 你的输入）—— 即「**这件事让上下文长了多少**」。
/// <b>不再用任何 prompt 数据</b>：旧口径 <c>Σ(未命中输入) + Σ(输出)</c> 里的「未命中输入」缓存一冷
/// 就等于整份 prompt，看上去像「总计 prompt」，而那个数没有价值（上下文是搬来搬去的）。
/// 新口径**缓存冷暖都不跳**，且逐条可数。</para>
/// </summary>
public sealed record SplitTaskUsage(int PromptTokens, int CachedTokens, int CompletionTokens, int AppendedChars = 0)
{
    /// <summary>未命中缓存的输入合计（<c>prompt − cached</c>，硬下限 0）。仅用于诊断，**不上屏**。</summary>
    public int UncachedTokens => Math.Max(0, PromptTokens - CachedTokens);

    /// <summary>本 task **新增**的 token（≈ 本次追加字节 ÷ 4；与协议区「≈token」同一口径）。</summary>
    public int AppendedTokens => AppendedChars / 4;

    /// <summary>命中率（prompt 为 0 ⇒ 0；不编造）。</summary>
    public double HitRate => PromptTokens == 0 ? 0d : (double)CachedTokens / PromptTokens;
}

/// <summary>区目录「区级状态表」的一行（列按右列宽度档位裁剪，见 <see cref="RegionColumns"/>）。</summary>
public sealed record SplitRegionRow(
    StackRegion Region,
    int Order,
    string Id,
    string ShortTitle,
    int Bytes,
    int Delta,
    string Fingerprint,
    string VersionTag,
    string Note = "");

/// <summary>详细块「区目录」的一项（折叠态只给摘要，展开才展正文）。</summary>
public sealed record SplitMenuEntry(StackRegion Region, string Id, string ShortTitle, string CountLabel, bool Expanded);

/// <summary>状态块合计（区栈字节 / ≈token / 真请求字节与指纹）。</summary>
public sealed record SplitTotals(int RegionBytes, int RequestBytes, string? RequestFingerprint)
{
    /// <summary>≈token：口径与协议区一致（~4 字节/token），屏面上标了 ≈ 就是估算。</summary>
    public int ApproxTokens => RegionBytes / 4;
}

/// <summary>「自报区」的一行：区短名 + 当前内容摘要（右栏固定块用）。</summary>
/// <param name="Label">区短名（如「白板 R4」）。</param>
/// <param name="Text">摘要正文（宽时显示）。</param>
/// <param name="Short">放不下时的**计数形式**（窄时显示）—— 与状态表同一条纪律：**窄了就换计数，绝不截字**。</param>
public sealed record SplitSelfReport(StackRegion Region, string Label, string Text, string Short, string CountText);

/// <summary>
/// **一帧双栏界面的全部输入**（纯数据）。
/// <para>渲染是纯函数（<see cref="SplitRenderer.Render"/>）⇒ 同一帧数据必定渲染出同一屏字节：
/// 这就是「交互没法自动化、但一帧可以」的底气（<c>--snapshot</c> 无头帧 / CI 断言 / diff）。</para>
/// </summary>
public sealed class SplitFrame
{
    public required string Title { get; init; }

    /// <summary>协议版本（顶部框「会话」行；**协议区的常量**，不是配置项）。</summary>
    public string ProtocolVersion { get; init; } = AgentRuntime.Core.Protocol.ProtocolText.Version;

    /// <summary>动态模块数（顶部框「会话」行）。</summary>
    public int ModuleCount { get; init; }

    /// <summary>本 task 用量（顶部框「会话」行：本 task +N tok · 缓存命中 M%；null = 未知 ⇒ 破折号）。</summary>
    public SplitTaskUsage? TaskUsage { get; init; }

    public required string Subtitle { get; init; }

    public required IReadOnlyList<ConversationEntry> Conversation { get; init; }

    public required IReadOnlyList<SplitRegionRow> Regions { get; init; }

    /// <summary>
    /// **自报区**（白板 R4 / 焦点 R3 / 草稿 R5 的当前内容；每轮刷新）。
    /// <para>为什么要有它：这三块原本是**模型回复末尾的自报块**（<c>[TAIL]</c>/<c>[FOCUS]</c>/<c>[DRAFT]</c>），
    /// 原样混在对话里只会盖住人话——所以正文里切掉（<c>SplitSession.SelfReportProse</c>），
    /// 在这里占一块**固定区域**给人一眼看。</para>
    /// </summary>
    public IReadOnlyList<SplitSelfReport> SelfReport { get; init; } = [];

    /// <summary>
    /// **R1 里实际装载的专家知识分类**（顶层框「专家」行；空 = 这一轮一个 expert 段都没有）。
    /// <para>口径 = <see cref="StackPanel.ExpertDomains"/>：只报**真的装进 R1 的**域 ——
    /// 不从配置反推（配了但没内容 = 没装载，屏上不许说装上了）。</para>
    /// <para>次序由宿主给（**占用降序**；主人 2026-09-22 令），渲染不再排序 —— 屏面就是宿主那一份。</para>
    /// </summary>
    public IReadOnlyList<ExpertDomain> ExpertDomains { get; init; } = [];

    public required SplitTotals Totals { get; init; }

    /// <summary>状态块标题行的「请求字节 / 指纹」注记（真发出去那一份，或预览）。</summary>
    public required string RequestNote { get; init; }

    public TurnRecord? LastTurn { get; init; }

    public required IReadOnlyList<SplitMenuEntry> Menu { get; init; }

    public required int MenuSelection { get; init; }

    public string? DetailTitle { get; init; }

    public required IReadOnlyList<string> DetailLines { get; init; }

    public required int DetailScroll { get; init; }

    public required int ConversationScroll { get; init; }

    public required string Input { get; init; }

    /// <summary>
    /// **底部状态条上的「简短最终结果」**（v14）：最新一条会话事实的首行（宿主给；空 = 只显示用量）。
    /// <para>为什么放这儿（主人 2026-09-22 00:1x）：「事实状态」有个**固定位置**，紧贴输入区上方；
    /// 它不参与正文区高度计算 ⇒ 输入变长 / 输入法合成都不会让它动。</para>
    /// </summary>
    public string LastNote { get; init; } = string.Empty;

    /// <summary>
    /// **输入面板行数**（宿主给的值；<c>0</c> = 按内容自动算，仅测试用）。
    /// <para>v14：宿主一律给 <see cref="SplitLayout.InputPanelRows"/>（**恒定**）——
    /// 行数一旦跟着内容走，整帧就会随输入重排（那正是主人报的「跳」）。内容超出面板时由渲染器
    /// **显示尾部**（光标行恒在最下），不再改帧结构。</para>
    /// </summary>
    public int InputRows { get; init; }

    /// <summary>
    /// **task 读数**（协议 v10 · 需求 E）：整个 task 生命周期的读秒 + 轮数（如 <c>task 00:42 · 3 轮</c>）。
    /// <para>为什么是 task 级而不是每轮：每轮读秒只说明「这一次请求多久」——人要的是「这件事干了多久」。
    /// 需要人并停下来时它也停（见 <c>SplitSession</c> 的 task 钟）。</para>
    /// </summary>
    public string TaskLabel { get; init; } = string.Empty;

    /// <summary>
    /// **就地展开的区**（主人 2026-09-20 15:3x：白板 / 草稿 / 焦点**默认展开**，内容放在**原来那个展开图标下面**，
    /// 不另占一块区域）。默认 = 空；宿主（SplitSession）把 R4/R5/R3 放进来。
    /// </summary>
    public IReadOnlyList<StackRegion> InlineExpanded { get; init; } = [];

    public required int Caret { get; init; }

    public required PaneFocus Focus { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

/// <summary>
/// **状态块列宽档位**（窄时省列）—— 让「笔记本半屏」也能放下：
/// 宽了给全套（含版本），窄了先丢版本、再收短指纹，最后连指纹也不要（只留 次序/区/字节/Δ）。
/// <para>Δ 与次序永不省：它们才是"这一轮哪一块变了"的答案。</para>
/// </summary>
public readonly record struct RegionColumns(
    int OrderWidth,
    int IdWidth,
    int BytesWidth,
    int DeltaWidth,
    int FingerprintChars,
    bool ShowVersion)
{
    /// <summary>
    /// 按右列可用宽度挑档位。
    /// <para>硬规则（主人 2026-09-16 01:0x 定）：<b>窄了就整列省，绝不截字</b>；
    /// <b>次序 / 区 / 字节 / Δ 四列永不省</b>（它们是「哪一块变了」的答案）。
    /// 优先级从低到高省：指纹列 → 版本列 → （仍不够时）合计行的 token 估算。</para>
    /// </summary>
    public static RegionColumns For(int rightWidth) => rightWidth switch
    {
        >= 48 => new RegionColumns(4, 5, 6, 6, 12, ShowVersion: true),
        >= 38 => new RegionColumns(4, 5, 6, 6, 12, ShowVersion: false),
        >= 34 => new RegionColumns(4, 5, 6, 6, 8, ShowVersion: false),
        >= 31 => new RegionColumns(4, 5, 6, 6, 6, ShowVersion: false),
        _ => new RegionColumns(4, 5, 6, 6, 0, ShowVersion: false),
    };

    /// <summary>必需四列的最小占宽（次序 / 区 / 字节 / Δ，含列间一个空格）。</summary>
    public int MandatoryWidth => (OrderWidth + 1) + (IdWidth + 1) + (BytesWidth + 1) + DeltaWidth;
}

/// <summary>
/// **双栏几何（唯一算法处）** —— 主人 2026-09-16 00:53 定的硬约束：
/// <list type="bullet">
/// <item><b>状态块固定右上角</b>，面积 ≈ 窗口 1/6（右列宽 ≈ 总宽 1/3 × 状态块高 ≈ 右列 1/2）。</item>
/// <item>右列上 = 状态（固定行数）· 下 = 详细（展开正文 / 面板输出，可滚动）。</item>
/// <item>左列（对话）≈ 总宽 2/3，<b>新消息贴底</b>，输入行在最下。</item>
/// <item>目标 <b>96×30</b>（笔记本半屏）放得下全部；<b>80×24 也不垮</b>；宽 &lt; <see cref="MinWidth"/> 或高 &lt; <see cref="MinHeight"/> 落 plain。</item>
/// </list>
/// </summary>
public readonly record struct SplitLayout(int Width, int Height, int InputRows = 2 /* = InputPanelRows；主构造默认值不能引用本类型常量 */, int InlineRows = 0)
{
    /// <summary>输入区最多占几行（长输入不会把对话挤没；超过就**面板内向上滚**，见 <c>InputRows</c>）。</summary>
    public const int MaxInputRows = 5;

    /// <summary>底部**状态条**占的行数（**恒定 1 行**：简短最终结果 + 上一轮用量；主人 2026-09-22 定）。</summary>
    public const int StripRows = 1;

    /// <summary>
    /// 该尺寸下的状态条行数（**同一屏高恒定**）：<b>矮屏（&lt; 20 行）整条让位给正文</b>——
    /// 那一档的 token / 上一轮读数本来就在右栏状态块里，底部条省掉不丢事实。
    /// </summary>
    public static int StripRowsFor(int height) => height >= 20 ? StripRows : 0;

    /// <summary>
    /// **输入面板占的行数（恒定，v14）** —— 这是「输入法不再让框跳」的结构性前提。
    /// <para>旧版把「按内容算出的行数」直接喂给布局 ⇒ 输入一变高 <see cref="BodyRows"/> 跟着变 ⇒ **整帧重排**
    /// （右栏的「合计 / 上一轮」行还会跨越 18/20 阈值**整行消失**）⇒ 真机表现就是合成过程中框在跳
    /// （主人 2026-09-22 00:1x 报）。现在面板与状态条**行数固定**，内容超出只在面板内向上滚
    /// （尾部可见、光标行恒在最下）：**帧的其余部分逐字节稳定**。
    /// </para>
    /// </summary>
    public const int InputPanelRows = 2;

    /// <summary>
    /// 该尺寸下的输入面板行数（**同一屏高恒定** ⇒ 输入法 / 长文本都不会让帧重排）。
    /// <para>矮屏（&lt; 24 行）优先把行还给正文：面板收成 1 行 —— 这是**按尺寸**定的，
    /// 不是按内容定的，所以输入过程里它一个字都不会变。</para>
    /// </summary>
    public static int InputPanelRowsFor(int height) => height >= 24 ? InputPanelRows : 1;
    /// <summary>降级阈值（规格 §二·1.6）：宽 &lt; 72 列 ⇒ plain。</summary>
    public const int MinWidth = 72;

    /// <summary>降级阈值：高 &lt; 20 行 ⇒ plain。</summary>
    public const int MinHeight = 20;

    /// <summary>状态块固定行数（v15 后**顶层信息框不再用它**；保留给旧渲染函数 / 测试）。</summary>
    public const int FixedStatusRows = 8;

    /// <summary>
    /// **顶部信息框的内容行数（不含它的上下框线）** —— v15 **单栏**（主人 2026-09-22 02:2x）：
    /// 右栏整块取消，区栈 / 自报 / 详细全部搬进顶部那个框。三档：≥28 行 = 6（会话 · 区栈 · **专家** · 白板 · 草稿 · 焦点）·
    /// 22–27 行 = 3（会话 · 区栈压成合计 · 自报压成一行计数）· 更低 = 2（会话 · 区栈合计）。
    /// </summary>
    public int TopBandContentRows => Height >= 28 ? 6 : Height >= 22 ? 3 : 2;

    /// <summary>顶部信息框占的总行数（含上下框线）。</summary>
    public int TopBandRows => TopBandContentRows + 2;

    /// <summary>正文区行数（**全宽**）：总高 − 顶框（含上下框线）− 分隔线 − 下框 − 状态条 − 输入区。</summary>
    public int BodyRows => Height - 4 - TopBandContentRows - StripRowsFor(Height) - Math.Clamp(InputRows, 1, MaxInputRows);

    /// <summary>左列内宽（v15：**正文占满整宽**，右栏已取消 ⇒ 它就是全宽）。</summary>
    public int LeftWidth => Width - 2;

    /// <summary>右列内宽（v15：**恒为 0** —— 右栏整块取消，保留此属性只为旧渲染函数仍能编译）。</summary>
    public int RightWidth => 0;

    /// <summary>矮时省「合计」行（再矮就只剩六区 + 表头）。</summary>
    public bool ShowTotalsRow => BodyRows >= 18;

    /// <summary>矮时省「上一轮」用量行。</summary>
    /// <summary>矮时省「上一轮」用量行（v14：阈值 20 → 18 —— 底部状态条已常驻显示同一读数）。</summary>
    public bool ShowUsageRow => BodyRows >= 18;

    /// <summary>状态块占的行数（右列上半）。</summary>
    /// <summary>状态块行数（含**就地展开**的正文行 —— 它占用的是状态块自己的高度，不另开一块）。</summary>
    public int StatusRows => FixedStatusRows + (ShowTotalsRow ? 1 : 0) + (ShowUsageRow ? 1 : 0) + Math.Max(0, InlineRows);

    /// <summary>矮窗口整块省掉「自报区」—— 保状态表与对话（尺寸是资源预算，不是面子）。</summary>
    /// <summary>
    /// 自报区（白板 R4 / 焦点 R3 / 草稿 R5）**几乎总是显示**：主人明确要求白板与草稿在右侧状态区看得见 ——
    /// 原先 <c>BodyRows &gt;= 18</c> 会在矮窗口把整块省掉，于是「打开后没发现」。现在只在**极小窗口**才让位给对话。
    /// </summary>
    public bool ShowSelfReport => BodyRows >= 8;

    /// <summary>高窗口给自报区每区两行（折行显示正文）；矮窗口每区一行（放不下就换计数）。</summary>
    public bool TallSelfReport => BodyRows >= 24;

    /// <summary>自报区占的行数（中缝 1 + 三个区各 1~2 行）；矮窗口为 0。</summary>
    /// <summary>详细块（区目录 / 面板输出）**至少**留几行 —— 自报区不许把它挤没（它是人看面板的地方）。</summary>
    public const int DetailMinRows = 3;

    /// <summary>自报区行数：要它，但**先给详细块留够**（不够就缩，缩到 0 也不硬挤）。</summary>
    public int SelfReportRows
    {
        get
        {
            if (!ShowSelfReport)
            {
                return 0;
            }

            // 上限按「高窗口那档」算（7 行），能放几行由 `room` 定 —— 逐区再决定给不给两行
            // （白板优先，见 `PerRegion`）。这样中等窗口也能看见**白板的正文**，而不是只剩计数。
            var room = BodyRows - StatusRows - 2 - DetailMinRows;
            return Math.Clamp(room, 0, SplitRenderer.SelfReportRowCountTall);
        }
    }

    /// <summary>详细块可用行数（右列下半，去掉中缝那一行）。</summary>
    public int DetailRows => Math.Max(0, BodyRows - StatusRows - SelfReportRows - 1);

    /// <summary>本尺寸下状态表的列档位。</summary>
    public RegionColumns Columns => RegionColumns.For(RightWidth);

    /// <summary>尺寸够不够画双栏。</summary>
    public static bool Fits(int width, int height) => width >= MinWidth && height >= MinHeight;
}

/// <summary>一帧的纯文本渲染（无 ANSI 转义 ⇒ 可落盘、可 diff、可 CI 断言）。</summary>
public static class SplitRenderer
{
    private const string LeftHeader = "对话（全宽）";

    /// <summary>「自报区」固定行数（矮窗口）：中缝 1 + 白板 / 焦点 / 草稿 各 1。</summary>
    public const int SelfReportRowCount = 4;

    /// <summary>「自报区」固定行数（高窗口）：中缝 1 + 白板 / 焦点 / 草稿 各 2（折行显示正文）。</summary>
    public const int SelfReportRowCountTall = 7;

    private const string SelfReportHeader = "自报 · 白板/焦点/草稿（每轮刷新；全文 /tail /focus /draft）";

    /// <summary>窄列时的短标题（不截字、不用省略号）。</summary>
    private const string SelfReportHeaderShort = "自报";

    private static readonly Dictionary<StackRegion, string> ShortTitles = new()
    {
        [StackRegion.R1P] = "协议",
        [StackRegion.R1] = "冷冻",
        [StackRegion.R2] = "事件流",
        [StackRegion.R4] = "白板",
        [StackRegion.R5] = "草稿",
        [StackRegion.R3] = "焦点",
    };

    /// <summary>区短名（状态表 / 目录用）。</summary>
    public static string ShortTitleOf(StackRegion region) => ShortTitles[region];

    /// <summary>由区栈（真实组装产物）造出状态表六行。</summary>
    public static IReadOnlyList<SplitRegionRow> BuildRows(
        IReadOnlyList<StackLayer> layers,
        IReadOnlyDictionary<StackRegion, int>? deltas,
        IReadOnlyDictionary<StackRegion, string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var rows = new List<SplitRegionRow>(layers.Count);
        foreach (var layer in layers)
        {
            var delta = deltas is not null && deltas.TryGetValue(layer.Region, out var value) ? value : 0;
            rows.Add(new SplitRegionRow(
                layer.Region, layer.Order, layer.Id, ShortTitleOf(layer.Region),
                layer.Bytes, delta, layer.Fingerprint, VersionTagOf(layer),
                notes is not null && notes.TryGetValue(layer.Region, out var note) ? note : string.Empty));
        }

        return rows;
    }

    /// <summary>逐区增量（当前 − 基准）；基准里没有的区视为「原本就是这么多」（Δ=0）。</summary>
    public static IReadOnlyDictionary<StackRegion, int> Deltas(
        IReadOnlyList<StackLayer> layers,
        IReadOnlyDictionary<StackRegion, int>? baseline)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var map = new Dictionary<StackRegion, int>(layers.Count);
        foreach (var layer in layers)
        {
            var before = baseline is not null && baseline.TryGetValue(layer.Region, out var value) ? value : layer.Bytes;
            map[layer.Region] = layer.Bytes - before;
        }

        return map;
    }

    /// <summary>区目录（折叠态）：每区一项，附行数（事件流给「条」）。</summary>
    public static IReadOnlyList<SplitMenuEntry> BuildMenu(IReadOnlyList<StackLayer> layers, StackRegion? expanded)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var menu = new List<SplitMenuEntry>(layers.Count);
        foreach (var layer in layers)
        {
            var count = layer.Region == StackRegion.R2 ? $"{layer.MessageCount} 条" : $"{LineCount(layer.Text)} 行";
            menu.Add(new SplitMenuEntry(layer.Region, layer.Id, ShortTitleOf(layer.Region), count, expanded == layer.Region));
        }

        return menu;
    }

    /// <summary>渲染一帧（**纯文本网格**）—— 富文本渲染的 <c>.Text</c> 投影（每行显示宽度恰好 = Width，行数恰好 = Height）。</summary>
    public static IReadOnlyList<string> Render(SplitFrame frame) =>
        [.. RenderRich(frame).Select(static line => line.Text)];

    /// <summary>
    /// 帧 → 布局（**唯一声明处**：渲染与光标定位共用同一把尺子，绝不各算一份）。
    /// <para>就地展开的正文行数先给详细块留够（<c>DetailMinRows</c>），不够就只展开前几行（与自报区同一纪律）。
    /// v13-B（主人 2026-09-22 00:00 选 A）：**展开的正文归「详细区」**（下、可滚动），状态块恢复**固定行数** ——
    /// 右上角只留状态 + 目录 + tokens（不再被正文顶大、也不再挤掉 tokens 行）⇒ <c>InlineRows = 0</c>。</para>
    /// </summary>
    private static SplitLayout LayoutFor(SplitFrame frame) =>
        new SplitLayout(
            frame.Width,
            frame.Height,
            frame.InputRows > 0 ? Math.Clamp(frame.InputRows, 1, SplitLayout.MaxInputRows) : InputRowsFor(frame.Input, frame.Width))
        with { InlineRows = 0 };

    /// <summary>
    /// **自绘光标（<see cref="CaretGlyph"/>）在帧上的行列**（**1-based**，直接喂终端 <c>\e[r;cH</c>）。
    /// <para>为什么要有它（2026-09-22 主人报的「IME 第二刀」）：关掉 DECAWM 只治「越界折行 ⇒ 顶高 / 滚屏」；
    /// 但**输入法的预编辑串与候选框画在「终端光标」处**（它不在我们的缓冲里、也不受我们控制），而我们从不移动
    /// 终端光标 ⇒ 它停在最后一次写入的位置（帧末尾），候选串就出现在帧底部而不是输入框里。把终端光标定位到
    /// **输入光标**处，两件事一起治。</para>
    /// <para>实现与画出来的帧**同源**：取 <see cref="Render"/> 的同一批帧行，只在**输入面板那几行**
    /// （末行 = 下边框，其上方紧邻 <see cref="SplitLayout.InputRows"/> 行）里扫标记；列号按**显示宽度**算（CJK 占 2 格）。</para>
    /// </summary>
    /// <returns>这一帧里没有标记（输入面板被整条省掉 / 焦点在右栏时仍画标记，故正常都有）⇒ <c>null</c>：调用方不定位即可。</returns>
    public static (int Row, int Col)? CaretPosition(SplitFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var rows = Math.Clamp(LayoutFor(frame).InputRows, 1, SplitLayout.MaxInputRows);
        var lines = Render(frame);

        for (var i = Math.Max(0, lines.Count - 1 - rows); i < lines.Count - 1; i++)
        {
            var at = lines[i].IndexOf(CaretGlyph, StringComparison.Ordinal);
            if (at >= 0)
            {
                // 1-based 行 / **视觉列**（前缀里可能有 CJK，字符下标 ≠ 显示列）。
                return (i + 1, TerminalText.WidthOf(lines[i][..at]) + 1);
            }
        }

        return null;
    }

    /// <summary>源文本 → 富文本（标记解析 + 保守标注；呈现层唯一入口）。</summary>
    private static RichText Rich(string? text) => Annotator.Annotate(Markup.Parse(text));

    /// <summary>整行统一成一个角色（用户行 = 斜体；行内标记不再抢角色 —— 整行一支笔画到底）。</summary>
    private static RichText UniformRole(RichText line, StyleRole role) =>
        line.Text.Length == 0 ? line : new RichText(line.Text, [new RichSpan(0, line.Text.Length, role)]);

    /// <summary>
    /// 渲染一帧（**富文本网格**：带角色段，供落屏上色）。
    /// <para>与 <see cref="Render"/> **同一条投影**：<c>Render(f)</c> 逐字节等于 <c>RenderRich(f)</c> 的正文 —— 闸门钉住。</para>
    /// </summary>
    public static IReadOnlyList<RichText> RenderRich(SplitFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var layout = LayoutFor(frame);
        var left = ConversationRich(frame, layout);

        // 左列：**自顶向下填充**；内容超过可视高度时自动贴底（最新仍在最下）、↑ 可回看。
        var area = Math.Max(0, layout.BodyRows - 1);
        var overflow = Math.Max(0, left.Count - area);
        var start = Math.Clamp(overflow - frame.ConversationScroll, 0, overflow);
        var leftRows = new RichText[Math.Max(layout.BodyRows, 0)];
        if (layout.BodyRows > 0)
        {
            leftRows[0] = RichText.From(HeaderCell(LeftHeader, false, layout.LeftWidth));
        }

        for (var i = 0; i < Math.Min(area, left.Count - start); i++)
        {
            leftRows[1 + i] = left[start + i];
        }

        // v15 **单栏**（主人 2026-09-22 02:2x）：右栏整块取消 —— 区栈 / 自报 / 详细全部搬进**顶部那个框**；
        // 正文占满整个宽度（卡与结果因此少折行、更好读）。面板输出由宿主塞进 Conversation（kind=panel）。
        var lines = new List<RichText>(frame.Height);
        lines.AddRange(TopBand(frame, layout));

        for (var i = 0; i < Math.Max(0, layout.BodyRows); i++)
        {
            // ⚠️ 正文行必须**自带左右边框**（与顶框 / 状态条同一口径）——否则整行宽度 = 内宽 ≠ 帧宽。
            lines.Add(RichText.From("│").Concat(Pad(leftRows[i] ?? RichText.Empty, layout.LeftWidth)).Append("│"));
        }

        lines.Add(RichText.From("├" + new string('─', frame.Width - 2) + "┤"));
        if (SplitLayout.StripRowsFor(frame.Height) > 0)
        {
            lines.Add(StripRow(frame, layout));
        }

        lines.AddRange(InputRows(frame, layout).Select(RichText.From));
        lines.Add(RichText.From("└" + new string('─', frame.Width - 2) + "┘"));
        return lines;
    }

    /// <summary>
    /// 底部状态条那一行的**富文本**版：把提示段（<c>ⓘ …</c>）标成**海蓝**（<see cref="StyleRole.Hint"/>），
    /// 用量部分保持中性（它是读数，不是提示）。
    /// <para>正文与 <see cref="StripLine"/> **同源**（直接对它做字符串切段）——标记是零宽，
    /// 帧宽与「纯文本帧 == 富文本帧正文」闸门照旧成立（2026-09-22 02:0x 主人定「这部分提示改成海蓝色」）。</para>
    /// </summary>
    private static RichText StripRow(SplitFrame frame, SplitLayout layout)
    {
        var plain = StripLine(frame, layout);
        var from = plain.IndexOf("ⓘ ", StringComparison.Ordinal);
        if (from < 0)
        {
            return RichText.From(plain);      // 提示没上去（放不下）⇒ 整行中性
        }

        var hint = plain[from..];
        var trimmed = hint.TrimEnd();
        return RichText.From(plain[..from])
            .Concat(Markup.Parse($"[[hint]]{trimmed}[[/]]"))
            .Concat(RichText.From(hint[trimmed.Length..]));   // 补回被 TrimEnd 掉的填充空格（保整行宽度）
    }

    /// <summary>
    /// **底部状态条**（v14：**固定 1 行**，紧贴输入区上方）：简短最终结果（左）+ 上一轮用量（右）。
    /// <para>口径（主人 2026-09-22 00:1x）：「把最终结果**简短**放进输入框上面那一个**固定位置**」。
    /// 它**不参与正文区高度计算** ⇒ 输入变长、输入法合成都不会让它（或它上面的区域）动。</para>
    /// <para>截断纪律：这里是**信息型**内容（放不下就截 + <c>…</c>）；决定型内容仍只走右栏详细区。</para>
    /// </summary>
    private static string StripLine(SplitFrame frame, SplitLayout layout)
    {
        var contentWidth = Math.Max(1, layout.Width - 4);
        // **必须去标记**：会话正文里带角色标记（[[strong]]…[[/]]），直接放进来会当字面量漏在屏上
        // （PresentationTests 的「纯文本帧 == 富文本帧正文」闸门抓过一次）。
        var note = Markup.Strip(frame.LastNote).Replace('\n', ' ').Trim();
        var text = note.Length == 0 ? string.Empty : "ⓘ " + note;

        // v15（主人 2026-09-22 02:2x）：右侧那组 `tokens ↑x ↓y` **取消** —— 与顶部框「会话」行是同一回事。
        // 这一条从此只承载**提示**（信息型：放不下就截 + `…`，全文在 `/result`）。
        return "│ " + Pad(RichText.From(TerminalText.TruncateTo(text, contentWidth)), contentWidth).Text + " │";
    }

    /// <summary>中缝行（左列照旧带角色，右列被一条横线切开）。</summary>
    private static RichText Seam(RichText left, SplitLayout layout) =>
        RichText.From("│").Concat(Pad(left, layout.LeftWidth)).Append("├" + new string('─', layout.RightWidth) + "┤");

    /// <summary>帧 → 文件文本（LF、无 BOM；同一帧在任何机器上逐字节相同）。</summary>
    public static string RenderText(SplitFrame frame) =>
        string.Concat(Render(frame).Select(l => l + "\n"));

    /// <summary>对话流全部显示行（换行后，纯文本）；会话据此夹滚动范围。</summary>
    public static IReadOnlyList<string> ConversationLines(SplitFrame frame, SplitLayout layout) =>
        [.. ConversationRich(frame, layout).Select(static line => line.Text)];

    /// <summary>对话流全部显示行的**富文本**版（前缀/正文带角色；工具行与记账行都看得见）。</summary>
    public static IReadOnlyList<RichText> ConversationRich(SplitFrame frame, SplitLayout layout)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var lines = new List<RichText>();
        foreach (var entry in frame.Conversation)
        {
            // 用户问的那一句：**上下各空一行** + 整行**斜体**（主人 2026-09-22 定：要一眼瞄到自己在问什么）。
            var isYou = entry.Kind == "you";
            var (prefix, indent) = entry.Kind switch
            {
                // `you > ` 两边各带一个空格；正文**逐字显示**（用户输入不解析标记 —— 免得自己打的 `> ` / `**` 被当标记吃掉）。
                "you" => ("you > ", 6),
                "ai" => ("ai  ", 4),
                "turn" => ("     ", 5),
                "info" => ("· ", 2),
                // 「正在思考」那一行：前缀与 info 同形，但**整行按「得解」的角色上色**（见下）。
                "thinking" => ("· ", 2),
                // 生命周期卡（L2）：一次用户请求只有这**一条**条目 —— 它自己带缩进与角色，
                // 所以不前缀、不缩进（卡的正文由 LifecyclePresenter 排）。
                "lifecycle" => (string.Empty, 0),
                // 工具行：调用与结果**都看得见**（只读免批的动作也不能静默——
                // 否则「工具用了」与「什么都没发生」在屏上长得一样）。
                "tool" => ("→ ", 2),
                _ => ("    ", 4),
            };

            if (isYou)
            {
                lines.Add(RichText.Empty);
            }

            var body = entry.Kind switch
            {
                "turn" => RichText.From("↳ ").Concat(Rich(entry.Text)),
                "you" => RichText.From(entry.Text),
                _ => Rich(entry.Text),
            };
            var wrapped = body.Wrap(Math.Max(8, layout.LeftWidth - indent));
            // **整行一个角色的两类行**：用户问的那句（引语色）与「正在思考」行（**与「得解」同色** —— 主人 2026-09-22 17:2x 定：一眼认出）。
            var uniform = isYou ? StyleRole.You : entry.Kind == "thinking" ? StyleRole.Done : (StyleRole?)null;
            for (var i = 0; i < wrapped.Count; i++)
            {
                var row = RichText.From(i == 0 ? prefix : new string(' ', indent))
                    .Concat(wrapped[i])
                    .TruncateTo(layout.LeftWidth);
                lines.Add(uniform is { } role ? UniformRole(row, role) : row);
            }

            if (isYou)
            {
                lines.Add(RichText.Empty);
            }
        }

        return lines;
    }

    /// <summary>详细块的全部行（标题 + 目录 / 展开正文 / 面板输出）—— 纯文本。</summary>
    public static IReadOnlyList<string> DetailCells(SplitFrame frame, SplitLayout layout) =>
        [.. DetailCellsRich(frame, layout).Select(static line => line.Text)];

    /// <summary>详细块的全部行（**富文本**版）。</summary>
    public static IReadOnlyList<RichText> DetailCellsRich(SplitFrame frame, SplitLayout layout)
    {
        var rows = new List<RichText>
        {
            RichText.From(DetailHeader(frame, layout)).TruncateTo(layout.RightWidth),
        };

        var content = DetailContentRich(frame, layout);

        var body = Math.Max(0, layout.DetailRows - 1);
        if (body == 0 || content.Count == 0)
        {
            return rows;
        }

        // 详细块**从顶部开始看**（目录第一项 / 面板输出的表头 / 正文第一行都在眼前），往下滚才是滚。
        var start = Math.Clamp(frame.DetailScroll, 0, Math.Max(0, content.Count - body));
        for (var i = 0; i < body; i++)
        {
            var index = start + i;
            rows.Add(index < content.Count ? content[index].TruncateTo(layout.RightWidth) : RichText.Empty);
        }

        return rows;
    }

    /// <summary>
    /// 详细块的**可滚动内容**（纯文本）：展开时 = 该区正文；有面板输出时 = 面板行；否则 = 区目录（折叠态）。
    /// </summary>
    public static IReadOnlyList<string> DetailContentLines(SplitFrame frame, SplitLayout layout) =>
        [.. DetailContentRich(frame, layout).Select(static line => line.Text)];

    /// <summary>详细块可滚动内容的**富文本**版（面板行 / 区正文都过标记解析）。</summary>
    public static IReadOnlyList<RichText> DetailContentRich(SplitFrame frame, SplitLayout layout)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.DetailTitle is { Length: > 0 })
        {
            return [.. frame.DetailLines.Select(Rich)];
        }

        var menu = new List<RichText>(frame.Menu.Count);

        // v13-B：**展开的正文画在这里**（详细区），紧跟它自己那一行下面 ——
        // 「目录下面的一个一个模块」在详细区里就是展开态（状态区再不承载正文）。
        var bodies = InlineBodiesRich(frame, layout.RightWidth, out _);
        foreach (var (entry, index) in frame.Menu.Select(static (e, i) => (e, i)))
        {
            var selected = frame.Focus == PaneFocus.Right && index == frame.MenuSelection;
            var marker = selected ? "›" : " ";
            var expand = entry.Expanded ? "▾" : "▸";
            menu.Add(RichText.From($"{marker} {expand} {entry.Id} {entry.ShortTitle}（{entry.CountLabel}）").TruncateTo(layout.RightWidth));

            foreach (var body in bodies.Where(b => b.Region == entry.Region))
            {
                menu.Add(body.Text.TruncateTo(layout.RightWidth));
            }
        }

        return menu;
    }

    /// <summary>状态块行（右上固定：表头 + 六区 + 合计 + 上一轮）—— 纯文本。</summary>
    public static IReadOnlyList<string> StatusCells(SplitFrame frame, SplitLayout layout) =>
        [.. StatusCellsRich(frame, layout).Select(static line => line.Text)];

    /// <summary>状态块行的**富文本**版（状态表本体无色；就地展开的区正文带角色）。</summary>
    private static IReadOnlyList<RichText> StatusCellsRich(SplitFrame frame, SplitLayout layout)
    {
        var rows = new List<RichText>
        {
            RichText.From(StateHeader(frame, layout)).TruncateTo(layout.RightWidth),
        };

        var columns = layout.Columns;
        rows.Add(RichText.From(RegionHeader(columns, layout)));

        foreach (var region in frame.Regions)
        {
            rows.Add(RichText.From(RegionRow(region, columns, layout)));
        }

        if (layout.ShowTotalsRow)
        {
            var totals = $"合计 {TerminalText.Number(frame.Totals.RegionBytes)} B ≈ {TerminalText.Number(frame.Totals.ApproxTokens)} token";

            // ③ 仍不够时：省掉 token 估算（合计字节是主信息；行本身不截字）。
            if (TerminalText.WidthOf(totals) > layout.RightWidth)
            {
                totals = $"合计 {TerminalText.Number(frame.Totals.RegionBytes)} B";
            }

            rows.Add(RichText.From(TerminalText.WidthOf(totals) <= layout.RightWidth
                ? totals
                : TerminalText.TruncateTo(totals, layout.RightWidth)));
        }

        if (layout.ShowUsageRow)
        {
            rows.Add(RichText.From(LastTurnRow(frame, layout)));
        }

        return rows;
    }

    private static string StateHeader(SplitFrame frame, SplitLayout layout)
    {
        // 口径（主人 2026-09-22 02:0x）：右上这块说的是**当前这一整个 task / 生命周期**，不是「上一次模型调用」
        // ⇒ 写「本生命周期」（「轮」= 一次模型调用，是计算单位，只用在真的指模型调用处，如「上一轮 prompt」）。
        var header = frame.Focus == PaneFocus.Right ? $"▸ 状态 · 本生命周期" : $"  状态 · 本生命周期";

        // 请求/预览的字节与指纹：宽了给全，窄了给短形，再窄只给字节（右上角那一小块的溯源注记）。
        if (frame.RequestNote.Length > 0 && layout.RightWidth >= 46)
        {
            header += "  ·  " + frame.RequestNote;
        }
        else if (frame.Totals.RequestBytes > 0 && layout.RightWidth >= 34)
        {
            header += $" · {KindOf(frame)} {TerminalText.Number(frame.Totals.RequestBytes)}B/{frame.Totals.RequestFingerprint?[..8]}";
        }
        else if (frame.Totals.RequestBytes > 0 && layout.RightWidth >= 28)
        {
            header += $" · {KindOf(frame)} {TerminalText.Number(frame.Totals.RequestBytes)}B";
        }

        return header;
    }

    private static string KindOf(SplitFrame frame) =>
        frame.RequestNote.StartsWith("预览", StringComparison.Ordinal) ? "预览" : "请求";

    /// <summary>就地展开的正文总行数（每区：白板/草稿最多 2 行、焦点 1 行；放不下就短形/计数）。</summary>
    private static int InlineRowsFor(SplitFrame frame, int rightWidth) =>
        InlineBodiesRich(frame, rightWidth, out _).Count;

    /// <summary>
    /// **就地展开的正文行**（白板 / 草稿 / 焦点）：`▾ 白板 R4 solve: …` 这样的行，**紧跟它自己那一行渲染**。
    /// <para>三级降级与自报区同一口径（长形 → 短形 → 计数），**不截字、不出省略号**；正文过标记解析。</para>
    /// </summary>
    private static List<(StackRegion Region, RichText Text)> InlineBodiesRich(SplitFrame frame, int rightWidth, out int unused)
    {
        unused = 0;
        var rows = new List<(StackRegion, RichText)>();
        foreach (var region in frame.InlineExpanded)
        {
            var report = frame.SelfReport.FirstOrDefault(r => r.Region == region);
            if (report is null)
            {
                continue;
            }

            var perRegion = region == StackRegion.R4 || region == StackRegion.R5 ? 2 : 1;
            var placed = false;
            foreach (var candidate in new[] { report.Text, report.Short })
            {
                if (candidate.Length == 0)
                {
                    continue;
                }

                var wide = RichText.From("    ▾ ").Concat(Rich(candidate));
                if (wide.Width <= rightWidth)
                {
                    rows.Add((region, wide));
                    placed = true;
                    break;
                }

                var wrapped = wide.Wrap(rightWidth);
                if (wrapped.Count <= perRegion)
                {
                    foreach (var line in wrapped)
                    {
                        rows.Add((region, line));
                    }

                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                rows.Add((region, RichText.From($"    · {report.CountText}")));
            }
        }

        return rows;
    }

    /// <summary>
    /// 「自报区」的固定行：标题 + 白板 / 焦点 / 草稿各 1~2 行（每轮刷新）。
    /// <para>高窗口给两行、**折行显示正文**（折行不截字）；放不下就用**计数形式**。
    /// 两条都与状态表「窄了就换计数」同一条纪律，也与 <c>TuiSplitTests</c> 的
    /// 「右列不许出现省略号」不变量一致。矮窗口整块不画。</para>
    /// </summary>
    private static IReadOnlyList<RichText> SelfReportCells(SplitFrame frame, SplitLayout layout)
    {
        // ⚠️ 两处都要判：`ShowSelfReport` 是「该不该有」，`SelfReportRows` 是「**放得下几行**」——
        // 后者会给详细块留出 DetailMinRows（否则中等窗口会把区目录整块挤没，实测踩到）。
        if (!layout.ShowSelfReport || layout.SelfReportRows <= 0 || frame.SelfReport.Count == 0)
        {
            return [];
        }

        // 每区几行：高窗口人人两行；否则**白板 R4 优先两行**（它是决定型内容：当前解 / 求解步），
        // 前提是预算给得出（`SelfReportRows` 已经先给详细块留够）。
        static int PerRegion(SplitLayout layout, StackRegion region) =>
            layout.TallSelfReport || (region == StackRegion.R4 && layout.SelfReportRows >= 5) ? 2 : 1;
        var marker = frame.Focus == PaneFocus.Right ? "▸ " : "  ";
        var header = marker + SelfReportHeader;
        var rows = new List<RichText>(SelfReportRowCountTall)
        {
            RichText.From(TerminalText.WidthOf(header) <= layout.RightWidth ? header : marker + SelfReportHeaderShort),
        };

        foreach (var region in frame.SelfReport.Take(3))
        {
            var perRegion = PerRegion(layout, region.Region);
            // **三级降级**：长形（现在的求解口径全文）→ 短形（首行 / 求解行）→ 计数。
            // 每一级都**先量再放**：放得下就用，放不下换下一级 —— 任意宽度下都不截字、不出省略号。
            var placed = false;
            foreach (var candidate in new[] { region.Text, region.Short })
            {
                if (candidate.Length == 0)
                {
                    continue;
                }

                var wide = RichText.From($"{region.Label} ").Concat(Rich(candidate));
                if (wide.Width <= layout.RightWidth)
                {
                    rows.Add(wide);
                    placed = true;
                    break;
                }

                var wrapped = wide.Wrap(layout.RightWidth);
                if (wrapped.Count <= perRegion)
                {
                    rows.AddRange(wrapped);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                var countForm = $"{region.Label} · {region.CountText}";
                rows.Add(RichText.From(TerminalText.WidthOf(countForm) <= layout.RightWidth
                    ? countForm
                    : region.Label));
            }

            if (perRegion == 1)
            {
                continue;   // 一行制：上面已经恰好加了一行
            }

            while (rows.Count % perRegion != 1)
            {
                rows.Add(RichText.Empty);   // 补齐这一区的行位（布局行数固定）
            }
        }

        // 按预算裁（预算不足时**先裁掉后面的区**，标题行优先保留）。
        return rows.Take(Math.Max(1, layout.SelfReportRows)).ToArray();
    }

    private static string DetailHeader(SplitFrame frame, SplitLayout layout)
    {
        if (frame.DetailTitle is { Length: > 0 } title)
        {
            return $"详细 · {title}";
        }

        return layout.RightWidth >= 34
            ? "详细 · 区目录（↑↓ 选项，Enter 展开正文）"
            : "详细 · 区目录（↑↓ Enter）";
    }

    private static string RegionHeader(RegionColumns columns, SplitLayout layout)
    {
        var text = string.Join(' ',
            TerminalText.PadLeftTo("次序", columns.OrderWidth),
            TerminalText.PadTo("区", columns.IdWidth),
            TerminalText.PadLeftTo("字节", columns.BytesWidth),
            TerminalText.PadLeftTo("Δ周期", columns.DeltaWidth));

        // 可选列：与数据行同一套「放得下才上」的规则（表头与数据行永不错位）。
        if (columns.FingerprintChars > 0
            && TerminalText.WidthOf(text) + 1 + columns.FingerprintChars <= layout.RightWidth)
        {
            text += " " + TerminalText.PadLeftTo("指纹", columns.FingerprintChars);
        }

        if (columns.ShowVersion && TerminalText.WidthOf(text) + 1 + 2 <= layout.RightWidth)
        {
            text += " 版本";
        }

        return text;
    }

    private static string RegionRow(SplitRegionRow row, RegionColumns columns, SplitLayout layout)
    {
        var text = string.Join(' ',
            TerminalText.PadLeftTo(row.Order.ToString(), columns.OrderWidth),
            TerminalText.PadTo(row.Id, columns.IdWidth),
            TerminalText.PadLeftTo(TerminalText.Number(row.Bytes), columns.BytesWidth),
            TerminalText.PadLeftTo(TerminalText.Delta(row.Delta), columns.DeltaWidth));

        if (columns.FingerprintChars > 0
            && row.Fingerprint.Length >= columns.FingerprintChars
            && TerminalText.WidthOf(text) + 1 + columns.FingerprintChars <= layout.RightWidth)
        {
            text += " " + row.Fingerprint[..columns.FingerprintChars];
        }

        if (columns.ShowVersion
            && row.VersionTag.Length > 0
            && TerminalText.WidthOf(text) + 1 + TerminalText.WidthOf(row.VersionTag) <= layout.RightWidth)
        {
            text += " " + row.VersionTag;
        }

        // **白板 / 草稿的首行摘要**（需求 B）：放得下才加，放不下就不加（不截字）。
        if (row.Note.Length > 0 && TerminalText.WidthOf(text) + 3 + TerminalText.WidthOf(row.Note) <= layout.RightWidth)
        {
            text += " · " + row.Note;
        }

        // 必需四列在合法尺寸下必定放得下；这里只做最后一道防守（真放不下时不静默错位）。
        return TerminalText.TruncateTo(text, layout.RightWidth);
    }

    private static string LastTurnRow(SplitFrame frame, SplitLayout layout)
    {
        if (frame.LastTurn is not { } turn)
        {
            return TerminalText.TruncateTo("上一轮：还没跑过", layout.RightWidth);
        }

        // 窄了就换成示意里的紧凑写法（2835/2560/90.9% 12.6ms）。
        var text = layout.RightWidth >= 40
            ? $"上一轮 prompt {TerminalText.Number(turn.PromptTokens)} · cached {TerminalText.Number(turn.CachedTokens)} ({TerminalText.Percent(turn.HitRate)}) · 未命中 {TerminalText.Number(turn.UncachedTokens)} · 开销 {turn.RuntimeOverheadMs:F1}ms"
            : $"上一轮 {turn.PromptTokens}/{turn.CachedTokens} {TerminalText.Percent(turn.HitRate)} {turn.RuntimeOverheadMs:F1}ms";

        return TerminalText.TruncateTo(text, layout.RightWidth);
    }

    /// <summary>
    /// **顶部信息框**（v15 · 单栏）：把原先右栏的三块（区栈 / 自报 / 详细）压成**一行一行**的「标签 | 值 | 值」
    /// —— 主人 2026-09-22 02:2x 定：不用表格、分隔符只用 `|`。
    /// <para>区栈一律用 **≈token**（`字节 ÷ 4`，项目既定口径）；**变过的区**才带 `Δ+n`。
    /// 用量写「本 task +N tok | 缓存命中 M tok」。</para>
    /// <para>降级按 <see cref="SplitLayout.TopBandContentRows"/>；一行放不下就**整段从尾部丢**（宁可少说，不截字）。</para>
    /// </summary>
    private static IReadOnlyList<RichText> TopBand(SplitFrame frame, SplitLayout layout)
    {
        var inner = frame.Width - 2;
        var tier = layout.TopBandContentRows;
        var content = new List<string>
        {
            BandLine(inner, SessionSegments(frame)),
            BandLine(inner, StackSegments(frame, compact: tier < 6)),
        };

        if (tier >= 6)
        {
            // 主人 2026-09-22 令：**区栈与白板之间**加一行「专家」——R1 实际装载的专家知识分类。
            content.Add(BandLine(inner, ExpertSegments(frame)));
            content.Add(BandLine(inner, ReportSegments(frame, StackRegion.R4, "白板", withText: true)));
            content.Add(BandLine(inner, ReportSegments(frame, StackRegion.R5, "草稿", withText: true)));
            content.Add(BandLine(inner, ReportSegments(frame, StackRegion.R3, "焦点", withText: true)));
        }
        else if (tier >= 3)
        {
            content.Add(BandLine(inner, ReportCountSegments(frame)));
        }

        return
        [
            RichText.From("┌" + Fill($"─ {frame.Title} ", inner) + "┐"),
            .. content.Select(t => RichText.From("│").Concat(Pad(Markup.Parse(BandText(t, inner)), inner)).Append("│")),
            RichText.From("├" + new string('─', inner) + "┤"),
        ];
    }

    /// <summary>「会话」行：协议版本 · 模块数 · task 读秒 · 本 task 累计 tok · 缓存命中 tok（**指纹/字节不再显示**）。</summary>
    private static IReadOnlyList<string> SessionSegments(SplitFrame frame)
    {
        var segs = new List<string> { "[[strong]]会话[[/]]", $"protocol v{frame.ProtocolVersion}" };
        if (frame.ModuleCount > 0)
        {
            segs.Add($"{frame.ModuleCount} 模块");
        }

        if (frame.TaskLabel.Length > 0)
        {
            segs.Add(frame.TaskLabel);
        }

        if (frame.TaskUsage is { } usage)
        {
            segs.Add($"本 task 新增 {TerminalText.Number(usage.AppendedTokens)} tok");
            segs.Add($"缓存命中 {TerminalText.Percent(usage.HitRate)}");
        }
        else
        {
            segs.Add("本 task 新增 — tok");
            segs.Add("缓存命中 —");
        }

        return segs;
    }

    /// <summary>「区栈」行：每个模块的 **≈token** 占用（= 字节 ÷ 4）；变过的区才带 `Δ+n`；末尾合计。</summary>
    private static IReadOnlyList<string> StackSegments(SplitFrame frame, bool compact)
    {
        var segs = new List<string> { "[[strong]]区栈[[/]]" };
        var total = $"合计 {TerminalText.Number(frame.Totals.RegionBytes / 4)} ≈token";

        if (compact)
        {
            segs.Add(total);
            foreach (var row in frame.Regions.Where(static r => r.Delta != 0))
            {
                segs.Add($"{row.Id} {TerminalText.Number(row.Bytes / 4)} Δ{row.Delta:+#;-#;0}");
            }

            return segs;
        }

        foreach (var row in frame.Regions)
        {
            var token = TerminalText.Number(row.Bytes / 4);
            segs.Add(row.Delta == 0 ? $"{row.Id} {token}" : $"{row.Id} {token} Δ{row.Delta:+#;-#;0}");
        }

        segs.Add(total);
        return segs;
    }

    /// <summary>
    /// **「专家」行**：R1 里**实际装载**的专家知识分类（主人 2026-09-22 令）。
    /// <para>各分类以**整行同一分隔符 ` | `** 隔开（与其它行同规：放不下就**从尾部整段丢**，绝不截字）。
    /// 一个都没装 ⇒ 如实写「（未装载）」，**不写预设清单**（屏上不许说装上了没装的东西）。</para>
    /// </summary>
    private static IReadOnlyList<string> ExpertSegments(SplitFrame frame)
    {
        var segs = new List<string> { "[[strong]]专家[[/]]" };
        if (frame.ExpertDomains.Count == 0)
        {
            segs.Add("（未装载）");
            return segs;
        }

        // 每类带**自己的占用**（≈token，与区栈同口径）；次序由宿主给（占用降序）。
        // 标签用**屏面短名**（主人 2026-09-22 定：id 不动、只换显示名 ⇒ 看屏就懂「流程 / 凭据」是什么）。
        foreach (var domain in frame.ExpertDomains)
        {
            segs.Add($"{domain.Label} ≈{TerminalText.Number(domain.ApproxTokens)}");
        }

        return segs;
    }

    /// <summary>自报块的正文行（白板 solve / step 首两行；信息型 ⇒ 超宽允许截，全文走 `/tail`·`/draft`·`/focus`）。</summary>
    private static IReadOnlyList<string> ReportSegments(SplitFrame frame, StackRegion region, string name, bool withText)
    {
        var item = frame.SelfReport.FirstOrDefault(s => s.Region == region);
        var segs = new List<string> { $"[[strong]]{name}[[/]]" };
        if (item is null)
        {
            segs.Add("—");
            return segs;
        }

        if (!withText)
        {
            segs.Add(item.CountText);
            return segs;
        }

        var lines = TerminalText.NormalizeNewlines(item.Text).Split('\n')
            .Select(static l => l.TrimStart('-', ' ').Trim())
            .Where(static l => l.Length > 0)
            .Take(2);
        foreach (var line in lines)
        {
            segs.Add(line);
        }

        if (segs.Count == 1)
        {
            segs.Add(item.CountText);
        }

        return segs;
    }

    /// <summary>自报三块的计数（中档：压成一行）。</summary>
    private static IReadOnlyList<string> ReportCountSegments(SplitFrame frame)
    {
        var segs = new List<string> { "[[strong]]自报[[/]]" };
        foreach (var (region, name) in new[] { (StackRegion.R4, "白板"), (StackRegion.R5, "草稿"), (StackRegion.R3, "焦点") })
        {
            var item = frame.SelfReport.FirstOrDefault(s => s.Region == region);
            segs.Add(item is null ? $"{name} —" : $"{name} {item.CountText}");
        }

        return segs;
    }

    /// <summary>按 ` | ` 拼一行；放不下就**从尾部整段丢**（宁可少说，不截字）。</summary>
    private static string BandLine(int inner, IReadOnlyList<string> segments)
    {
        var keep = segments.ToList();
        while (keep.Count > 1 && TerminalText.WidthOf(Markup.Strip(string.Join(" | ", keep))) > inner - 1)
        {
            keep.RemoveAt(keep.Count - 1);
        }

        return " " + string.Join(" | ", keep);
    }

    /// <summary>只超一点点（只剩一段仍超宽）⇒ 截 + `…`（信息型内容；决定型内容不走这里）。</summary>
    private static string BandText(string text, int inner) =>
        TerminalText.WidthOf(Markup.Strip(text)) <= inner ? text : TerminalText.TruncateTo(text, inner);

    private static string TopBorder(SplitFrame frame, SplitLayout layout)
    {
        // 顶边框也要有左右分缝（┬ 落在列分界上）：一眼看得出上面是两栏。
        var left = $"─ {frame.Title} ";
        var leftPart = TerminalText.WidthOf(left) <= layout.LeftWidth
            ? Fill(left, layout.LeftWidth)
            : TerminalText.TruncateTo(left, layout.LeftWidth);

        var right = $" {frame.Subtitle} ─";
        var rightPart = TerminalText.WidthOf(right) <= layout.RightWidth
            ? right + new string('─', layout.RightWidth - TerminalText.WidthOf(right))
            : TerminalText.TruncateTo(right, layout.RightWidth);

        return "┌" + leftPart + "┬" + rightPart + "┐";
    }

    private static RichText BodyRow(RichText left, RichText right, SplitLayout layout) =>
        RichText.From("│").Concat(Pad(left, layout.LeftWidth)).Append("│").Concat(Pad(right, layout.RightWidth)).Append("│");

    /// <summary>行高**收窄**的防抖窗口（**唯一声明处**；主人 2026-09-20 定 400ms）。
    /// <para>v13 修 BUG 后它**只管收窄**：长高是立刻的（内容一变就长），收窄要等停稳。</para>
    /// </summary>
    public const int InputSettleMs = 400;

    /// <summary>
    /// 输入区占的行数（**单一口径，v13**）：按显示宽度**软换行**，上限 <see cref="SplitLayout.MaxInputRows"/>。
    /// <para>⚠️ **不再有「键入中 / 停稳」两段口径**（那是 2026-09-20 的防抖土方）：
    /// 它换来的是「要等几百毫秒才长高」＋「再敲一个字又跳回一行」两个真机 BUG（主人 2026-09-21 报）。
    /// 现在行高**只跟着内容走**；「长高即时 / 收窄防抖」由宿主 <c>SplitSession.NextInputRows</c> 负责。</para>
    /// </summary>
    /// <param name="input">输入缓冲原文。</param>
    /// <param name="width">面板宽度。</param>
    public static int InputRowsFor(string input, int width)
    {
        if (input.Length == 0)
        {
            return 1;
        }

        var explicitRows = 1 + input.Count(c => c == '\n');
        var textWidth = TextWidthFor(width);
        var wrapped = 0;
        foreach (var line in input.Split('\n'))
        {
            wrapped += InputSegments(line, textWidth).Count;
        }

        return Math.Clamp(Math.Max(explicitRows, wrapped), 1, SplitLayout.MaxInputRows);
    }

    /// <summary>
    /// 把一个**逻辑行**按可用列宽切成显示段（**行高与渲染共用同一把尺子**，不允许两处各算一套）。
    /// <para>空行也返回 1 段（空串）—— 所以段数 = 这个逻辑行占的显示行数。</para>
    /// </summary>
    public static IReadOnlyList<string> InputSegments(string line, int textWidth)
    {
        var width = Math.Max(1, textWidth);
        var total = TerminalText.WidthOf(line);
        var count = Math.Max(1, (int)Math.Ceiling(total / (double)width));

        var segments = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            segments.Add(TerminalText.SliceColumns(line, i * width, width));
        }

        return segments;
    }

    /// <summary>输入区正文可用列宽（去掉边框与提示符）—— **行高与渲染共用同一口径**，不允许两处各算一套。</summary>
    private static int TextWidthFor(int width) => Math.Max(1, Math.Max(1, width - 4) - PromptWidth);

    /// <summary>输入提示符（唯一声明处：换行后的续行按它对齐）。</summary>
    private const string Prompt = "agent-runtime > ";

    /// <summary>自绘光标标记（**唯一声明处**：画它的人与找它的人必须是同一个字符串）。</summary>
    private const string CaretGlyph = "_";

    private static int PromptWidth => TerminalText.WidthOf(Prompt);

    /// <summary>
    /// 输入区（**停稳后自动换行**；键入中不换行、不抖 —— 需求 C + 2026-09-21 防抖补完）。
    /// <para>与旧的「一行内左右平移」的区别：停稳后是**按显示宽度软换行**（整条指令看得见，面板高度随行数增长）；
    /// 键入中的那几百毫秒仍按逻辑行 + 行内滚动画（中文输入法候选串不会把行高抖上去）。</para>
    /// </summary>
    private static IReadOnlyList<string> InputRows(SplitFrame frame, SplitLayout layout)
    {
        var caret = Math.Clamp(frame.Caret, 0, frame.Input.Length);
        var marker = frame.Focus == PaneFocus.Input ? string.Empty : "（右栏焦点）";
        var contentWidth = Math.Max(1, layout.Width - 4);
        var textWidth = TextWidthFor(layout.Width);

        // 光标列（按显示宽度算；含 "_" 占 1 列）→ 决定光标落在第几段。
        var caretCol = TerminalText.WidthOf(frame.Input[..caret]);

        // **逻辑行 = 按显式换行切**；每个逻辑行再按显示宽度**软换行**（v13 单一口径：
        //   行高与渲染共用同一把尺子；不再有「停稳才换行」那条分支）。
        var logical = frame.Input.Length == 0 ? [string.Empty] : frame.Input.Split('\n');

        var rows = new List<string>(logical.Length);
        var consumed = 0;
        for (var i = 0; i < logical.Length; i++)
        {
            var text = logical[i];
            var head = i == 0 ? Prompt : new string(' ', PromptWidth);
            var tail = i == logical.Length - 1 ? marker : string.Empty;
            var lineWidth = TerminalText.WidthOf(text);
            var caretInLine = caretCol - consumed;

            if (lineWidth > textWidth)
            {
                var segments = InputSegments(text, textWidth);
                for (var s = 0; s < segments.Count; s++)
                {
                    var seg = segments[s];
                    var segWidth = TerminalText.WidthOf(seg);
                    var segCaret = caretInLine - (s * textWidth);

                    if (segCaret >= 0 && segCaret <= segWidth)
                    {
                        seg = TerminalText.SliceColumns(seg, 0, segCaret)
                              + CaretGlyph
                              + TerminalText.SliceColumns(seg, segCaret, Math.Max(0, segWidth - segCaret));
                    }

                    rows.Add((s == 0 ? head : new string(' ', PromptWidth)) + seg + (s == segments.Count - 1 ? tail : string.Empty));
                }

                consumed += lineWidth;
                continue;
            }

            // 行内水平滚动：光标（含 "_"）始终可见，旧字从左边挤出视野。
            if (lineWidth > textWidth)
            {
                var startCol = Math.Max(0, caretInLine + 1 - textWidth);
                text = TerminalText.SliceColumns(text, startCol, textWidth);
                caretInLine -= startCol;
                lineWidth = TerminalText.WidthOf(text);
            }

            if (caretInLine >= 0 && caretInLine <= lineWidth)
            {
                var offset = Math.Max(0, caretInLine);
                text = TerminalText.SliceColumns(text, 0, offset)
                       + CaretGlyph
                       + TerminalText.SliceColumns(text, offset, Math.Max(0, TerminalText.WidthOf(text) - offset));
            }

            consumed += TerminalText.WidthOf(logical[i]) + 0;   // 逻辑行内不跨行
            rows.Add(head + text + tail);
        }

        while (rows.Count < layout.InputRows)
        {
            rows.Add(new string(' ', PromptWidth));
        }

        // 超过上限 ⇒ 显示**最后**几行（光标在末尾，必须看得见）。
        var shown = rows.Count > layout.InputRows ? rows.Skip(rows.Count - layout.InputRows) : rows;
        return shown.Select(r => "│ " + Pad(RichText.From(r), contentWidth).Text + " │").ToArray();
    }

    private static string HeaderCell(string header, bool focused, int width) =>
        TerminalText.TruncateTo(focused ? $" {header}  [焦点]" : $" {header}", width);

    private static string Column(string text, int width, bool left = false) =>
        (left ? TerminalText.PadTo(text, width) : TerminalText.PadLeftTo(text, width)) + " ";

    private static RichText Pad(RichText text, int width) =>
        text.TruncateTo(width).PadTo(width);

    private static string Fill(string text, int width)
    {
        var progress = TerminalText.WidthOf(text);
        return progress >= width ? TerminalText.TruncateTo(text, width) : text + new string('─', width - progress);
    }

    private static (int Start, int Count) Window(int lineCount, int rows, int scroll)
    {
        if (rows <= 0 || lineCount <= rows)
        {
            return (0, Math.Min(lineCount, Math.Max(rows, 0)));
        }

        var maxStart = lineCount - rows;

        // scroll = 0 ⇒ 贴底（最新在眼前）；scroll = maxStart ⇒ 顶到头。
        var start = Math.Clamp(maxStart - Math.Max(0, scroll), 0, maxStart);
        return (start, rows);
    }

    private static int LineCount(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Length;

    /// <summary>「版本 / 条数」列：协议/冷冻给段账本版本（r/k/m），事件流给条数，其余 ——。</summary>
    private static string VersionTagOf(StackLayer layer)
    {
        switch (layer.Region)
        {
            case StackRegion.R2:
                return $"{layer.MessageCount} 条";

            case StackRegion.R1P:
                return layer.Segments.Count == 0 ? "—" : $"v{layer.Segments[0].Version}";

            case StackRegion.R1:
            {
                var parts = new List<string>();
                foreach (var segment in layer.Segments)
                {
                    var prefix = segment.Id.StartsWith("rules", StringComparison.Ordinal) ? "r"
                        : segment.Id.StartsWith("knowledge", StringComparison.Ordinal) ? "k"
                        : segment.Id.StartsWith("memoryindex", StringComparison.Ordinal) ? "m"
                        : segment.Id;
                    parts.Add(prefix + segment.Version);
                }

                return parts.Count == 0 ? "—" : string.Join('/', parts);
            }

            default:
                return "—";
        }
    }

    private static IReadOnlyList<string> Slice(this (int Start, int Count) window, IReadOnlyList<string> source, int width)
    {
        var rows = new List<string>(window.Count);
        for (var i = 0; i < window.Count; i++)
        {
            var index = window.Start + i;
            rows.Add(index < source.Count ? TerminalText.TruncateTo(source[index], width) : string.Empty);
        }

        return rows;
    }
}

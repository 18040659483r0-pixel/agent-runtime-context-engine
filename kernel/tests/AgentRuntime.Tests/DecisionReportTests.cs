using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告解析**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §六/§七 · P1）—— 守四件事：
/// <list type="number">
/// <item><b>形状 = 契约</b>：键白名单、必填、条数上限、块界与既有块头清单**同源**；</item>
/// <item><b>fail-closed 但不吓人</b>：必填缺失 ⇒ **整块不采纳**（宿主换成「未提交决策报告」，**不拿原料顶上**）；</item>
/// <item><b>认不出就不编</b>（`§十·64`）：不认识的键丢掉并**记注记**，认不出的行当**续行**（折行不是丢内容）；</item>
/// <item><b>解析是纯函数</b>：不查流、不看钟 —— 锚只**提取**，**存在性由宿主校验**（不存在即丢）。</item>
/// </list>
/// </summary>
public sealed class DecisionReportTests
{
    // ---------------- 「成品正文」（坑 #154 / §十·61）----------------

    [Fact]
    public void body_逐字多行_不折叠不截断()
    {
        // 成品 = 交付给人阅读的内容本身（题面 / 文案 / 方案正文）⇒ **逐字**进来，
        // 既不 Collapse（空格保留）也不丢缩进。判据：原文里的多空格与缩进必须原样取回。
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 出一题给人看",
            "step 1: 定层与场景",
            "outcome: 题成型",
            "body:",
            "  题：分手三个月。她半夜找你。",
            "",
            "  A.  等。她半夜找我，说明心里有我。",
            "  B.  她多久找我一次，我就多久理她一次。",
            "next rec: 先给判断"));

        Assert.True(parsed.Accepted);
        Assert.Equal(4, parsed.Report!.Body.Count);   // 题 / 空行（段落分隔）/ A / B
        Assert.Equal("  题：分手三个月。她半夜找你。", parsed.Report.Body[0]);   // 缩进与原文逐字
        Assert.Equal(string.Empty, parsed.Report.Body[1]);                      // 空行也是内容（段落分隔）
        Assert.Equal("  A.  等。她半夜找我，说明心里有我。", parsed.Report.Body[2]); // 双空格不被折叠
    }

    [Fact]
    public void body_缺省为空_不影响报告成立()
    {
        // 负向口径：body 是**可选**的 —— 没有它，报告照样成立（老报告一字不改仍有效）。
        var parsed = DecisionReportParser.Parse(Good());

        Assert.True(parsed.Accepted);
        Assert.Empty(parsed.Report!.Body);
    }

    // ---------------- 夹具 ----------------

    private static string Text(params string[] lines) => string.Join('\n', lines);

    /// <summary>一份**合法**报告（两条解步 / 一条发现 / 两条建议，带两个锚）。</summary>
    private static string Good() => Text(
        "[REPORT]",
        "need: 把这轮的部署代号在仓库里统一",
        "step 1: 扫完全仓，定位 3 处代号 (E004)",
        "step 2: 目标文件被锁 ⇒ 改道 write (E011)",
        "outcome: 三处代号已统一；未动其它文件",
        "found failed: edit 被锁（已改道） (E011)",
        "next rec: 我接着扫剩下的 2 处",
        "next: 到此为止，本任务结案");

    // ---------------- 正例 ----------------

    [Fact]
    public void 合法报告_字段与锚与指纹()
    {
        var parsed = DecisionReportParser.Parse(Good());

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Empty(parsed.Notes);

        var report = parsed.Report!;
        Assert.Equal("把这轮的部署代号在仓库里统一", report.Need);
        Assert.Equal("三处代号已统一；未动其它文件", report.Outcome);

        Assert.Equal(2, report.Steps.Count);
        Assert.Equal(1, report.Steps[0].Index);
        Assert.Equal("扫完全仓，定位 3 处代号", report.Steps[0].What);   // 锚从正文里摘走
        Assert.Equal("E004", report.Steps[0].Evidence);
        Assert.Equal(2, report.Steps[1].Index);
        Assert.Equal("E011", report.Steps[1].Evidence);

        var found = Assert.Single(report.Found);
        Assert.Equal("failed", found.Kind);
        Assert.Equal("edit 被锁（已改道）", found.What);
        Assert.Equal("E011", found.Evidence);

        Assert.Equal(2, report.Next.Count);
        Assert.True(report.Next[0].Recommended);        // next rec: = 它推荐的那一条
        Assert.False(report.Next[1].Recommended);
        Assert.Equal(1, report.Next[0].Index);
        Assert.Equal(2, report.Next[1].Index);

        Assert.Equal(16, report.Fingerprint.Length);
        Assert.Equal(report.Fingerprint.ToLowerInvariant(), report.Fingerprint);   // 全小写十六进制
    }

    [Fact]
    public void 指纹_同文本稳定_改一字即变()
    {
        var a = DecisionReportParser.Parse(Good()).Report!.Fingerprint;
        var b = DecisionReportParser.Parse(Good()).Report!.Fingerprint;
        Assert.Equal(a, b);

        var changed = DecisionReportParser.Parse(Good().Replace("三处代号", "四处代号", StringComparison.Ordinal));
        Assert.NotEqual(a, changed.Report!.Fingerprint);
    }

    // ---------------- 负例（fail-closed） ----------------

    [Fact]
    public void 没交块_如实说不交()
    {
        var parsed = DecisionReportParser.Parse(Text("我先看了一圈。", "[DONE] 做完了"));

        Assert.False(parsed.Accepted);
        Assert.Null(parsed.Report);
        Assert.Contains(ProtocolText.ReportPrefix, parsed.RejectReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 野标签不算块_必须整词_REPORTX_不是报告()
    {
        var parsed = DecisionReportParser.Parse(Text("[REPORTX]", "need: 甲", "step 1: 乙", "outcome: 丙"));

        Assert.False(parsed.Accepted);
        Assert.Contains(ProtocolText.ReportPrefix, parsed.RejectReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺必填_need_step_outcome_各自拒绝()
    {
        var noNeed = DecisionReportParser.Parse(Text("[REPORT]", "step 1: 乙", "outcome: 丙"));
        Assert.False(noNeed.Accepted);
        Assert.Contains("need", noNeed.RejectReason, StringComparison.Ordinal);

        var noStep = DecisionReportParser.Parse(Text("[REPORT]", "need: 甲", "outcome: 丙"));
        Assert.False(noStep.Accepted);
        Assert.Contains("step", noStep.RejectReason, StringComparison.Ordinal);

        var noOutcome = DecisionReportParser.Parse(Text("[REPORT]", "need: 甲", "step 1: 乙"));
        Assert.False(noOutcome.Accepted);
        Assert.Contains("outcome", noOutcome.RejectReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 空块与全垃圾_都不采纳()
    {
        Assert.False(DecisionReportParser.Parse("[REPORT]").Accepted);

        var junk = DecisionReportParser.Parse(Text("[REPORT]", "我扫了一圈", "然后写了点东西"));
        Assert.False(junk.Accepted);
    }

    // ---------------- 容错（认不出就不编，但别丢内容） ----------------

    [Fact]
    public void 未知键丢掉并记注记_折行并进上一条()
    {
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 1: 乙",
            "outcome: 丙",
            "  还有一行说明",          // 折行 ⇒ 并进 outcome
            "foo: 丁",                 // 不认识的键 ⇒ 丢掉 + 注记
            "next rec: 戊"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Contains("还有一行说明", parsed.Report!.Outcome, StringComparison.Ordinal);
        Assert.Contains(parsed.Notes, note => note.Contains("foo", StringComparison.Ordinal));
    }

    [Fact]
    public void 步编号认不出_退化为出现次序_不编内容()
    {
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 三: 乙",             // 编号认不出 ⇒ 用出现次序（排序是事实，不是编造）
            "step: 丙",
            "outcome: 丁"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal([1, 2], parsed.Report!.Steps.Select(static s => s.Index).ToArray());
        Assert.Equal("乙", parsed.Report.Steps[0].What);
    }

    [Fact]
    public void 超上限_截断并记注记_不拒绝()
    {
        var lines = new List<string> { "[REPORT]", "need: 甲" };
        for (var i = 1; i <= 8; i++)
        {
            lines.Add($"step {i}: 第 {i} 步");
        }

        for (var i = 1; i <= 5; i++)
        {
            lines.Add($"found noticed: 发现 {i}");
        }

        for (var i = 1; i <= 5; i++)
        {
            lines.Add($"next: 建议 {i}");
        }

        lines.Add("outcome: 丙");

        var parsed = DecisionReportParser.Parse(Text([.. lines]));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal(ProtocolText.ReportMaxSteps, parsed.Report!.Steps.Count);
        Assert.Equal(ProtocolText.ReportMaxFound, parsed.Report.Found.Count);
        Assert.Equal(ProtocolText.ReportMaxNext, parsed.Report.Next.Count);
        Assert.True(parsed.Notes.Count >= 3, string.Join(" | ", parsed.Notes));
    }

    [Fact]
    public void 块界_遇到下一个块头即停_不吞邻块()
    {
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 1: 乙",
            "outcome: 丙",
            "[TAIL]",
            "todos: 与报告无关的白板行"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal("丙", parsed.Report!.Outcome);
        Assert.DoesNotContain("todos", parsed.Report.Outcome, StringComparison.Ordinal);
        Assert.Empty(parsed.Notes);
    }

    [Fact]
    public void 取最后一个块_回复末尾才是报告()
    {
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 旧的那份",
            "step 1: 旧",
            "outcome: 旧",
            "",
            "我重新整理了一下：",
            "[REPORT]",
            "need: 新的那份",
            "step 1: 新",
            "outcome: 新"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal("新的那份", parsed.Report!.Need);
    }

    [Fact]
    public void 窗口容得下各段上限之和_报告与邻块都在()
    {
        // G9（P2）：报告块（最长 30 行）上线后，「白板 12 + 草稿 16 + 报告 30 + 工具 4 = 62」已占满旧的 64 行窗口
        // ⇒ **零余量**（多一行就把最外那一块压出去 ⇒ 静默不装载，2026-09-16 先例）。本测钉住：满额三段同现时各块都还在。
        var lines = new List<string> { "[TAIL]" };
        for (var i = 1; i <= ProtocolText.TailMaxLines; i++)
        {
            lines.Add($"todos: 白板第 {i} 行");
        }

        lines.Add("[DRAFT]");
        for (var i = 1; i <= ProtocolText.DraftMaxLines; i++)
        {
            lines.Add($"- 草稿第 {i} 条");
        }

        lines.Add("[REPORT]");
        lines.Add("need: 甲");
        lines.Add("step 1: 乙");
        lines.Add("outcome: 丙");
        lines.AddRange(Enumerable.Range(1, ProtocolText.ReportMaxLines).Select(static i => $"found noticed: 发现 {i}"));

        var reply = string.Join('\n', lines);
        var capSum = ProtocolText.ReportMaxLines + ProtocolText.TailMaxLines
            + ProtocolText.DraftMaxLines + ProtocolText.ToolMaxCallsPerReply;
        Assert.True(reply.Split('\n').Length > capSum, "本测要一个「各段都满额」的回复（长过各段上限之和）");
        Assert.True(ProtocolText.ReportScanLines > capSum, "窗口必须**有余量** —— 否则最外那一块会被静默丢掉");

        Assert.True(DecisionReportParser.Parse(reply).Accepted);                       // 报告没被压出窗口
        Assert.True(AgentRuntime.Core.Tail.CurrentTailService.TryParseReport(reply, out _));   // 白板仍在
        Assert.True(AgentRuntime.Core.Draft.DraftService.TryParseReport(reply, out _));        // 草稿仍在
    }

    [Fact]
    public void 真机回复原样回放_带方括号的限定词也认()
    {
        // 夹具来源：**真机取样**（2026-09-24 19:5x，`benchmark/runs/20260924-1950-report-compliance/`，协议 v21 完整版措辞）。
        // 三例里模型两种写法都出现过：`found [noticed]:` / `found noticed:`与 `next [rec]:` / `next rec:` ⇒ 两种都得认。
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 读 README.md 前 3 行，用一句话给出结论并以 [DONE] 收尾。",
            "step 1: 按上限 3 行读取 README.md，得到标题、版本号与封版文档指向 (E003)。",
            "step 2: 汇总为一句话结论并收尾。",
            "outcome: README 开头表明这是 Agent Runtime，内核版本 0.1.0 (E003)。",
            "found [failed]: README 共 298 行，本次刻意只读前 3 行 (E003)。",
            "next [rec]: 想确认封版事实，读 docs/FREEZE-0.1.0.md（rec）(E003)。",
            "next: 想继续读 README，从 offset=4 续读。"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        var report = parsed.Report!;
        Assert.Equal(2, report.Steps.Count);
        Assert.Equal("E003", report.Steps[0].Evidence);
        Assert.Equal("failed", Assert.Single(report.Found).Kind);       // 带方括号也得归一成 failed
        Assert.Equal("E003", report.Found[0].Evidence);
        Assert.Equal(2, report.Next.Count);
        Assert.True(report.Next[0].Recommended);                        // `next [rec]:` ⇒ 推荐
        Assert.False(report.Next[1].Recommended);
    }

    [Fact]
    public void 真机回复原样回放_省方括号也认()
    {
        // 同一批取样的另一种写法（省方括号）—— 两种写法都得归到**同一个类别**，否则「形状」就成了运气。
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 1: 乙",
            "outcome: 丙",
            "found failed: 丁 (E011)",
            "next rec: 戊",
            "next: 己"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal("failed", Assert.Single(parsed.Report!.Found).Kind);
        Assert.True(parsed.Report.Next[0].Recommended);
    }

    [Fact]
    public void 锚不是E形_原样留在正文里()
    {
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 1: 乙（第 3 行）",
            "outcome: 丙"));

        Assert.True(parsed.Accepted, parsed.RejectReason);
        Assert.Equal("乙（第 3 行）", parsed.Report!.Steps[0].What);
        Assert.Null(parsed.Report.Steps[0].Evidence);
    }
}

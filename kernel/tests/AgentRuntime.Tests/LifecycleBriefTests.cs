using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Hosting.Panels;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告渲染 + 锚表**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §五/§八/§九 · P1 第二批）—— 守五件事：
/// <list type="number">
/// <item><b>五节固定序</b>（一/二/三/四/五），**缺节不占用位**；</item>
/// <item><b>未交 ⇒ 只贴框</b>：一句「未提交决策报告」+ 指针，**绝不拿原料充数**（G8）；</item>
/// <item><b>完整展示、不折叠、不截字</b>（主人 19:1x 定）；</item>
/// <item><b>锚表默认关</b>（`/anchors` 另开），且**双向**（`§2.1 ⇄ E011`）；</item>
/// <item><b>确定性</b>：同一份报告渲染两次 ⇒ 正文行与锚表**逐字节相同**（可复算 ⇒ 可 diff ⇒ 可审计）。</item>
/// </list>
/// </summary>
public sealed class LifecycleBriefTests
{
    // ---------------- 夹具 ----------------

    private static string Text(params string[] lines) => string.Join('\n', lines);

    private static DecisionReport Report(params string[] extra)
    {
        var lines = new List<string>
        {
            "[REPORT]",
            "need: 把这轮的部署代号在仓库里统一",
            "step 1: 扫完全仓，定位 3 处代号 (E004)",
            "step 2: 目标文件被锁 ⇒ 改道 write (E011)",
            "outcome: 三处代号已统一；未动其它文件",
        };

        lines.AddRange(extra);

        var parsed = DecisionReportParser.Parse(Text([.. lines]));
        Assert.True(parsed.Accepted, parsed.RejectReason);
        return parsed.Report!;
    }

    private static DecisionReportFrame Frame(bool unreported = false) =>
        new(3, TaskLifecycleStatus.Done, unreported, [], "账：3 轮 · 工具 5 · 拒绝 1 · risk 2 · 用量 12.3k · 追加 4.1k 字");

    /// <summary>某一段标题所在行号（找不到 ⇒ -1）。</summary>
    private static int At(IReadOnlyList<string> lines, string heading) =>
        lines.ToList().FindIndex(l => l.Contains(heading, StringComparison.Ordinal));

    // ---------------- 正例 ----------------

    [Fact]
    public void 五节固定序_编号与锚_兜底通道与账()
    {
        var report = Report(
            "found failed: edit 被锁（已改道） (E011)",
            "found noticed: 顺手发现 README 里也有一处 (E005)",
            "next rec: 我接着扫剩下的 2 处",
            "next: 到此为止，本任务结案");

        var brief = LifecyclePresenter.Brief(report, Frame());

        // 框：身份 + 状态徽章
        Assert.Contains(brief.Lines, l => l.Contains("决策报告 #3", StringComparison.Ordinal) && l.Contains("得解", StringComparison.Ordinal));

        // 五节固定序（缺节不占位 ⇒ 这里五节齐）
        var order = new[] { "一、这一轮要解决什么", "二、怎么解的", "三、结果", "四、过程发现", "五、接下来" }
            .Select(h => At(brief.Lines, h))
            .ToArray();
        Assert.All(order, i => Assert.True(i >= 0, "缺节"));
        Assert.Equal(order.OrderBy(static i => i).ToArray(), order);

        // 解步：宿主的显示键（2.1 / 2.2）+ 行内锚
        Assert.Contains(brief.Lines, l => l.Contains("2.1 扫完全仓，定位 3 处代号", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("（E004）", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("2.2 目标文件被锁", StringComparison.Ordinal));

        // 结果不截断
        Assert.Contains(brief.Lines, l => l.Contains("三处代号已统一；未动其它文件", StringComparison.Ordinal));

        // 过程发现 ≥2 条 ⇒ 表格 T1（类别 / 发现 / 锚）
        Assert.Contains(brief.Lines, l => l.Contains("T1 过程发现", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("失败", StringComparison.Ordinal) && l.Contains("edit 被锁", StringComparison.Ordinal));

        // 接下来：编号项 + 「它建议」标记 + 兜底通道行 + 账
        Assert.Contains(brief.Lines, l => l.Contains("1.", StringComparison.Ordinal) && l.Contains("我接着扫剩下的 2 处", StringComparison.Ordinal) && l.Contains("它建议", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("/trace", StringComparison.Ordinal) && l.Contains("/result", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("账：3 轮", StringComparison.Ordinal));
    }

    [Fact]
    public void 锚表_双向且默认不印()
    {
        var report = Report("found failed: edit 被锁 (E011)");

        var off = LifecyclePresenter.Brief(report, Frame());
        Assert.DoesNotContain(off.Lines, l => l.Contains("锚表", StringComparison.Ordinal));

        var on = LifecyclePresenter.Brief(report, Frame(), showAnchors: true);
        Assert.Contains(on.Lines, l => l.Contains("锚表", StringComparison.Ordinal));

        Assert.True(on.Index.TryTag("§2.1", out var tag));
        Assert.Equal("E004", tag);
        Assert.True(on.Index.TryKey("E011", out var keys));
        Assert.Contains("§2.2", keys);
    }

    [Fact]
    public void 一条发现_用项目符不用表格()
    {
        var brief = LifecyclePresenter.Brief(Report("found failed: edit 被锁 (E011)"), Frame());

        Assert.DoesNotContain(brief.Lines, l => l.Contains("T1", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("4.1 失败", StringComparison.Ordinal));
    }

    [Fact]
    public void 缺节不占位_发现与建议为空就不画那两节()
    {
        var brief = LifecyclePresenter.Brief(Report(), Frame());

        Assert.True(At(brief.Lines, "一、") >= 0);
        Assert.True(At(brief.Lines, "三、结果") >= 0);
        Assert.Equal(-1, At(brief.Lines, "四、过程发现"));
        Assert.Equal(-1, At(brief.Lines, "五、接下来"));
    }

    [Fact]
    public void 确定性_两次渲染逐字节相同()
    {
        var report = Report("found failed: edit 被锁 (E011)", "next: 到此为止");

        var a = LifecyclePresenter.Brief(report, Frame());
        var b = LifecyclePresenter.Brief(report, Frame());

        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(
            a.Index.Anchors.Select(static x => $"{x.Key}={x.Tag}").ToArray(),
            b.Index.Anchors.Select(static x => $"{x.Key}={x.Tag}").ToArray());
    }

    [Fact]
    public void 长结果_不截字不折行()
    {
        var long_ = new string('甲', 300);
        var parsed = DecisionReportParser.Parse(Text(
            "[REPORT]",
            "need: 甲",
            "step 1: 乙",
            $"outcome: {long_}"));

        var brief = LifecyclePresenter.Brief(parsed.Report!, Frame());

        Assert.Contains(brief.Lines, l => l.Contains(long_, StringComparison.Ordinal));
        Assert.DoesNotContain(brief.Lines, l => l.Contains("…", StringComparison.Ordinal));
    }

    // ---------------- 负例（G8：未交只贴框） ----------------

    [Fact]
    public void 未交_只贴框_绝不拿原料充数()
    {
        var brief = LifecyclePresenter.Brief(null, Frame(unreported: true));

        Assert.Contains(brief.Lines, l => l.Contains("未提交决策报告", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("/trace", StringComparison.Ordinal));
        Assert.Contains(brief.Lines, l => l.Contains("账：3 轮", StringComparison.Ordinal));

        // 框里不许出现任何报告正文 / 原料
        Assert.DoesNotContain(brief.Lines, l => l.Contains("三处代号", StringComparison.Ordinal));
        Assert.Empty(brief.Index.Anchors);
    }

    // ---------------- 锚表本身 ----------------

    [Fact]
    public void 锚表_去重与空表()
    {
        var index = ReportAnchorIndex.From(
            [new ReportAnchor("§2.1", "E004"), new ReportAnchor("§2.1", "E004"), new ReportAnchor("§2.2", "")]);

        Assert.Single(index.Anchors);
        Assert.True(index.TryTag("§2.1", out var tag));
        Assert.Equal("E004", tag);
        Assert.False(index.TryTag("§9.9", out _));
        Assert.False(ReportAnchorIndex.Empty.TryKey("E004", out _));
        Assert.NotEmpty(ReportAnchorIndex.Empty.Lines());
    }
}

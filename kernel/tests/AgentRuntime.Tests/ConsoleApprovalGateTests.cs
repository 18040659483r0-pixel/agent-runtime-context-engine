using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **CLI 审批闸门（G2）的测试** —— 每一档结局都要有，且**负例必须有牙**：
/// 非交互 / 没有答复 / 非 <c>y</c> 都必须**拒**，而且拒绝要能被看见（不是静默无事发生）。
/// <para>对照口径：<c>docs/DESIGN-TOOL-FACE.md</c> §四·二 S2/S3/S5。</para>
/// </summary>
public sealed class ConsoleApprovalGateTests
{
    private static ApprovalRequest Request(string tool, string json, string? path)
    {
        var parsed = ToolArgs.Parse(json);
        var face = tool switch
        {
            ToolNames.Write => new WriteTool().Preview(parsed, ToolLimits.Default),
            ToolNames.Edit => new EditTool().Preview(parsed, ToolLimits.Default),
            _ => new ReadTool().Preview(parsed, ToolLimits.Default),
        };

        return new ApprovalRequest(
            tool,
            ToolNames.RiskOf(tool),
            json,
            parsed.Canonical,
            $"{tool} {parsed.Canonical}",
            Turn: 0,
            SessionId: null,
            Face: face,
            ApprovalPath: path ?? string.Empty);
    }

    /// <summary>统一带上取消令牌（xUnit1051），避免每个调用点重复。</summary>
    private static ValueTask<ApprovalDecision> Decide(ConsoleApprovalGate gate, ApprovalRequest request) =>
        gate.DecideAsync(request, TestContext.Current.CancellationToken);

    private static (ConsoleApprovalGate Gate, StringWriter Out) Build(string input, bool interactive)
    {
        var output = new StringWriter();
        var gate = new ConsoleApprovalGate(output, new StringReader(input), () => interactive);
        return (gate, output);
    }

    // ---------------- S5：非交互 ⇒ 拒绝（fail-closed） ----------------

    [Fact]
    public async Task 非交互_一律拒绝_且说一句人话()
    {
        var (gate, output) = Build("y\n", interactive: false);   // 就算输入里塞了 y，也不该被当成人点头

        var decision = await Decide(gate, Request(ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"a\"}", "/tmp/x"));

        Assert.Equal(ApprovalDecision.Denied, decision);
        Assert.Equal(ApprovalActors.NonInteractive, gate.Actor);
        Assert.Contains(ApprovalFaces.Prefix, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("非交互", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 输入结束_判不出_不谎称有人点头()
    {
        var (gate, _) = Build(string.Empty, interactive: true);   // StringReader 空 ⇒ ReadLine 返回 null（EOF）

        var decision = await Decide(gate, Request(ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"a\"}", "/tmp/x"));

        Assert.Equal(ApprovalDecision.Unknown, decision);            // 判不出
        Assert.Equal(ApprovalActors.NonInteractive, gate.Actor);     // 但**绝不**记成 human
        Assert.False(ApprovalActors.IsHuman(gate.Actor));
    }

    // ---------------- 正例 / 负例 ----------------

    [Fact]
    public async Task 点头_执行()
    {
        var (gate, output) = Build("y\n", interactive: true);

        var decision = await Decide(gate, Request(ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"a\"}", "/tmp/x"));

        Assert.Equal(ApprovalDecision.Approved, decision);
        Assert.Equal(ApprovalActors.Human, gate.Actor);
        Assert.Contains("已点头", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task yes_也算点头_但别的词一律拒绝()
    {
        foreach (var (answer, expected) in new[]
                 {
                     ("yes\n", ApprovalDecision.Approved),
                     ("Y\n", ApprovalDecision.Approved),
                     (" y \n", ApprovalDecision.Approved),
                     ("sure\n", ApprovalDecision.Denied),
                     ("\n", ApprovalDecision.Denied),          // 直接回车 = 不点头
                     ("no\n", ApprovalDecision.Denied),
                 })
        {
            var (gate, _) = Build(answer, interactive: true);
            var decision = await Decide(gate, 
                Request(ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"a\"}", "/tmp/x"));

            Assert.Equal(expected, decision);
            Assert.Equal(ApprovalActors.Human, gate.Actor);       // 有人当真答复过
        }
    }

    // ---------------- S2/S3：审批面「知情」（真身 + diff + 覆盖标注） ----------------

    [Fact]
    public async Task 审批面_新文件给全文_且路径是真身()
    {
        using var work = new SnapshotTestWorkspace("approval-face-new");
        var target = Path.Combine(work.Root, "sub", "new.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var (gate, output) = Build("n\n", interactive: true);
        await Decide(gate, Request(
            ToolNames.Write, $"{{\"path\":\"{target}\",\"content\":\"甲\\n乙\\n\"}}", target));

        var text = output.ToString();
        Assert.Contains(target, text, StringComparison.Ordinal);       // 真身绝对路径
        Assert.Contains("新建", text, StringComparison.Ordinal);        // 存在性说清
        Assert.Contains("甲", text, StringComparison.Ordinal);          // 新文件 ⇒ 全文
        Assert.Contains("完整内容", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 审批面_覆盖已存在文件_给diff与显著标注()
    {
        using var work = new SnapshotTestWorkspace("approval-face-overwrite");
        var target = Path.Combine(work.Root, "old.txt");
        await File.WriteAllTextAsync(target, "一\n二\n三\n", TestContext.Current.CancellationToken);

        var (gate, output) = Build("n\n", interactive: true);
        await Decide(gate, Request(
            ToolNames.Write, $"{{\"path\":\"{target}\",\"content\":\"一\\n二改\\n三\\n\"}}", target));

        var text = output.ToString();
        Assert.Contains(target, text, StringComparison.Ordinal);
        Assert.Contains("覆盖", text, StringComparison.Ordinal);        // S2 第三要素：不可逆性要显著
        Assert.Contains("差异", text, StringComparison.Ordinal);        // 已存在 ⇒ diff
        Assert.Contains("+1 行", text, StringComparison.Ordinal);       // 精确计数
        Assert.Contains("-1 行", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 审批面_edit给oldText到newText()
    {
        using var work = new SnapshotTestWorkspace("approval-face-edit");
        var target = Path.Combine(work.Root, "e.txt");
        await File.WriteAllTextAsync(target, "甲\n乙\n", TestContext.Current.CancellationToken);

        var (gate, output) = Build("n\n", interactive: true);
        await Decide(gate, Request(
            ToolNames.Edit,
            $"{{\"path\":\"{target}\",\"oldText\":\"乙\",\"newText\":\"丙\"}}",
            target));

        var text = output.ToString();
        Assert.Contains(target, text, StringComparison.Ordinal);
        Assert.Contains("oldText → newText", text, StringComparison.Ordinal);
        Assert.Contains("丙", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 审批面_不含模型原文_只由参数渲染()
    {
        // S3：审批面从**结构化参数**渲染 ⇒ 模型在正文里写的任何"审批提示"都进不来。
        var (gate, output) = Build("n\n", interactive: true);
        await Decide(gate, Request(
            ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"被写入的内容\"}", "/tmp/x"));

        var text = output.ToString();
        Assert.DoesNotContain("我已批准", text, StringComparison.Ordinal);
        Assert.Contains("被写入的内容", text, StringComparison.Ordinal);  // 内容来自参数，不是模型正文
    }

    // ---------------- 只读工具根本不问 ----------------

    [Fact]
    public async Task 一次一批_本闸门不缓存任何许可()
    {
        var (gate, _) = Build("y\ny\n", interactive: true);
        var request = Request(ToolNames.Write, "{\"path\":\"/tmp/x\",\"content\":\"a\"}", "/tmp/x");

        Assert.Equal(ApprovalDecision.Approved, await Decide(gate, request));

        // 同一个动作**再问一次**：本闸门没有"记住上次点头"这回事，仍然要人再答一次（这里是第二次 y）。
        Assert.Equal(ApprovalDecision.Approved, await Decide(gate, request));

        // 第三次没有输入了 ⇒ EOF ⇒ 判不出 ⇒ 拒。
        Assert.Equal(ApprovalDecision.Unknown, await Decide(gate, request));
    }
}

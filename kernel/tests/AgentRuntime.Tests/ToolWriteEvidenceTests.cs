using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 写入类工具的**落点与体量证据**（2026-09-23，坑 #137 族）。
/// <para>
/// 判据：写成功 ⇒ 结果里必须能看到「**绝对落点 + 字节数 + 前后 sha256**」；
/// 写不进去 ⇒ **响亮失败**（且点明相对路径的基准目录）；写进共享临时区 ⇒ 必须**警告**。
/// 背景：2026-09-23 01:15 [WB] 收尾把四层正文写进 <c>/tmp</c> 又没搬回来，
/// 而工具只回「成功」、闸门又全绿 ⇒ 一整层 L4 静默丢失。
/// </para>
/// </summary>
public sealed class ToolWriteEvidenceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ToolContext Context() => new(ToolLimits.Default, 0, null);

    [Fact]
    public async Task 写_新建_回显绝对落点与体量与哈希()
    {
        using var work = new SnapshotTestWorkspace("write-evidence-new");
        var target = Path.Combine(work.Root, "sub", "a.txt");

        var outcome = await new WriteTool().ExecuteAsync(
            Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"甲\\n乙\"}}"), Ct);

        Assert.True(outcome.Ok);
        Assert.Contains($"落点 {ToolPaths.Normalize(target)}", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("新建", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("字节", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("sha ", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("甲\n乙", await File.ReadAllTextAsync(target, Ct));

        // 每用户临时目录是运行时正常的中转区 ⇒ **不该**报临时区警告（只认 /tmp · /var/tmp）。
        Assert.DoesNotContain("系统临时区", outcome.Text, StringComparison.Ordinal);

        // 结论必须**排在参数回显之前**（大正文时回显会把结论淹掉 = 那次「看起来成功了」的成因）。
        Assert.True(
            outcome.Text.IndexOf("落点", StringComparison.Ordinal) < outcome.Text.IndexOf("参数回显", StringComparison.Ordinal),
            "落点/体量必须写在参数回显之前；实际：" + outcome.Text[..Math.Min(200, outcome.Text.Length)]);
    }

    [Fact]
    public async Task 写_覆盖_给出前后哈希且同内容可判()
    {
        using var work = new SnapshotTestWorkspace("write-evidence-overwrite");
        var target = Path.Combine(work.Root, "a.txt");
        var tool = new WriteTool();

        var first = await tool.ExecuteAsync(Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"v1\"}}"), Ct);
        Assert.True(first.Ok);

        var second = await tool.ExecuteAsync(Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"v2\"}}"), Ct);
        Assert.True(second.Ok);
        Assert.Contains("覆盖", second.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("逐字节相同", second.Text, StringComparison.Ordinal);

        var third = await tool.ExecuteAsync(Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"v2\"}}"), Ct);
        Assert.True(third.Ok);
        Assert.Contains("逐字节相同", third.Text, StringComparison.Ordinal);   // 写「一样的内容」也说得出来
        Assert.Equal("v2", await File.ReadAllTextAsync(target, Ct));
    }

    [Fact]
    public async Task 写_目标是个目录_响亮失败并点明基准目录()
    {
        using var work = new SnapshotTestWorkspace("write-evidence-dir");
        var dir = Path.Combine(work.Root, "adir");
        Directory.CreateDirectory(dir);

        var outcome = await new WriteTool().ExecuteAsync(
            Context(), ToolArgs.Parse($"{{\"path\":\"{dir}\",\"content\":\"x\"}}"), Ct);

        Assert.False(outcome.Ok);                                            // 绝不静默成功
        Assert.Contains("目录", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("基准目录", outcome.Text, StringComparison.Ordinal);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task 改_文件不存在_失败里带基准目录()
    {
        using var work = new SnapshotTestWorkspace("edit-evidence-missing");

        var outcome = await new EditTool().ExecuteAsync(
            Context(),
            ToolArgs.Parse($"{{\"path\":\"{Path.Combine(work.Root, "nope.txt")}\",\"oldText\":\"甲\",\"newText\":\"乙\"}}"),
            Ct);

        Assert.False(outcome.Ok);
        Assert.Contains("文件不存在", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("基准目录", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 改_成功_回显落点与前后哈希()
    {
        using var work = new SnapshotTestWorkspace("edit-evidence-ok");
        var target = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(target, "甲\n乙\n", Ct);

        var outcome = await new EditTool().ExecuteAsync(
            Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"oldText\":\"乙\",\"newText\":\"丙\"}}"), Ct);

        Assert.True(outcome.Ok);
        Assert.Contains($"落点 {ToolPaths.Normalize(target)}", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("sha ", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("甲\n丙\n", await File.ReadAllTextAsync(target, Ct));
    }

    [Fact]
    public async Task 写_共享临时区_给警告()
    {
        if (!Directory.Exists("/tmp"))
        {
            return;   // 非 unix 环境跳过
        }

        var target = Path.Combine("/tmp", "tool-evidence-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var outcome = await new WriteTool().ExecuteAsync(
                Context(), ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"正文\"}}"), Ct);

            Assert.True(outcome.Ok);
            Assert.Contains("系统临时区", outcome.Text, StringComparison.Ordinal);
            Assert.Contains("不该写这里", outcome.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }
}

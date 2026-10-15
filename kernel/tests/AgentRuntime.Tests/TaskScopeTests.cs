using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Index;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **TaskScope（意图 → 地址表）的不变量测试** —— 批次 3 判据五件：
/// ① 同输入两次逐字节一致（确定性）；② 投影移走 ⇒ 逐字节重建；③ 真身变 ⇒ 报「陈旧」并重建；
/// ④ 摘要只走按需区段（真跑一次 scope 后常驻前缀字节不变）；⑤ `read --symbol` 的返回 ≪ 整文件。
/// <para>另加一条**现状判据**（应红③ 的软形态）：同一窗重复读 ⇒ 带「逐字节相同」提示，换窗 ⇒ 不带（负例）。</para>
/// </summary>
public sealed class TaskScopeTests
{
    private const string Task = "fix read symbol LocateBySymbol in FileTools";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RuntimeConfiguration Config() => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Modules = [],
        Frozen = new FrozenConfiguration(),
    };

    /// <summary>参数文本用拼接构造（**避免在源码里写反斜杠转义** —— 坑 #157 同族）。</summary>
    private static ToolArgs Args(string path, int maxLines, int? offset = null)
    {
        const char q = '"';
        var json = "{" + q + "path" + q + ":" + q + path + q + "," + q + "maxLines" + q + ":" + maxLines
            + (offset is null ? string.Empty : "," + q + "offset" + q + ":" + offset) + "}";
        return ToolArgs.Parse(json);
    }

    private static ToolArgs TaskArgs(string task)
    {
        const char q = '"';
        return ToolArgs.Parse("{" + q + "task" + q + ":" + q + task + q + "}");
    }

    /// <summary>造一个像仓库根的沙箱；另落一个大文件供 <c>read --symbol</c> 与重复读判据用。</summary>
    private static string Sandbox(out string bigFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-scope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "AgentRuntime.slnx"), "<Solution />");
        File.WriteAllText(
            Path.Combine(root, "src", "FileTools.cs"),
            "namespace N;\npublic sealed class FileTools { public static int LocateBySymbol() => 0; }\n");

        // 目标符号放在**文件靠前**处：read 的单条结果上限是 2,000 字符（超限截断）⇒
        // 若把符号埋在填充行之后，「整文件读」的正文里就看不到它（夹具会因截断而假红）。
        bigFile = Path.Combine(root, "src", "Big.cs");
        var filler = string.Join("\n", Enumerable.Range(0, 300).Select(i => "// filler line " + i));
        File.WriteAllText(
            bigFile,
            "namespace N;\npublic sealed class Big\n{\n    public static int Target()\n    {\n        return 42;\n    }\n}\n" + filler + "\n");
        return root;
    }

    // ---------------- ① 确定性：同输入两次逐字节一致 ----------------

    [Fact]
    public void 同一任务两次_地址表逐字节一致()
    {
        var root = Sandbox(out _);
        try
        {
            var first = TaskScopeBuilder.Build(root, Task);
            var second = TaskScopeBuilder.Build(root, Task);

            Assert.Equal(first.TaskHash, second.TaskHash);
            Assert.Equal(first.Render(), second.Render());
            Assert.Contains(first.Candidates, c => c.File.EndsWith("FileTools.cs", StringComparison.Ordinal));

            // 任务原文**不入盘**（只留 12 位指纹）。
            Assert.DoesNotContain("LocateBySymbol", first.Render(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ② 投影移走 ⇒ 逐字节重建 ----------------

    [Fact]
    public void 地址表移走_能逐字节重建()
    {
        var root = Sandbox(out _);
        var backup = Path.Combine(Path.GetTempPath(), "wb-scope-bak-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var built = TaskScopeStore.RebuildNow(root, Task);
            var before = File.ReadAllBytes(built.Path);

            File.Move(built.Path, backup);

            var check = TaskScopeStore.Check(root, Task, rebuildWhenStale: true);
            Assert.Equal(IndexFreshness.Missing, check.Freshness);
            Assert.True(check.Rebuilt);
            Assert.Equal(before, File.ReadAllBytes(check.Path));   // 逐字节一致
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    // ---------------- ③ 真身变 ⇒ 报「陈旧」并重建（绝不静默使用） ----------------

    [Fact]
    public void 真身变了_报地址表陈旧并重建()
    {
        var root = Sandbox(out _);
        try
        {
            var built = TaskScopeStore.RebuildNow(root, Task);
            var old = File.ReadAllText(built.Path);

            File.WriteAllText(
                Path.Combine(root, "src", "FileToolsExtra.cs"),
                "namespace N;\npublic sealed class FileToolsExtra { public static int LocateBySymbol() => 1; }\n");

            var check = TaskScopeStore.Check(root, Task, rebuildWhenStale: true);
            Assert.Equal(IndexFreshness.Stale, check.Freshness);
            Assert.True(check.Rebuilt);
            Assert.Contains("陈旧", check.Detail, StringComparison.Ordinal);
            Assert.NotEqual(old, File.ReadAllText(check.Path));
            Assert.Equal(IndexFreshness.Fresh, TaskScopeStore.Check(root, Task).Freshness);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ④ 摘要只走按需区段：真跑一次 scope，常驻前缀逐字节不变 ----------------

    [Fact]
    public async Task 地址表不进常驻前缀()
    {
        var root = Sandbox(out _);
        try
        {
            var before = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));

            var outcome = await new ScopeTool().ExecuteAsync(
                new ToolContext(new ToolLimits { WorkingDirectory = root }, 0, null),
                TaskArgs(Task),
                Ct);

            Assert.True(outcome.Ok);
            Assert.Contains("任务地址表", outcome.Text, StringComparison.Ordinal);

            var after = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));
            Assert.Equal(before.PromptText, after.PromptText);
            Assert.Equal(before.Id, after.Id);

            // 反向自检：地址表**根本没进**常驻前缀。
            Assert.DoesNotContain("任务地址表", before.PromptText, StringComparison.Ordinal);
            Assert.DoesNotContain("TaskScope", before.PromptText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ⑤ read --symbol：返回 ≪ 整文件，且含签名与行号 ----------------

    [Fact]
    public async Task 按符号读_远小于整文件()
    {
        var root = Sandbox(out var big);
        try
        {
            var whole = new FileInfo(big).Length;
            var read = new ReadTool();

            var bySymbol = await read.ExecuteAsync(
                new ToolContext(ToolLimits.Default, 0, null),
                Args(big, 400), Ct);

            Assert.True(bySymbol.Ok);
            Assert.Contains("Target", bySymbol.Text, StringComparison.Ordinal);

            var symbolRead = await read.ExecuteAsync(
                new ToolContext(ToolLimits.Default, 0, null),
                ToolArgs.Parse("{" + '"' + "path" + '"' + ":" + '"' + big + '"' + "," + '"' + "symbol" + '"' + ":" + '"' + "Target" + '"' + "}"),
                Ct);

            Assert.True(symbolRead.Ok);
            Assert.Contains("符号「Target」", symbolRead.Text, StringComparison.Ordinal);
            Assert.Contains("行", symbolRead.Text, StringComparison.Ordinal);
            Assert.True(symbolRead.Text.Length * 3 < whole, $"按符号读 {symbolRead.Text.Length} 字符，整文件 {whole} 字节 —— 未显著小于整文件。");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- 现状判据（应红③ 软形态）：同窗重复读 ⇒ 提示；换窗 ⇒ 不提示 ----------------

    [Fact]
    public async Task 同一窗重复读_带逐字节相同提示_换窗不带()
    {
        var root = Sandbox(out var big);
        try
        {
            var read = new ReadTool();
            var ctx = new ToolContext(ToolLimits.Default, 0, "s");

            var first = await read.ExecuteAsync(ctx, Args(big, 20), Ct);
            Assert.True(first.Ok);
            Assert.DoesNotContain("逐字节相同", first.Text, StringComparison.Ordinal);

            var again = await read.ExecuteAsync(ctx, Args(big, 20), Ct);
            Assert.Contains("逐字节相同", again.Text, StringComparison.Ordinal);   // 判据有牙

            var other = await read.ExecuteAsync(ctx, Args(big, 20, offset: 30), Ct);
            Assert.DoesNotContain("逐字节相同", other.Text, StringComparison.Ordinal);   // 负例：换窗不误报
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

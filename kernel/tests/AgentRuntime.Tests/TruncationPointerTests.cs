using System.Text;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **C1：截断必须给「能走通」的指针**（依据 <c>handoff/27</c> §三）。
/// <para>
/// 现场：截断话术写「全文在 <c>/trace</c>」—— 那是个**假指针**（<c>/trace</c> 是 TUI 看生命周期轨迹的命令，
/// 不是这条工具结果的原文落点；原文在写入那一刻就被截掉了）。判据只有一个：**顺着指针一步能不能拿到全文**。
/// </para>
/// <para>三条：① 正例 —— 大结果截断 ⇒ 指针给出真落点且**文件真在**、内容逐字节等于全体；
/// ② 应红 —— 旧形状（<c>/trace</c>）必须被抓（拔牙自证）；③ 防假红 —— 小结果**逐字节原样**、不许多话。</para>
/// </summary>
public sealed class TruncationPointerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolLimits LimitsWith(string results) => new()
    {
        ResultDirectory = results,
        MaxOutputChars = 2000,
        MaxCommandEchoChars = 200,
    };

    // ---------------- 正例：指针能走通 ----------------

    [Fact]
    public async Task exec_大输出_截断指针指向真落点且全文逐字节可读()
    {
        var results = TempDir();
        var limits = LimitsWith(results);
        var context = new ToolContext(limits, 0, "test");

        // 一条能产出远大于上限的命令（用 printf 循环造可复算的正文）。
        var tool = new ExecTool();
        var args = ToolArgs.Parse("{\"command\":\"i=0; while [ $i -lt 400 ]; do echo line$i; i=$((i+1)); done\"}");
        var outcome = await tool.ExecuteAsync(context, args, Ct);

        Assert.True(outcome.Text.Length <= limits.MaxOutputChars + 400, "截断话术不该把结果撑回大块");
        Assert.Contains("按字符上限截断", outcome.Text, StringComparison.Ordinal);

        // 真指针：话术里给出的落点必须**真的存在**，且内容 = 完整正文。
        var landing = Path.Combine(results, Directory.GetFiles(results, "*.txt").Select(Path.GetFileName).Single()!);
        Assert.True(File.Exists(landing), $"指针指向的文件必须存在：{landing}");

        var full = await File.ReadAllTextAsync(landing, Ct);
        Assert.StartsWith("exec ", full, StringComparison.Ordinal);
        Assert.Contains("line399", full, StringComparison.Ordinal);
        Assert.True(full.Length > limits.MaxOutputChars, "落盘的必须是**完整**正文（大于上限）");

        // 逐字节可复算：文件名 = 正文 sha256 前 16 位。
        Assert.Equal(Path.GetFileNameWithoutExtension(landing), TruncationPointer.Sha256(full)[..16]);
    }

    [Fact]
    public async Task list_大目录_截断指针指向真落点()
    {
        var results = TempDir();
        var work = TempDir();
        for (var i = 0; i < 300; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(work, $"f{i:D3}-padding-padding-padding.txt"), "x", Ct);
        }

        var context = new ToolContext(LimitsWith(results), 0, "test");
        var outcome = await new ListTool().ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{work}\"}}"), Ct);

        Assert.True(outcome.Truncated);
        Assert.Contains("按字符上限截断", outcome.Text, StringComparison.Ordinal);
        var landing = Directory.GetFiles(results, "*.txt").Single();
        Assert.Contains("f299-padding-padding-padding.txt", await File.ReadAllTextAsync(landing, Ct), StringComparison.Ordinal);
    }

    // ---------------- 应红：旧形状必须被抓 ----------------

    [Theory]
    [InlineData("…（按字符上限截断：只给前 2000 字符，共 9000 字符 ⇒ 全文在 /trace；要接着看就缩小窗口 / 给 offset）")]
    [InlineData("…（按字符上限截断：只给前 2000 字符，共 9000 字符 ⇒ 全文在 /trace；要精准的部分就把命令收窄（grep / head））")]
    public void 应红_旧的假指针说法必须被闸门抓(string oldFakePointer)
    {
        // 闸门本体：截断话术**不许**只给 TUI 命令当落点（它拿不到这条结果的原文）。
        Assert.Contains("全文在 /trace", oldFakePointer, StringComparison.Ordinal);
        Assert.DoesNotContain("全文在 /trace", TruncationPointer.Of(new string('x', 9000), LimitsWith(TempDir()), 2000), StringComparison.Ordinal);
    }

    // ---------------- 防假红：小结果逐字节不变 ----------------

    [Fact]
    public async Task exec_小输出_逐字节原样且无任何截断话术()
    {
        var limits = LimitsWith(TempDir());
        var context = new ToolContext(limits, 0, "test");
        var outcome = await new ExecTool().ExecuteAsync(context, ToolArgs.Parse("{\"command\":\"echo hi\"}"), Ct);

        // 命令回显**逐字节原样**（7 字符 < 回显上限 ⇒ 一个字都不该动），且不得出现任何截断话术。
        // ⚠️ 只断言到 stdout 段为止：`/bin/sh -c` 的 stderr 可能带**本机 shell 噪声**（如 shell-init），
        // 那是环境事实、不归被测物管 —— 拿它做全等断言 = 脆弱夹具（本机跑就红）。
        Assert.StartsWith("exec echo hi → exit 0\n--- stdout ---\nhi\n", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("命令回显截断", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("按字符上限截断", outcome.Text, StringComparison.Ordinal);
        Assert.False(outcome.Truncated);
    }

    [Fact]
    public async Task exec_长命令_回显凝结但命令可辨()
    {
        var limits = LimitsWith(TempDir());
        var context = new ToolContext(limits, 0, "test");
        var cmd = "echo " + new string('a', 400);
        var outcome = await new ExecTool().ExecuteAsync(context, ToolArgs.Parse($"{{\"command\":\"{cmd}\"}}"), Ct);

        Assert.Contains("命令回显截断", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("sha256:", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("完整命令见本轮 [TOOL] 块", outcome.Text, StringComparison.Ordinal);
        // 回显部分不超过上限 + 少量话术
        var head = outcome.Text[..outcome.Text.IndexOf('\n')];
        Assert.True(head.Length < limits.MaxCommandEchoChars + 120, $"回显该被压住，实际 {head.Length}");
    }

    // ---------------- 重复读识别 ----------------

    [Fact]
    public async Task read_同一窗读两次_第二次如实提示逐字节相同()
    {
        var work = TempDir();
        var path = Path.Combine(work, "r.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 50).Select(i => $"l{i}")), Ct);

        var read = new ReadTool();
        var context = new ToolContext(LimitsWith(TempDir()), 0, "s1");
        var args = ToolArgs.Parse($"{{\"path\":\"{path}\",\"maxLines\":10}}");

        var first = await read.ExecuteAsync(context, args, Ct);
        Assert.DoesNotContain("逐字节相同", first.Text, StringComparison.Ordinal);

        var second = await read.ExecuteAsync(context, args, Ct);
        Assert.Contains("逐字节相同", second.Text, StringComparison.Ordinal);
        Assert.Contains("sha256:", second.Text, StringComparison.Ordinal);
        // 只报事实：**不许**给「还剩哪几段没读」的清单（那会递出一张待办清单，坑 #115）。
        Assert.DoesNotContain("未读", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task read_行号同内容不同_不许误报重复()
    {
        var work = TempDir();
        var path = Path.Combine(work, "m.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 50).Select(i => $"l{i}")), Ct);

        var read = new ReadTool();
        var context = new ToolContext(LimitsWith(TempDir()), 0, "s2");
        await read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"maxLines\":10}}"), Ct);

        // 内容变了 ⇒ 即便行号区间一样，也不是「同一份内容」。
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 50).Select(i => $"changed{i}")), Ct);
        var again = await read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"maxLines\":10}}"), Ct);
        Assert.DoesNotContain("逐字节相同", again.Text, StringComparison.Ordinal);
    }
}

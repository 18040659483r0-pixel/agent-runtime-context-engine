using AgentRuntime.Core.Lifecycle;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **生成器清单的覆盖率闸门**（§十·32 + §十·57 + §十·59）—— 判据是**派生规则**，不是关键词清单。
/// <para>
/// 应有集合 = **仓里真实存在的脚本**（<c>tools/**</c> + <c>whitebox/workspace/scripts/**</c>）。
/// 声明方式就是「脚本在那里」（与 <c>workspace/skills/&lt;name&gt;/scripts/</c> 同源）⇒ 新脚本当天进集合，
/// 一旦它不能被认成一次「验证」，本闸门**变红**（而不是等关键词表漏掉、C 类静默判豁免）。
/// </para>
/// <para>每条不变量两件东西（§十·30）：一条**应红** + 一条**防假红**。</para>
/// </summary>
public sealed class GeneratorCoverageTests
{
    /// <summary>仓根（按 <c>AgentRuntime.slnx</c> 认）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓根（AgentRuntime.slnx）。");
    }

    /// <summary>应有集合：仓内脚本（生成器 / 闸门都在这里）。</summary>
    private static List<string> Scripts()
    {
        var root = RepoRoot();
        var dirs = new[]
        {
            Path.Combine(root, "tools"),
            Path.Combine(root, "whitebox", "workspace", "scripts"),
        };

        var found = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            found.AddRange(Directory
                .EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f) is ".py" or ".sh")
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    [Fact]
    public void 应红_新脚本当天就被认出来_不靠关键词清单()
    {
        // 这条**在旧实现下必红**（关键词表里没有它）—— 它就是「会漏」的那个反例。
        Assert.Equal("generator", TaskTerminalAssertions.ClassifyVerification("python3 tools/brand-new-thing.py --run"));
        Assert.Equal("generator", TaskTerminalAssertions.ClassifyVerification("cd /repo && sh scripts/new-sync.sh push"));
    }

    [Fact]
    public void 防假红_只是提到脚本路径不算调用()
    {
        // 提到路径 ≠ 跑了一次生成器（否则 `grep` 会把 C 类判成「已重算」= 假绿）。
        Assert.NotEqual("generator", TaskTerminalAssertions.ClassifyVerification("grep -rn 'tools/path-triage.py' src"));
        Assert.NotEqual("generator", TaskTerminalAssertions.ClassifyVerification("cat tools/build-l4.py"));
    }

    [Fact]
    public void 防假红_闸门与构建不被抢走()
    {
        // `--check` 既像闸门又像脚本调用 ⇒ 必须先判闸门（D 类只认闸门）。
        Assert.Equal("gate", TaskTerminalAssertions.ClassifyVerification("dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --check"));
        Assert.Equal("build", TaskTerminalAssertions.ClassifyVerification("dotnet test tests/X/X.csproj"));
        Assert.Equal(string.Empty, TaskTerminalAssertions.ClassifyVerification("ls -la"));
    }

    [Fact]
    public void 覆盖率_仓内不得再有脚本_且任何新脚本都要被规则认出来()
    {
        var scripts = Scripts();

        // **2026-10-14 翻转**（用户令「一律 C#」）：仓内脚本的**应有集合 = 空**。
        // 旧的 `NotEmpty` 是「有脚本 ⇒ 必须认得出来」；现在**有脚本本身就是红**（更强的不变量）。
        Assert.True(
            scripts.Count == 0,
            $"仓内又出现脚本（一律 C#；请写成 tools/ 下的 C# 工程）：{string.Join("、", scripts)}");

        // 保留原判据的**牙**：万一将来重新允许某种脚本，它仍必须被派生规则认出来（N/N，而非「机制已存在」）。
        var missed = scripts
            .Where(s => !TaskTerminalAssertions.IsRepoScriptCall($"python3 {s}"))
            .ToList();

        Assert.True(
            missed.Count == 0,
            $"有 {missed.Count}/{scripts.Count} 个仓内脚本认不出来：{string.Join("、", missed)}");
    }

    [Fact]
    public void 豁免可数_不算生成器的脚本只在显式清单里()
    {
        var scripts = Scripts();
        var exempt = scripts
            .Where(s => TaskTerminalAssertions.ClassifyVerification($"python3 {s}") is "")
            .ToList();

        // 兜底与豁免必须**一一对得上**：判不出生成器 ⇒ 必须正是例外清单命中的那些（豁免可见、可点数）。
        Assert.All(
            exempt,
            s => Assert.True(
                TaskTerminalAssertions.IsExemptScript($"python3 {s}"),
                $"{s} 既不是生成器、又不在例外清单里（静默漏项）。"));
    }

    [Fact]
    public void 防假红_长期运行的服务不算生成器()
    {
        // 唯一例外清单里的是服务，不是「从源重算派生物」。
        Assert.NotEqual("generator", TaskTerminalAssertions.ClassifyVerification("python3 tools/mock-provider/mock_server.py"));
    }
}

using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 禁并行（**进程级静态**）：<see cref="ToolPaths.NamedRoots"/> 是进程级可写静态，而 <c>RuntimeHost</c> 的
/// <b>构造函数</b>也会写它 —— 十余个测试类在各自（并行）集合里构造宿主，与本人的
/// 「写 → 断言 → 还原」交错 ⇒ 断言读到被覆写的根（实测：全量第 8 次跑即红，`String` 是代码仓根）。
/// <para>与 <c>ToolBaseInjectionTests</c> 同族：改进程级全局的用例一律进 <c>DisableParallelization</c> 集合，
/// 与其他任何集合并行互斥。</para>
/// </summary>
[CollectionDefinition("ToolPaths 命名根", DisableParallelization = true)]
public sealed class ToolPathsNamedRootsCollection
{
}

/// <summary>
/// **命名根**（D2 · 2026-10-10）：工具的相对路径可带 <c>repo/…</c> / <c>live/…</c> 前缀 ⇒ 按该根解析；
/// **不带前缀 ⇒ 行为逐字不变**（仍按 cwd）。
/// <para>真机病根：运行时 cwd（活版）与代码仓根**是两个根**，而相对路径只认一个 ⇒ master 写 <c>src/…</c> 落空、
/// 每条任务先 <c>pwd</c> 探基准（E4）。命名根把「谁都不用猜树」变成**可断言**。</para>
/// </summary>
[Collection("ToolPaths 命名根")]
public sealed class ToolPathsNamedRootsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void 带命名根前缀按该根解析_不带前缀仍按cwd()
    {
        var saved = ToolPaths.NamedRoots;
        var repo = Path.Combine(Path.GetTempPath(), "ar-named-repo-" + Guid.NewGuid().ToString("N")[..8]);
        var live = Path.Combine(Path.GetTempPath(), "ar-named-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Directory.CreateDirectory(Path.Combine(live, "handoff"));

        // 两侧同口径（坑 #139 / §十·54）：Path.GetTempPath() 在 macOS 给 /var/…，
        // 而 ToolPaths.Normalize 按设计解析符号链接 ⇒ /private/var/…。拿未归一的期望值去比
        // 归一后的结果 = 字符串相等恒假。期望值先过同一把尺子（不是产品缺陷：realpath 是 S1 审批真身）。
        repo = ToolPaths.Normalize(repo);
        live = ToolPaths.Normalize(live);
        try
        {
            ToolPaths.NamedRoots = [("repo", repo), ("live", live)];

            // ① 带前缀 ⇒ 按声明的根（大小写不敏感）
            Assert.StartsWith(repo, ToolPaths.Normalize("repo/src/x.cs"), StringComparison.Ordinal);
            Assert.StartsWith(live, ToolPaths.Normalize("live/handoff/a.md"), StringComparison.Ordinal);
            Assert.StartsWith(repo, ToolPaths.Normalize("REPO/src/x.cs"), StringComparison.Ordinal);

            // ② 不带前缀 ⇒ 仍按 cwd（旧行为逐字不变）
            Assert.Equal(
                Path.Combine(Directory.GetCurrentDirectory(), "plain.txt"),
                ToolPaths.Normalize("plain.txt"));

            // ③ 绝对路径不受影响
            var abs = Path.Combine(live, "z.txt");
            Assert.Equal(abs, ToolPaths.Normalize(abs));

            // ④ 只是名字撞前缀、不是根段 ⇒ 不当作命名根（`repoX/…` ≠ `repo/…`）
            Assert.Equal(
                Path.Combine(Directory.GetCurrentDirectory(), "repoX", "a"),
                ToolPaths.Normalize("repoX/a"));
        }
        finally
        {
            ToolPaths.NamedRoots = saved;
        }
    }

    [Fact]
    public void 未声明命名根时_行为与从前逐字相同()
    {
        var saved = ToolPaths.NamedRoots;
        try
        {
            ToolPaths.NamedRoots = [];
            Assert.Equal("", ToolPaths.NamedRootsText);
            Assert.Equal(
                Path.Combine(Directory.GetCurrentDirectory(), "src", "x.cs"),
                ToolPaths.Normalize("src/x.cs"));
        }
        finally
        {
            ToolPaths.NamedRoots = saved;
        }
    }
}

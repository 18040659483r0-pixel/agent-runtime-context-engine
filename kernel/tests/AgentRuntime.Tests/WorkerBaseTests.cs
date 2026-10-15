using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 基座（A1/A2）的不变量测试** —— 批次 4：
/// ① 派发物带机器字段 <c>base:</c>，且与 <c>ComposePrompt</c> 的段名逐字对齐；
/// ② 有名字段里的变量展不开 ⇒ **响亮拒绝**（不把 raw <c>$VAR</c> 递给 worker）；
/// ③ <c>FindRepoRoot</c> 必须找到**能编译的那一层**（代码根优先，不让 <c>whitebox/workspace</c> 的 <c>.svn</c> 提前命中）；
/// ④ 相对路径按**给定基准**解析（引擎基准 = base）。
/// </summary>
public sealed class WorkerBaseTests
{
    private static string TempTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-base-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "whitebox", "workspace", ".svn"));
        File.WriteAllText(Path.Combine(root, "AgentRuntime.slnx"), "<Solution />");
        return root;
    }

    // ---------------- ① 派发物：base 段 + 五段名逐字对齐 ----------------

    [Fact]
    public void 派发物带base段_且五段名齐()
    {
        var task = new CrossProviderTask
        {
            Task = "read WindowResolver",
            TargetProvider = "deepinfra",
            Base = "/repo/root",
            Window = ["/repo/root/src"],
            Note = "上一次回执缺 basis=",
        };

        var p = task.ComposePrompt();
        Assert.Contains("base: /repo/root", p, StringComparison.Ordinal);
        Assert.Contains("TASK:", p, StringComparison.Ordinal);
        Assert.Contains("TARGET-PROVIDER:", p, StringComparison.Ordinal);
        Assert.Contains("NOTE:", p, StringComparison.Ordinal);          // 2026-10-06：知识段→NOTE 段
        Assert.Contains("WINDOW: ", p, StringComparison.Ordinal);   // 与worker 协议区对齐（此前是 “WINDOW (absolute):”，属漂移）
        Assert.Contains("CONTRACT:", p, StringComparison.Ordinal);

        // 缺 base ⇒ 明写「拒绝而不是猜」，绝不静默留空。
        Assert.Contains("refuse rather than guess", new CrossProviderTask { Task = "t" }.ComposePrompt(), StringComparison.Ordinal);
    }

    // ---------------- ② 展不开 ⇒ 响亮拒绝；绝对路径 ⇒ 放行 ----------------

    [Fact]
    public void 变量展不开_响亮拒绝_绝对路径放行()
    {
        Assert.NotNull(WindowResolver.Refusal("$WB_NOPE_DIR/src"));   // 展不开（且不是路径）
        Assert.Contains("展不开", WindowResolver.Refusal("$WB_NOPE_DIR/src")!, StringComparison.Ordinal);
        Assert.Null(WindowResolver.Refusal("/tmp/abs/src"));          // 绝对路径 ⇒ 放行
        Assert.Equal(string.Empty, WindowResolver.Resolve("$WB_NOPE_DIR/src", "/repo"));   // fail-closed：解析不出 ⇒ 空
    }

    // ---------------- ③ 根：代码根优先于工作副本根 ----------------

    [Fact]
    public void 找根_代码根优先于工作副本根()
    {
        var root = TempTree();
        try
        {
            var work = Path.Combine(root, "whitebox", "workspace");
            var found = WindowResolver.FindRepoRoot(work);
            Assert.Equal(Path.GetFullPath(root), found);   // 不是那层有 .svn 的 workspace
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ④ 相对路径按给定基准解析 ----------------

    [Fact]
    public void 相对路径按给定基准解析()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-rel-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "src", "A.cs")),
            WindowResolver.Resolve("src/A.cs", root));
    }

    // ---------------- ⑤ 归一优先：过长的相对路径往祖先找（真机 2026-09-29）----------------

    [Fact]
    public void 相对路径多带一段_归一到祖先里存在的那个()
    {
        // 现场：master 给 `projects/AgentRuntime/docs/PITFALLS.md`（相对**工作副本根**），
        // 而 base 已经是 `<工作副本>/projects/AgentRuntime` ⇒ 直接拼会出现重复的 `projects/AgentRuntime`。
        var soft = Path.Combine(Path.GetTempPath(), "wb-norm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var repo = Path.Combine(soft, "projects", "AgentRuntime");
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            File.WriteAllText(Path.Combine(repo, "docs", "PITFALLS.md"), "x");

            var resolved = WindowResolver.Resolve("projects/AgentRuntime/docs/PITFALLS.md", repo);
            Assert.Equal(Path.Combine(repo, "docs", "PITFALLS.md"), resolved);

            // 不存在的目标（新建文件）行为不变：仍按给定基准拼，不外扩。
            Assert.Equal(
                Path.GetFullPath(Path.Combine(repo, "docs", "new-file.md")),
                WindowResolver.Resolve("docs/new-file.md", repo));
        }
        finally
        {
            Directory.Delete(soft, recursive: true);
        }
    }

    // ---------------- ⑥ A 方案：派发地址一律绝对（2026-09-29 主人裁决）----------------

    [Fact]
    public void 相对路径解不到_派发前拒绝并给绝对路径建议()
    {
        var root = TempTree();
        try
        {
            var problem = WindowResolver.Audit("handoff/36-x.md", out var resolved, root);
            Assert.NotNull(problem);
            Assert.Contains("绝对路径", problem!, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(Path.Combine(root, "handoff", "36-x.md")), problem!, StringComparison.Ordinal);
            Assert.Equal(string.Empty, resolved);

            // 绝对路径、目标不存在、但**所在层存在** ⇒ 不拒（写目标允许尚不存在）。
            Directory.CreateDirectory(Path.Combine(root, "handoff"));
            var abs = Path.GetFullPath(Path.Combine(root, "handoff", "new.md"));
            Assert.Null(WindowResolver.Audit(abs, out var kept, root));
            Assert.Equal(abs, kept);

            // 2026-10-06（主人令 · 问题一）：绝对路径**不再免检** —— 指向一棵**不存在的树**同样拒。
            var ghost = Path.GetFullPath(Path.Combine(root, "ghost", "deep", "x.md"));
            var ghostProblem = WindowResolver.Audit(ghost, out _, root);
            Assert.NotNull(ghostProblem);
            Assert.Contains("不存在的树", ghostProblem!, StringComparison.Ordinal);
            Assert.NotNull(WindowResolver.MissingNote(kept));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 相对路径解得到且存在_归一成绝对后放行()
    {
        var root = TempTree();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "PITFALLS.md"), "x");

            Assert.Null(WindowResolver.Audit("docs/PITFALLS.md", out var abs, root));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "docs", "PITFALLS.md")), abs);
            Assert.Null(WindowResolver.MissingNote(abs));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void window落在基准树之外_派发前拒绝()
    {
        var root = TempTree();
        var other = Path.Combine(Path.GetTempPath(), "wb-out-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "x.md"), "x");

            // 存在、但不在 base 下 ⇒ 拒（worker 的全部相对路径都按 base 解析）。
            var problem = WindowResolver.Audit(Path.Combine(other, "x.md"), out var resolved, root);
            Assert.NotNull(problem);
            Assert.Contains("基准树之外", problem!, StringComparison.Ordinal);
            Assert.Equal(string.Empty, resolved);

            // 基准树下 ⇒ 放行。
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "y.md"), "y");
            Assert.Null(WindowResolver.Audit(Path.Combine(root, "docs", "y.md"), out var ok, root));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "docs", "y.md")), ok);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void 正文里的相对路径_只报不改()
    {
        var root = TempTree();
        try
        {
            var bad = WindowResolver.AuditTaskPaths("读 handoff/36-next-big-move.md 再回来", root);
            Assert.Single(bad);
            Assert.Contains("handoff/36-next-big-move.md", bad[0], StringComparison.Ordinal);

            // 绝对路径不报。
            Assert.Empty(WindowResolver.AuditTaskPaths($"读 {Path.Combine(root, "src")} 再回来", root));

            // 层标号（L1/L2 这种）不当成路径报，否则每次派发都刷屏。
            Assert.Empty(WindowResolver.AuditTaskPaths("按 L1/L2 指引下探", root));

            // 中文正文里的 `/` 不是路径（2026-09-29 真机两条假阳性：「派发/worker」「各节行范围/字符数」）——
            // 假阳性会把 master 支到「改成绝对路径」的错改法上，所以宁可漏报。
            Assert.Empty(WindowResolver.AuditTaskPaths("紧接上一轮那份派发/worker 源码地图，只交两项", root));
            Assert.Empty(WindowResolver.AuditTaskPaths("先给该文件全部节标题与各节行范围/字符数", root));

            // 反面守住：**像路径的**（末段带扩展名 / 显式目录）仍必须报。
            Assert.Single(WindowResolver.AuditTaskPaths("读 handoff/36-next-big-move.md 再回来", root));
            Assert.Single(WindowResolver.AuditTaskPaths("扫 ./handoff 下看看", root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

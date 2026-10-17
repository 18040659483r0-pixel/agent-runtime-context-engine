using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Hosting;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **收尾面窗口**（用户 2026-09-30 01:03：「让 master 在收尾时获得全部工具处理能力，并且完全 master 自己收尾，
/// 而 start 后还是按照 默认 master 指挥 worker 干活的模式做」）。
/// <para>三条不变量：</para>
/// <list type="number">
/// <item><b>默认（不传开关）= 与从前逐字相同</b>：没有新模块，master 面还是两张地图。</item>
/// <item><b>窗口开 = master 面是全量</b>，且模式声明与面**同源渲染**（键清单不许手写）。</item>
/// <item><b>冻结前缀与窗口无关</b>（工具面零注入 + 模式声明是动态段）—— 这是**不自指**的判据：
/// 若模式声明进了冻结正文，窗口一开前缀指纹就变，而「是否存在未收尾内容」正是拿指纹比的 ⇒
/// 窗口会把自己点亮、永远关不上。</item>
/// </list>
/// </summary>
public sealed class CloseoutFaceWindowTests
{
    private static RuntimeConfiguration Config() => new()
    {
        BaseUrl = "https://x/v1",
        Model = "m",
        Modules = ["system-rules", "append-stream", "tool"],
        SystemRules = "你是助手。",
    };

    private static IReadOnlyList<string> MasterFaceOf(IReadOnlyList<IRuntimeModule> modules) =>
        [.. modules.OfType<ToolModule>().Single().Runner.Tools.Select(t => t.Name)];

    [Fact]
    public void 默认_面是全量_且没有收尾窗口模块()
    {
        var modules = ModuleRegistry.Create(Config());

        // 2026-10-10（用户令「把工具交回 master」）：master 面 = **全量**（ToolNames.MasterFace = All）
        // ⇒ 窗口不再是「两张地图 → 全量」的开关。
        Assert.Equal(ToolNames.All, MasterFaceOf(modules));
        Assert.DoesNotContain(modules, m => m is CloseoutWindowModule);
    }

    [Fact]
    public void 窗口开_master面是全量_且有模式声明()
    {
        var modules = ModuleRegistry.Create(Config(), closeoutWindow: true);

        Assert.Equal(ToolNames.All, MasterFaceOf(modules));
        Assert.Contains(modules, m => m is CloseoutWindowModule);

        var text = CloseoutWindowModule.Render();
        foreach (var name in ToolNames.All)
        {
            Assert.Contains(name + "{", text, StringComparison.Ordinal);
        }

        Assert.Contains("CLOSEOUT WINDOW", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 收尾命令强制开全量面_且_reset_后收回()
    {
        // 用户 2026-09-30 12:47：「收尾命令中强制使用 master 全量工具授权；非收尾阶段工具面从 master 手中收走」。
        // 2026-10-10（用户令「把工具交回 master」）：master 面**常态就是全量** ⇒ 本测试改守**窗口开关**本身
        // （收尾命令开窗 / reset 收窗），面不再随窗口变（但它仍是同一份声明渲染出来的）。
        using var harness = new TuiHarness("closeout-face-force", modules: ["rules","append-stream","tool"]);
        var host = harness.Host;

        Assert.False(host.CloseoutFaceWindow);
        Assert.Equal(ToolNames.All, MasterFaceOf(host.Modules));

        SessionLifecycle.Closeout(host, DateTimeOffset.Now);

        Assert.True(host.CloseoutFaceWindow);
        Assert.Contains(host.Modules, m => m is CloseoutWindowModule);
        Assert.Equal(ToolNames.All, MasterFaceOf(host.Modules));

        SessionLifecycle.Reset(host, DateTimeOffset.Now);

        Assert.False(host.CloseoutFaceWindow);
        Assert.Equal(ToolNames.All, MasterFaceOf(host.Modules));
    }

    [Fact]
    public void 冻结前缀与窗口无关_不自指()
    {
        var off = ModuleRegistry.Create(Config());
        var on = ModuleRegistry.Create(Config(), closeoutWindow: true);

        Assert.Equal(FrozenPrefix.Assemble(off).Id, FrozenPrefix.Assemble(on).Id);
    }
}

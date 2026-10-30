using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Presentation;
using AgentRuntime.Tui2;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 顶栏（TUI2）的界面文案必须全部走语言包：**英文档下不出现汉字**（2026-10-29 主人令）。
/// <para>夹具自建：不写真实 ~/.agentruntime（全部落在临时工作区）。</para>
/// </summary>
public sealed class TopBandLanguageTests
{
    private static RuntimeConfiguration Config(SnapshotTestWorkspace ws) => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "fake-model",
        Modules = RuntimeHost.SplitList("append-stream,current-tail,dynamic-draft,focus"),
        Frozen = new FrozenConfiguration
        {
            Root = ws.FrozenRoot(),
            Watermark = ws.File("watermark.json"),
        },
        Stream = new StreamConfiguration { Path = ws.File("stream.jsonl") },
        Snapshot = new SnapshotConfiguration { Path = ws.File("snapshot.json"), Enabled = false },
        Focus = new FocusConfiguration { Path = ws.File("focus.json") },
        Lifecycle = new LifecycleConfiguration
        {
            Handover = ws.File("handover.json"),
            HandoverExplicit = true,
        },
        CurrentTail = new CurrentTailConfiguration { StorePath = ws.File("tail") },
        DynamicDraft = new DynamicDraftConfiguration { StorePath = ws.File("draft") },
    };

    [Fact]
    public void 顶栏空台面_英文档下不含汉字_且用语言包的空值文案()
    {
        var original = TuiStrings.Language;
        using var ws = new SnapshotTestWorkspace("topband-lang");
        using var host = RuntimeHost.BootWith(http: null, FakeModelClient.Returning("好的"), Config(ws));
        try
        {
            TuiStrings.Language = TuiLanguage.En;

            var band = new TopBandProvider(host, [], new TaskClock(), new SentContext());
            band.RefreshIfDue(120, force: true);
            var text = string.Join("\n", band.Lines);

            Assert.DoesNotContain(text, c => c >= '\u4e00' && c <= '\u9fff');
            Assert.Contains(TuiStrings.T(TuiKey.EmptyZero), text);
        }
        finally
        {
            TuiStrings.Language = original;
        }
    }
}

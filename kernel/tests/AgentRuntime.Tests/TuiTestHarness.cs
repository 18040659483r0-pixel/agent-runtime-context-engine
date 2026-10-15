using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>一个独立的 TUI 测试台：临时工作区 + 配置文件 + 假模型客户端 + 共享宿主。
/// <para>默认假模型只回一句；要驱动**多轮**（自动接续链 / 工具轮）就传 <c>replies</c> 按序回放。</para></summary>
internal sealed class TuiHarness : IDisposable
{
    public TuiHarness(string label, string[]? modules = null, bool autoSnapshot = true, string[]? replies = null,
        FakeModelClient? client = null)
    {
        Workspace = new SnapshotTestWorkspace(label);
        Workspace.FrozenRoot();
        ConfigPath = Workspace.File("config.xml");
        RuntimeConfiguration.Save(new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "fake-model",
            Modules = modules is null
                ? ["system-rules", "rules", "append-stream", "current-tail", "dynamic-draft", "focus"]
                : [.. modules],
            SystemRules = "测试铁则",
            Frozen = new FrozenConfiguration { Root = "./frozen", Watermark = "./watermark.json" },
            Stream = new StreamConfiguration { Path = "./stream.jsonl" },
            Snapshot = new SnapshotConfiguration { Path = "./snapshot.json", Enabled = autoSnapshot },
            Focus = new FocusConfiguration { Path = "./focus.json" },
            CurrentTail = new CurrentTailConfiguration { StorePath = "./tail" },
            DynamicDraft = new DynamicDraftConfiguration { StorePath = "./draft" },
        }, ConfigPath);

        Config = RuntimeHost.ResolveConfiguration(ConfigPath, new HostOverrides());
        Client = client ?? (replies is { Length: > 0 }
            ? FakeModelClient.ReturningSequence(replies)
            : FakeModelClient.Returning("[mock] 收到"));
        Host = RuntimeHost.BootWith(http: null, Client, Config);
        Ledger = new TurnLedger();
        Ablation = new AblationService();
        Router = new PanelRouter(Host, Ledger, Ablation);
    }

    public SnapshotTestWorkspace Workspace { get; }

    public string ConfigPath { get; }

    public RuntimeConfiguration Config { get; }

    public FakeModelClient Client { get; }

    public RuntimeHost Host { get; }

    public TurnLedger Ledger { get; }

    public AblationService Ablation { get; }

    public PanelRouter Router { get; }

    public StringWriter Stderr { get; } = new();

    /// <summary>磁盘上「状态类」文件的当前字节（面板只读性的证据）。</summary>
    public Dictionary<string, byte[]> StateBytes()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[]
        {
            Workspace.File("stream.jsonl"),
            Workspace.File("snapshot.json"),
            Workspace.File("focus.json"),
            Workspace.File("tail/default.json"),
            Workspace.File("draft/default.json"),
        })
        {
            map[path] = File.Exists(path) ? File.ReadAllBytes(path) : [];
        }

        return map;
    }

    public void Dispose()
    {
        Host.Dispose();
        Workspace.Dispose();
    }
}

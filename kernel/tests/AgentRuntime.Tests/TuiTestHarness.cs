using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>一个独立的 TUI 测试台：临时工作区 + 配置文件 + 假模型客户端 + 共享宿主。</summary>
internal sealed class TuiHarness : IDisposable
{
    public TuiHarness(string label, string? modules = null, bool autoSnapshot = true)
    {
        Workspace = new SnapshotTestWorkspace(label);
        Workspace.FrozenRoot();
        ConfigPath = Workspace.File("config.json");
        File.WriteAllText(ConfigPath, $$"""
        {
          "baseUrl": "https://example.invalid/v1",
          "model": "fake-model",
          "modules": {{modules ?? "[\"system-rules\", \"rules\", \"append-stream\", \"current-tail\", \"dynamic-draft\", \"focus\"]"}},
          "systemRules": "测试铁则",
          "frozen": { "root": "./frozen", "watermark": "./watermark.json" },
          "stream": { "path": "./stream.jsonl" },
          "snapshot": { "path": "./snapshot.json", "enabled": {{autoSnapshot.ToString().ToLowerInvariant()}} },
          "focus": { "path": "./focus.json" },
          "currentTail": { "storePath": "./tail" },
          "dynamicDraft": { "storePath": "./draft" }
        }
        """);

        Config = RuntimeHost.ResolveConfiguration(ConfigPath, new HostOverrides());
        Client = FakeModelClient.Returning("[mock] 收到");
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

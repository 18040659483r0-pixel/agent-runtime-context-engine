using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4 **Runtime Snapshot** 测试（账本面）—— 守四件事：
/// ① 账本**不可变**（类型上没有 setter，Store 上没有「改」入口）；
/// ② 落盘**原子**（失败不留半成品）且**逐字节往返稳定**（时间戳不参与指纹）；
/// ③ **版本闸门**：未知 schemaVersion 拒绝加载（不静默降级）；
/// ④ **指纹自证**：前缀漂移必须报告并列出账本 diff。
/// 恢复流程本身（续写 / 分叉 / 缺口）见 <see cref="SnapshotResumeTests"/>。
/// </summary>
public sealed class RuntimeSnapshotTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("snapshot");

    public void Dispose() => _workspace.Dispose();

    private static readonly DateTimeOffset SavedAt = new(2026, 9, 15, 0, 53, 0, TimeSpan.FromHours(8));

    /// <summary>一份最小冻结前缀（rules.global）。</summary>
    private static FrozenSnapshot Prefix(string text = "铁则正文", string version = "1") =>
        FrozenSnapshot.Create([new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, version, text)]);

    private RuntimeSnapshot Capture(FrozenSnapshot prefix, long cursor = 4) =>
        SnapshotService.Capture("session-1", prefix, cursor, "/tmp/stream.jsonl", "mock-model", SavedAt);

    // ---------------- I4 ----------------

    [Fact]
    public void Snapshot_RoundTrip_IsByteStable()
    {
        var path = _workspace.File("snapshot.json");
        var store = new SnapshotStore(path);
        var prefix = Prefix();

        store.Save(Capture(prefix));
        var first = File.ReadAllBytes(path);

        var loaded = store.Load();
        store.Save(loaded);
        var second = File.ReadAllBytes(path);

        // 写 → 读 → 再写：逐字节一致（SavedAt 原样往返，不参与指纹 ⇒ 不漂移）。
        Assert.Equal(first, second);
        Assert.Equal(SavedAt, loaded.SavedAt);
        var sessionId = loaded.SessionId;
        Assert.NotNull(sessionId);
        Assert.Equal("session-1", sessionId);
        Assert.Equal(4L, loaded.StreamCursor);
        Assert.Equal("/tmp/stream.jsonl", loaded.StreamPath);

        // 指纹可重算自证：快照里的 Id == 重新组装前缀得到的 Id。
        Assert.Equal(prefix.Id, loaded.FrozenSnapshotId);
        Assert.Equal(loaded.FrozenSnapshotId, loaded.Manifest!.SnapshotId);

        // UTF-8 无 BOM + LF；字段序固定（可 diff）。
        Assert.Equal((byte)'{', first[0]);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\r", text);
        var keys = new[]
        {
            "schemaVersion", "sessionId", "frozenSnapshotId", "manifest", "streamCursor",
            "streamPath", "focus", "pending", "workerState", "model", "savedAt",
        };
        var offsets = keys.Select(k => text.IndexOf($"\"{k}\"", StringComparison.Ordinal)).ToArray();
        Assert.All(offsets, offset => Assert.True(offset >= 0));
        Assert.Equal(offsets.OrderBy(o => o).ToArray(), offsets);
    }

    // ---------------- I5 ----------------

    [Fact]
    public void SnapshotStore_Save_IsAtomic()
    {
        var directory = Path.Combine(_workspace.Root, "atomic");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "snapshot.json");
        var store = new SnapshotStore(target);

        store.Save(Capture(Prefix()));

        // 成功后：目录里只有目标文件（临时文件绝不留痕），内容完整可读回。
        Assert.Equal(new[] { target }, Directory.GetFiles(directory));
        Assert.Equal(4L, store.Load().StreamCursor);

        // 失败注入：目标路径被目录占住 → 改名失败；目标不得被破坏，也不留半成品。
        var blocked = Path.Combine(directory, "blocked.json");
        Directory.CreateDirectory(blocked);
        var blockedStore = new SnapshotStore(blocked);

        Assert.ThrowsAny<IOException>(() => blockedStore.Save(Capture(Prefix())));

        Assert.True(Directory.Exists(blocked));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp-*", SearchOption.AllDirectories));

        // 原来的完整快照仍然完好（半成品写坏的是临时文件，不是目标）。
        Assert.Equal(4L, store.Load().StreamCursor);
    }

    // ---------------- I6 ----------------

    [Fact]
    public void RuntimeSnapshot_HasNoMutators()
    {
        var properties = typeof(RuntimeSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            if (property.SetMethod is null)
            {
                continue; // get-only 更好：连 init 都不给
            }

            Assert.True(IsInitOnly(property.SetMethod), $"{property.Name} 暴露了可写 setter —— 快照账本只允许 init。");
        }

        var methods = PublicDeclaredMethods(typeof(RuntimeSnapshot));

        foreach (var forbidden in new[] { "Remove", "RemoveAt", "RemoveRange", "Insert", "Clear", "Sort", "Reverse", "Replace", "Add", "Set", "Update" })
        {
            Assert.DoesNotContain(forbidden, methods);
        }

        // Store 上也只有一读一写两个入口（属性取值器不算入口）。
        Assert.Equal(new[] { "Load", "Save" }, PublicDeclaredMethods(typeof(SnapshotStore)));

        static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private static string[] PublicDeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal) && !name.StartsWith("set_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    // ---------------- I10 ----------------

    [Fact]
    public void Snapshot_UnknownSchemaVersion_IsRejected()
    {
        var path = _workspace.File("future.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 99,
          "frozenSnapshotId": "deadbeefdeadbeef",
          "streamCursor": 238,
          "streamPath": "/tmp/stream.jsonl",
          "model": "future-model",
          "savedAt": "2030-01-01T00:00:00+08:00"
        }
        """);

        var exception = Assert.Throws<InvalidDataException>(() => new SnapshotStore(path).Load());

        Assert.Contains("schemaVersion=99", exception.Message);
        Assert.Contains("拒绝加载", exception.Message);
    }

    [Fact]
    public void Snapshot_CurrentSchemaVersion_IsAccepted()
    {
        var path = _workspace.File("known.json");
        var store = new SnapshotStore(path);
        store.Save(Capture(Prefix()));

        Assert.Equal(SnapshotService.CurrentSchemaVersion, store.Load().SchemaVersion);
    }

    // ---------------- I3 ----------------

    [Fact]
    public void Verify_ReportsPrefixDriftWithSegmentDiff()
    {
        var snapshot = Capture(Prefix("铁则正文", "1"));
        var current = Prefix("铁则正文已改", "2");

        Assert.NotEqual(snapshot.FrozenSnapshotId, current.Id); // 指纹确实变了（重算自证，不沿用）

        var verification = SnapshotService.Verify(snapshot, current, fileCursor: 4);

        // 前缀变了**不阻断**恢复（历史是历史），但必须报告并说清变了哪几段。
        Assert.Equal(ResumeOutcome.Exact, verification.Outcome);
        Assert.Contains(verification.Warnings, w => w.Contains("冻结前缀已变更"));
        Assert.Contains(verification.Warnings, w => w.Contains("rules.global") && w.Contains("v1 → v2"));
    }

    [Fact]
    public void Verify_WhenPrefixUnchanged_IsSilent()
    {
        var prefix = Prefix();
        var verification = SnapshotService.Verify(Capture(prefix), prefix, fileCursor: 4);

        Assert.Equal(ResumeOutcome.Exact, verification.Outcome);
        Assert.Empty(verification.Warnings);
        Assert.True(verification.IsExact);
    }

    // ---------------- I11 ----------------

    [Fact]
    public void Closeout_RecordsRealStreamCursor()
    {
        var streamPath = _workspace.File("stream.jsonl");
        var store = new SessionStreamStore(streamPath);
        store.Append(new SessionEvent(1, SessionEventKind.UserInput, "第一句"));
        store.Append(new SessionEvent(2, SessionEventKind.AgentOutput, "答一"));
        store.Append(new SessionEvent(3, SessionEventKind.UserInput, "第二句"));
        store.Append(new SessionEvent(4, SessionEventKind.AgentOutput, "答二"));

        // 「真实游标」= 从流文件重放出来的条数（以前 CLI 这里硬编码 0）。
        var cursor = new SessionStreamStore(streamPath).Load().Cursor;
        Assert.Equal(4L, cursor);

        var watermarkPath = _workspace.File("watermark.json");
        var watermark = CloseoutService.Perform(watermarkPath, Prefix(), (int)cursor, SavedAt);

        Assert.Equal(4, watermark.StreamCursor);
        Assert.Equal(4, CloseoutService.Load(watermarkPath)!.StreamCursor);

        using var document = JsonDocument.Parse(File.ReadAllText(watermarkPath));
        Assert.Equal(4, document.RootElement.GetProperty("StreamCursor").GetInt32());
    }

    // ---------------- 扩展点 / 层级 ----------------

    [Fact]
    public void Capture_OnlyRecordsPosition_PreReservedFieldsStayEmpty()
    {
        var snapshot = Capture(Prefix());

        Assert.Empty(snapshot.Focus);
        Assert.Empty(snapshot.Pending);
        Assert.Equal(string.Empty, snapshot.WorkerState);

        // 账本里没有正文（正文靠流重放）：段正文只在 prompt 里，不进恢复点。
        var json = File.ReadAllText(SaveAndRead(snapshot));
        Assert.DoesNotContain("铁则正文", json);
    }

    private string SaveAndRead(RuntimeSnapshot snapshot)
    {
        var path = _workspace.File($"capture-{Guid.NewGuid():N}.json");
        new SnapshotStore(path).Save(snapshot);
        return path;
    }

    [Fact]
    public void 层级_Snapshot不引用Modules与Providers()
    {
        var referenced = typeof(SnapshotService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("AgentRuntime.Modules", referenced);
        Assert.DoesNotContain("AgentRuntime.Providers", referenced);
        Assert.DoesNotContain("AgentRuntime.Cli", referenced);

        // 快照类型都在 Core 程序集里（层级：Cli → Modules → Core → Models）。
        Assert.Same(typeof(FrozenSnapshot).Assembly, typeof(RuntimeSnapshot).Assembly);
    }
}

using System.Reflection;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.1 **Focus 纯函数面**测试 —— 守四件事：
/// ① 权重表是**流上的可重算纯函数**（同流两次 ⇒ 逐条相同，含落盘重放）；
/// ② 渲染**逐字节稳定**（同状态 ⇒ 同 band；空焦点 ⇒ 空串）；
/// ③ <see cref="FocusState"/> **没有修改入口**（反射断言：无 setter / Remove / Insert）；
/// ④ 默认值 = K=16 / **minWeight=0.95**（规格书 §六 初值 1.0，主人 2026-09-15 下调）/ 半衰期=40 / band 一行封顶。
/// </summary>
public sealed class FocusServiceTests
{
    private static SessionAppendStream StreamWithReports()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "问题一");
        stream.Append(SessionEventKind.AgentOutput, "回答一");
        stream.Append(SessionEventKind.FocusReport, "E001 E004");   // turn 1 自报
        stream.Append(SessionEventKind.UserInput, "问题二");
        stream.Append(SessionEventKind.AgentOutput, "回答二");
        stream.Append(SessionEventKind.FocusReport, "E002 E004");   // turn 2 自报
        return stream;
    }

    // ---------------- I7 ----------------

    [Fact]
    public void Focus_Render_IsByteStable()
    {
        var state = new FocusState { Tags = ["E004", "E003", "E004"] };

        // 同状态 ⇒ 同 band（逐字节），且渲染前先归一化（去重 + 升序）——输入顺序不泄漏进 prompt。
        Assert.Equal("[FOCUS] E003 E004", FocusService.Render(state));
        Assert.Equal(FocusService.Render(state), FocusService.Render(state));

        // 同流 ⇒ 同渲染行（重放同一份输入得到同一串字节）。
        var stream = StreamWithReports();
        var options = new FocusOptions();
        var first = FocusService.Render(FocusService.Snapshot(stream, options));
        var second = FocusService.Render(FocusService.Snapshot(stream, options));
        Assert.Equal(first, second);

        // 算一下这串字节是怎么来的（权重降序选，**渲染时按 Tag 升序**；低于 minWeight 不选）：
        //   E004：turn1 + turn2 各报一次 ⇒ 0.5^(1/40) + 1.0 = 1.9828（权重最高）
        //   E002：turn2 报一次 ⇒ 1.0（Δ=0，最高档）
        //   E001：turn1 报一次 ⇒ 0.5^(1/40) = 0.9828 ≥ minWeight(0.95) ⇒ 入选
        //        （阈值若还是初值 1.0，E001 在 Δ=1 就退场 —— 下调阈值救的正是这种「只报过一次」的标签）
        Assert.Equal("[FOCUS] E001 E002 E004", first);

        // 空焦点 ⇒ 空串（不产生任何消息）。
        Assert.Equal(string.Empty, FocusService.Render(FocusState.Empty));
    }

    // ---------------- 额外：单次自报的留存期（默认阈值的联合后果） ----------------

    [Fact]
    public void Focus_SingleReport_SurvivesThreeTurns()
    {
        // 默认阈值 0.95 + 半衰期 40 ⇒ **只自报一次**的标签撑 3 个 turn：
        // 权重 = 0.5^(Δ/40)：Δ=0 ⇒ 1.0、Δ=1 ⇒ 0.9828、Δ=2 ⇒ 0.9659 均 ≥ 0.95；Δ=3 ⇒ 0.9494 ⇒ 退场。
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "问题一");
        stream.Append(SessionEventKind.AgentOutput, "回答一");
        stream.Append(SessionEventKind.FocusReport, "E001");

        var single = new[] { "E001" };
        Assert.Equal(single, FocusService.Snapshot(stream, new FocusOptions()).Tags);   // Δ = 0

        for (var delta = 1; delta <= 2; delta++)
        {
            stream.Append(SessionEventKind.UserInput, $"问题{delta + 1}");
            stream.Append(SessionEventKind.AgentOutput, $"回答{delta + 1}");
            Assert.Equal(single, FocusService.Snapshot(stream, new FocusOptions()).Tags); // Δ = 1 / 2
        }

        stream.Append(SessionEventKind.UserInput, "问题四");
        stream.Append(SessionEventKind.AgentOutput, "回答四");
        Assert.Empty(FocusService.Snapshot(stream, new FocusOptions()).Tags);             // Δ = 3 ⇒ 退场
    }

    // ---------------- I10 ----------------

    [Fact]
    public void Focus_Weights_AreRecomputable()
    {
        var stream = StreamWithReports();

        var first = FocusService.Weigh(stream, halfLifeTurns: 40);
        var second = FocusService.Weigh(stream, halfLifeTurns: 40);

        Assert.Equal(first.Signature(), second.Signature());
        Assert.Equal(3, first.Count);                                  // E001 / E002 / E004

        // 落盘再重放 ⇒ 逐条相同（真相在流里，不在内存里）。
        var path = Path.Combine(Path.GetTempPath(), "agentruntime-focus-tests", $"{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new SessionStreamStore(path);
            foreach (var @event in stream.Events)
            {
                store.Append(@event);
            }

            var replayed = FocusService.Weigh(new SessionStreamStore(path).Load(), halfLifeTurns: 40);
            Assert.Equal(first.Signature(), replayed.Signature());
        }
        finally
        {
            File.Delete(path);
        }

        // 半衰期确实在起作用：同样两条自报，半衰期越短，旧那条权重越低。
        var longHalfLife = FocusService.Weigh(stream, halfLifeTurns: 40);
        var shortHalfLife = FocusService.Weigh(stream, halfLifeTurns: 1);
        Assert.True(longHalfLife.TryGet("E001", out var slow));
        Assert.True(shortHalfLife.TryGet("E001", out var fast));
        Assert.True(fast.Weight < slow.Weight);
    }

    // ---------------- I15 ----------------

    [Fact]
    public void FocusState_HasNoMutators()
    {
        var type = typeof(FocusState);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            if (property.SetMethod is null)
            {
                continue;
            }

            // 连 init 都允许，但**不允许**普通 setter（焦点只能在别处重算，不能就地被改）。
            Assert.True(IsInitOnly(property.SetMethod), $"{property.Name} 暴露了可写 setter —— FocusState 必须不可变。");
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal) && !n.StartsWith("set_", StringComparison.Ordinal))
            .ToArray();

        foreach (var forbidden in new[] { "Remove", "RemoveAt", "RemoveRange", "Insert", "Add", "Clear", "Set", "Update", "Replace", "Sort" })
        {
            Assert.DoesNotContain(forbidden, methods);
        }

        // 集合一律只读视图（拿不到可变集合）。
        Assert.Equal(typeof(IReadOnlyList<string>), type.GetProperty(nameof(FocusState.Tags))!.PropertyType);

        static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    // ---------------- 额外：默认值 = 规格书 §六 ----------------

    [Fact]
    public void Focus_Defaults_MatchSpec()
    {
        var options = new FocusOptions();

        Assert.Equal(16, options.TopK);
        Assert.Equal(0.95, options.MinWeight);
        Assert.Equal(0.95, FocusOptions.DefaultMinWeight);
        Assert.Equal(40, options.HalfLifeTurns);
        Assert.Equal(1, FocusOptions.MaxBandLines);
        Assert.Equal(FocusPolicy.Report, options.Policy);

        // 自报提示词的**唯一声明处已迁到协议区**（`focus.reportHint` 配置项已删除）：
        //   以前的断言是 `FocusOptions.DefaultReportHint` 非空 —— 现在该字段不存在，改断言协议区文本含自报格式。
        Assert.Contains("[FOCUS]", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("[TAIL]", ProtocolText.Text, StringComparison.Ordinal);

        // 配置段默认值 → 纯参数：逐项对上（两处默认值不许分裂）。
        var fromConfig = new RuntimeConfiguration().Focus.ToOptions();
        Assert.Equal(options.TopK, fromConfig.TopK);
        Assert.Equal(options.MinWeight, fromConfig.MinWeight);
        Assert.Equal(options.HalfLifeTurns, fromConfig.HalfLifeTurns);
        Assert.Equal(FocusPolicy.Report, fromConfig.Policy);
        Assert.Contains("focus", RuntimeConfiguration.KnownModules);
    }

    // ---------------- 额外：协议解析（自报行） ----------------

    [Fact]
    public void Focus_ReportParsing_ToleratesTailNoise()
    {
        Assert.True(FocusService.TryParseReport("答案正文\n[FOCUS] E004 E007", out var tags));
        Assert.Equal(new[] { "E004", "E007" }, tags);

        // 大小写 / 逗号 / 重复 / 非法词都能收敛到规范标签。
        Assert.True(FocusService.TryParseReport("x\n[focuse] 嗯\n[FOCUS] e7, E007 E4x", out var messy));

        // 解析不到 ⇒ false（不报错）：模型没配合不是数据错误。
        Assert.False(FocusService.TryParseReport("这轮没有自报", out var none));
        Assert.Empty(none);
        Assert.False(FocusService.TryParseReport(null, out _));

        // 显式覆盖也是同一个归一化口径（非法标签被丢弃）。
        Assert.Equal(new[] { "E003", "E010" }, FocusService.Normalize(["e3", " E010 ", "乱写"]));
    }

    [Fact]
    public void Focus_ReportParsing_被后面的块压住也能解析()
    {
        // 与 [L3] 同一条根因（窗口常数曾四处各写一份）：[FOCUS] 后面跟一个 [TAIL] 块时不能被丢掉。
        var reply = string.Join('\n', "[FOCUS] E004 E007", "", "[TAIL]", "任务：x", "- a", "- b", "- c", "- d");
        Assert.True(FocusService.TryParseReport(reply, out var tags));
        Assert.Equal(new[] { "E004", "E007" }, tags);
    }

    // ---------------- P4 悬空标签告警（焦点面板） ----------------

    [Fact]
    public void Focus_Verify_悬空标签报警_不静默()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "问题一");   // E001
        stream.Append(SessionEventKind.AgentOutput, "回答一"); // E002

        // 存在标签 ⇒ 无警告；空 ⇒ 无警告（无焦点就不产生消息）。
        Assert.Empty(FocusService.Verify(["E001", "E002"], stream.Events));
        Assert.Empty(FocusService.Verify([], stream.Events));
        Assert.Empty(FocusService.Verify(null, stream.Events));

        // 悬空（引用了流里不存在的标签）⇒ 一条警告，点名缺失标签，不静默丢弃。
        var warnings = FocusService.Verify(["E001", "E999"], stream.Events);
        var text = Assert.Single(warnings);
        Assert.Contains("E999", text, StringComparison.Ordinal);
        Assert.Contains("悬空", text, StringComparison.Ordinal);
    }
}

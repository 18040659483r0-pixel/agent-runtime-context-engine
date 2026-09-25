using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Presentation;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **L1 · 生命周期呈现器**（`docs/DESIGN-LIFECYCLE-UX.md` §六）—— 守四件事：
/// <list type="number">
/// <item><b>纯文本 == 上色的正文</b>：产物是标记字符串，<c>Markup.Strip(行)</c> 就是屏幕上那一行的正文
/// （所以落盘 / diff / CI 断言都对着同一份字节）；</item>
/// <item><b>折叠不吞证据</b>：默认折叠中间叙述与工具结果，但<b>被拒的调用与决策点永不折叠</b>；</item>
/// <item><b>决定型内容不截断</b>：请求 / 结果 / 决策点全文（只有总览表的缩略列例外）；</item>
/// <item><b>不知道就说不知道</b>：没有用量账本 ⇒ 页脚写「—」，不写 0。</item>
/// </list>
/// </summary>
public sealed class LifecyclePresenterTests
{
    // ---------------- 夹具 ----------------

    private static LifecycleReport Report(Action<SessionAppendStream> fill, IReadOnlyList<LifecycleTurnUsage>? usages = null)
    {
        var flow = new SessionAppendStream();
        fill(flow);
        return LifecycleAggregator.Aggregate(flow, usages);
    }

    private static TaskLifecycle Card0(Action<SessionAppendStream> fill, IReadOnlyList<LifecycleTurnUsage>? usages = null) =>
        Report(fill, usages).Lifecycles[0];

    /// <summary>「叙述 / 工具 / 拒绝 / 决策」四样齐备的一张卡（末尾停在等 人）。</summary>
    private static TaskLifecycle Folded() => Card0(flow =>
    {
        flow.Append(SessionEventKind.UserInput, "把 run.sh 修好");
        flow.Append(SessionEventKind.AgentOutput, "先看一下。[TOOL] read {\"path\":\"run.sh\"}");
        flow.Append(SessionEventKind.ToolResult, "read run.sh → 42 行");
        flow.Append(SessionEventKind.AgentOutput, "[TOOL] exec {\"cmd\":\"rm -rf /\"} risk: none");
        flow.Append(SessionEventKind.ToolDenied, "exec → 拒绝：硬红线（全局破坏面）");
        flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 要不要顺便清一下临时文件");
    });

    private static TaskLifecycle Done() => Card0(flow =>
    {
        flow.Append(SessionEventKind.UserInput, "把 run.sh 修好");
        flow.Append(SessionEventKind.AgentOutput, "[DONE] run.sh 修好了（退出码 0）");
    });

    // ---------------- 决策点：正文为空 vs 块式正文（2026-09-22 主人真机现场） ----------------

    [Fact]
    public void 决策点_正文为空_不画空问题位_改成一句告警()
    {
        // 现场：卡上是「待你决定：」+ 一个空项目符号 —— 提示人回话却没问题（人就无话可答）。
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "帮我看看这件事");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER]");
        });

        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        Assert.DoesNotContain(plain, l => l.Contains("待你决定", StringComparison.Ordinal));
        Assert.DoesNotContain(plain, l => l.Trim() == "•");
        Assert.Contains(plain, l => l.Contains("没写要什么", StringComparison.Ordinal));
    }

    [Fact]
    public void 决策点_块式正文_两句问题都上卡()
    {
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "帮我看看这件事");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER]\n两件事：\n1. 甲？\n2. 乙？\n\n[FOCUS] E01");
        });

        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        Assert.Contains(plain, l => l.Contains("待你决定", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("甲？", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("乙？", StringComparison.Ordinal));
    }

    // ---------------- 1. 纯文本 == 上色的正文 ----------------

    [Fact]
    public void 标记只带角色_剥掉后就是屏幕上的正文()
    {
        var lines = LifecyclePresenter.Card(Done());

        foreach (var line in lines)
        {
            var plain = Markup.Strip(line);
            Assert.DoesNotContain("[[", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("**", plain, StringComparison.Ordinal);
        }

        var plainLines = lines.Select(Markup.Strip).ToArray();
        Assert.Contains(plainLines, l => l == "生命周期 #1");
        Assert.Contains(plainLines, l => l == "用户：把 run.sh 修好");
        // v8（主人 2026-09-24 19:30）：结果**累积**显示 —— 了结时终局块正文照旧作一条 `结果：`（卡上原有内容不变）。
        Assert.Contains(plainLines, l => l == "结果：run.sh 修好了（退出码 0）");
    }

    [Fact]
    public void 状态徽章只报角色_颜色由样式表定()
    {
        Assert.Equal(("✓", StyleRole.Done, "得解"), LifecyclePresenter.Badge(TaskLifecycleStatus.Done));
        Assert.Equal(("✗", StyleRole.Warn, "无解"), LifecyclePresenter.Badge(TaskLifecycleStatus.NoSolution));
        Assert.Equal(("⏳", StyleRole.Todo, "等人"), LifecyclePresenter.Badge(TaskLifecycleStatus.WaitingForUser));
        Assert.Equal(("●", StyleRole.Attention, "进行中"), LifecyclePresenter.Badge(TaskLifecycleStatus.Running));
        Assert.Equal(("⚠", StyleRole.Warn, "未终局"), LifecyclePresenter.Badge(TaskLifecycleStatus.Unsettled));
    }

    [Fact]
    public void 答复的卡带上指回上一张卡的链接()
    {
        var report = Report(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "帮我选一条路");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 请二选一：① 改现有层；② 新建独立层");
            flow.Append(SessionEventKind.UserInput, "②");
            flow.Append(SessionEventKind.AgentOutput, "[DONE] 按②建了独立层");
        });

        var first = LifecyclePresenter.Card(report.Lifecycles[0]).Select(Markup.Strip).ToArray();
        var second = LifecyclePresenter.Card(report.Lifecycles[1]).Select(Markup.Strip).ToArray();

        Assert.DoesNotContain(first, l => l.StartsWith("接在", StringComparison.Ordinal));
        Assert.Contains(second, l => l == "接在 #1 的决策点之后");
        Assert.Contains(second, l => l == "用户：②");
    }

    [Fact]
    public void 卡是一条任务叙事_需求意图打算过程动作结果风险齐()
    {
        // 主人 2026-09-22 23:0x 定：一张卡要讲清「理解了什么需求 / 打算怎么做 / 过程中发现了什么问题 /
        // 最后做了什么动作 / 还有什么可能有问题」——而不是只把「**末轮**结果」顶上来。
        // 全部字段都是**纯投影**（同一条流两次聚合逐字段相同；零模型调用、零协议改动）。
        var report = Report(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "把 run.sh 修好");
            flow.Append(SessionEventKind.AgentOutput,
                "先看现状。\n[TOOL] read {\"path\":\"run.sh\"} risk: none\n"
                + "[TAIL]\n- solve: run.sh 起不来（缺 shebang）\n- step: 1 -- 读出全文\n"
                + "[DRAFT]\n- 还没决定要不要顺手加日志\n");
            flow.Append(SessionEventKind.RiskClaimed, "risk: none（模型自判）⇒ 自主放行 · read {\"path\":\"run.sh\"}");
            flow.Append(SessionEventKind.ToolResult, "read {\"path\":\"run.sh\"} → 26 行 ...", "read");
            flow.Append(SessionEventKind.ToolDenied, "[TOOL] → 拒绝：工具块不合语法：参数不是配平的 JSON 对象：{…}", "tool-face");
            flow.Append(SessionEventKind.AgentOutput, "[DONE] run.sh 修好了（退出码 0）");
        });

        var card = LifecyclePresenter.Card(report.Lifecycles[0]).Select(Markup.Strip).ToArray();

        Assert.Contains(card, l => l == "用户：把 run.sh 修好");                                   // ① 需求
        Assert.Contains(card, l => l.StartsWith("意图：", StringComparison.Ordinal)
            && l.Contains("run.sh 起不来", StringComparison.Ordinal));                             // ② 意图（模型自述）
        Assert.Contains(card, l => l == "打算：1 -- 读出全文");                                     // ② 打算
        Assert.Contains(card, l => l == "过程：");                                                 // ③ 过程发现
        Assert.Contains(card, l => l.Contains("被拒 1 次：工具块不合语法", StringComparison.Ordinal));
        Assert.Contains(card, l => l.StartsWith("动作：read×1", StringComparison.Ordinal));        // ④ 动作（聚合，不再平铺）
        // v8（主人 2026-09-24 19:30）：意图 / 打算 / 结果**累积** —— 原文在卡上（`/append off` 回到只给最新）。
        Assert.Contains(card, l => l == "结果：run.sh 修好了（退出码 0）");                          // ④ 终局正文（原有内容不变）
        Assert.Contains(card, l => l.StartsWith("风险：", StringComparison.Ordinal)
            && l.Contains("risk 声明 1 次", StringComparison.Ordinal)
            && l.Contains("草稿未决 1 条", StringComparison.Ordinal));                             // ⑤ 可能有问题

        // 缺数据的那几段**不画**（不编 —— 与 §十·64「认不出就不编」同一纪律）。
        var bare = Report(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "问一句");
            flow.Append(SessionEventKind.AgentOutput, "[DONE] 答完了");
        });
        var bareCard = LifecyclePresenter.Card(bare.Lifecycles[0]).Select(Markup.Strip).ToArray();
        Assert.DoesNotContain(bareCard, l => l.StartsWith("意图：", StringComparison.Ordinal));
        Assert.DoesNotContain(bareCard, l => l.StartsWith("打算：", StringComparison.Ordinal));
        Assert.DoesNotContain(bareCard, l => l == "过程：");
        Assert.DoesNotContain(bareCard, l => l.StartsWith("动作：", StringComparison.Ordinal));
        Assert.DoesNotContain(bareCard, l => l.StartsWith("风险：", StringComparison.Ordinal));
    }

    // ---------------- 2. 折叠不吞证据 ----------------

    [Fact]
    public void 默认折叠中间叙述_被拒调用存档不再常驻红字()
    {
        var lifecycle = Folded();
        var denied = lifecycle.Trace.Count(static e => e.NeverFold);
        var folded = lifecycle.Trace.Count - denied;

        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();

        // v8：模型散文**在卡上**（作为「结果」的一条 —— 主人要看得见思考过程）；
        // 只切到它的正文（行内 `[TOOL]` 也切掉），且**工具结果原文照旧不上卡**（只在 `/trace`）。
        Assert.Contains(plain, l => l.Contains("先看一下", StringComparison.Ordinal));
        Assert.DoesNotContain(plain, l => l.Contains("read run.sh → 42 行", StringComparison.Ordinal));

        // v14（主人 2026-09-22 01:1x）：被拒调用**不再常驻红字** —— 折叠态只给一句计数注记 + 指到哪里看原文。
        Assert.DoesNotContain(plain, l => l.StartsWith("拒绝 E005", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains($"被拒 {denied} 条", StringComparison.Ordinal) && l.Contains("/trace", StringComparison.Ordinal));

        // 但**决策点**照旧必须摆在屏上（它是人的行动项，不是证物）。
        Assert.Contains(plain, l => l == "  • 要不要顺便清一下临时文件");
        Assert.Contains(plain, l => l == $"（已折叠 {folded} 条执行轨迹 —— 原文永远在流里，没有丢）");
    }

    [Fact]
    public void 展开后轨迹一条不少且折叠行消失()
    {
        var lifecycle = Folded();
        var plain = LifecyclePresenter.Card(lifecycle, expanded: true).Select(Markup.Strip).ToArray();

        foreach (var entry in lifecycle.Trace)
        {
            Assert.Contains(plain, l => l.Contains(entry.Tag, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(plain, l => l.StartsWith("（已折叠", StringComparison.Ordinal));
        Assert.Contains(plain, l => l == $"完整轨迹（{lifecycle.Trace.Count} 条）");

        // v14：被拒的原文在**展开态里必须在**（存档 + 可查看），且仍带 warn 角色（颜色在展开处给）。
        var raw = LifecyclePresenter.Card(lifecycle, expanded: true);
        Assert.Contains(raw, l => l.Contains("[[warn]]", StringComparison.Ordinal) && l.Contains("ToolDenied", StringComparison.Ordinal));
    }

    // ---------------- 3. 决定型内容不截断 ----------------

    [Fact]
    public void 结果累积_每轮一行_多的行给_result指针()
    {
        // v8（主人 2026-09-24 19:4x 定）：「**有新结果就加上一行**」——
        // 结果一次**一行**（行尾带 `E###`）；该轮还有续行 ⇒ 给 `/result` 去处（**不截字**）。
        var prose = "先纠一条：我上轮的结论可能错，正在复核。\n\n## A 案（照主人批的写全）\n| 1 | ProtocolText.cs | 删 45 字符 |\n| 3 | MaxTokens doc | 改成真值 |";
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "A 案具体是什么样的？");
            flow.Append(SessionEventKind.AgentOutput, prose);
        });

        var on = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        Assert.Contains(on, l => l.StartsWith("结果：先纠一条", StringComparison.Ordinal)
            && l.Contains("另 4 行", StringComparison.Ordinal) && l.Contains("/result", StringComparison.Ordinal));
        Assert.DoesNotContain(on, l => l.Contains("MaxTokens doc", StringComparison.Ordinal));   // 续行不上卡（去 /result 看）

        // 关掉累积 ⇒ 只给最新一条（旧口径，正文照旧上卡）
        var off = LifecyclePresenter.Card(lifecycle, accumulate: false).Select(Markup.Strip).ToArray();
        Assert.Contains(off, l => l.StartsWith("结果：先纠一条", StringComparison.Ordinal));
        Assert.Contains(off, l => l.Contains("MaxTokens doc", StringComparison.Ordinal));
    }

    [Fact]
    public void 结果累积_轮数越多行越多()
    {
        // 同一份流、两种显示口径：累积 = N 轮各一条（+ 了结时终局正文一条）；关掉 = 只留最新。
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "干活");
            flow.Append(SessionEventKind.AgentOutput, "第一步：看文件");
            flow.Append(SessionEventKind.AgentOutput, "第二步：改文件");
            flow.Append(SessionEventKind.AgentOutput, "[DONE] 收工");
        });

        var on = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        var results = on.Where(l => l.StartsWith("结果：", StringComparison.Ordinal)).ToArray();

        Assert.Equal(3, results.Length);                      // 两轮正文 + 终局正文
        Assert.Contains(results, l => l.Contains("第一步：看文件", StringComparison.Ordinal));
        Assert.Contains(results, l => l.Contains("第二步：改文件", StringComparison.Ordinal));
        Assert.Contains(results, l => l.Contains("收工", StringComparison.Ordinal));
        Assert.Contains(results, l => l.Contains("E00", StringComparison.Ordinal));   // 每轮带事件锚（人指哪儿找哪儿）

        var off = LifecyclePresenter.Card(lifecycle, accumulate: false).Select(Markup.Strip).ToArray();
        Assert.Single(off.Where(l => l.StartsWith("结果：", StringComparison.Ordinal)));
    }

    [Fact]
    public void 请求与决策点全文上屏_不被截断()
    {
        var longRequest = new string('长', 200);
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, longRequest);
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 要不要继续（这句也不能被截）");
        });

        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();

        Assert.Contains(plain, l => l == $"用户：{longRequest}");
        Assert.Contains(plain, l => l == "  • 要不要继续（这句也不能被截）");
    }

    // ---------------- 4. 用量：不知道就说不知道 ----------------

    [Fact]
    public void 页脚有账给数字_没账给破折号()
    {
        var known = Card0(
            flow =>
            {
                flow.Append(SessionEventKind.UserInput, "问一句");
                flow.Append(SessionEventKind.AgentOutput, "[DONE] 答完了");
            },
            [new LifecycleTurnUsage(1_000, 900, 42, 1_500)]);

        Assert.Equal(
            "1 轮 · 工具 0 · 拒绝 0 · 风险声明 0 · +42 new tokens · 1.5s · 命中 90%",
            LifecyclePresenter.Footer(known));

        var unknown = Done();
        Assert.Contains("用量 —", LifecyclePresenter.Footer(unknown));
        Assert.DoesNotContain("0 new tokens", LifecyclePresenter.Footer(unknown));
    }

    // ---------------- 5. 总览 ----------------

    [Fact]
    public void 总览把每条用户消息列成一行()
    {
        var report = Report(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "第一件");
            flow.Append(SessionEventKind.AgentOutput, "[DONE] 好了");
            flow.Append(SessionEventKind.UserInput, "第二件");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 请选一个");
        });

        var plain = LifecyclePresenter.Summary(report).Select(Markup.Strip).ToArray();

        Assert.Contains(plain, l => l.StartsWith("生命周期总览", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.StartsWith("┌", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("✓ 得解", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("⏳ 等人", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("第一件", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("第二件", StringComparison.Ordinal));
    }

    // ---------------- 6. 决策卡（L3） ----------------

    [Fact]
    public void 决策卡_认出圈号选项并给出一眼能选的几行()
    {
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "帮我选一条路");
            flow.Append(SessionEventKind.AgentOutput, "两条路都行。\n[NEED-USER] 请二选一：① 改现有层；② 新建独立层");
        });

        var options = LifecyclePresenter.DecisionOptions(lifecycle);
        Assert.Equal(2, options.Count);
        Assert.Equal("改现有层", options[0].Text);
        Assert.Equal("新建独立层", options[1].Text);

        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        Assert.Contains(plain, l => l == "可选：");
        Assert.Contains(plain, l => l == "  1. 改现有层");
        Assert.Contains(plain, l => l == "  2. 新建独立层");
        Assert.Contains(plain, l => l.Contains("/decide 2", StringComparison.Ordinal));
    }

    [Fact]
    public void 决策卡_不是圈号枚举就不给可选项()
    {
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "选一个");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 请选：A) 改现有层 还是 B) 新建独立层");
        });

        // 认不出就不编造 —— 只在屏上照旧逐字给全文（[NEED-USER] 的正文只有一行，选项目录那种写法本就不在正文里）。
        Assert.Empty(LifecyclePresenter.DecisionOptions(lifecycle));
    }

    [Fact]
    public void 决策卡_认不出选项时不编造()
    {
        var lifecycle = Card0(flow =>
        {
            flow.Append(SessionEventKind.UserInput, "收尾");
            flow.Append(SessionEventKind.AgentOutput, "[NEED-USER] 请批准写权限");
        });

        Assert.Empty(LifecyclePresenter.DecisionOptions(lifecycle));
        var plain = LifecyclePresenter.Card(lifecycle).Select(Markup.Strip).ToArray();
        Assert.DoesNotContain(plain, l => l == "可选：");
        Assert.Contains(plain, l => l == "  • 请批准写权限");   // 全文照旧逐字上屏
    }

    [Fact]
    public void 空流的总览不炸且说清楚()
    {
        var plain = LifecyclePresenter
            .Summary(LifecycleAggregator.Aggregate(new SessionAppendStream()))
            .Select(Markup.Strip)
            .ToArray();

        Assert.Contains(plain, l => l.Contains("流里还没有用户消息", StringComparison.Ordinal));
    }
}

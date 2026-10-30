using System.Text;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Presentation;
using AgentRuntime.Hosting.Commands;

namespace AgentRuntime.Tui2;

/// <summary>
/// **自动验收**（设计 §七「自动（可复算）」一节的全部判据）—— 不碰终端、不联网，返回码 = 失败条数。
/// <para>
/// 为什么要把这些做成断言而不是「手工点点看」：CJK/emoji/组合符/320 行粘贴这四类，
/// 手工点**看不出来**（错一格也算「看着没问题」），只有逐字节比对才算证据。
/// </para>
/// </summary>
public static class SelfTest
{
    private static int _failures;

    public static int RunAll(TextWriter output)
    {
        _failures = 0;

        CellModelTests(output);
        PasteModelTests(output);
        InputDriverTests(output);
        EditorDocumentTests(output);
        TurnCardTests(output);
        StyleTests(output);
        CommandTests(output);
        SelectionTests(output);
        MeterTests(output);
        OutputWriterTests(output);

        output.WriteLine();
        output.WriteLine(_failures == 0
            ? "SELFTEST: PASS（全部断言通过）"
            : $"SELFTEST: FAIL（{_failures} 条失败）");
        return _failures;
    }

    // ---------- ① CellModel：宽度 / 换行 / 光标 ----------

    private static void CellModelTests(TextWriter o)
    {
        o.WriteLine("[CellModel]");
        Check(o, CellModel.Width("中") == 2, "CJK 宽度 = 2");
        Check(o, CellModel.Width("a") == 1, "ASCII 宽度 = 1");
        Check(o, CellModel.Width("🙂") == 2, "emoji 宽度 = 2");
        Check(o, CellModel.Width("e\u0301") == 1, "组合符 e+U+0301 宽度 = 1（不额外占格）");
        Check(o, CellModel.Width("\u200D") == 0, "零宽连接符 = 0");
        Check(o, CellModel.Width("中文abc") == 7, "混排宽度 = 2+2+1+1+1");
        Check(o, CellModel.Width("👨‍👩‍👧") == 2, "ZWJ 家庭 emoji 整体宽度 = 2（不是逐码位 6）");
        Check(o, CellModel.Width("🇨🇳") == 2, "旗帜 = 2（不是 4）");
        Check(o, CellModel.Width("1️⃣") == 2, "keycap = 2");

        var wrapped = CellModel.Wrap("中文中文中", 4);
        Check(o, wrapped.Count == 3, $"5 个 CJK 按宽 4 换行 = 3 行（实得 {wrapped.Count}）");
        Check(o, string.Concat(wrapped) == "中文中文中", "换行可往返（拼回等于原文）");

        var (row, col) = CellModel.Cursor("aaa中", 4, 4);
        Check(o, row == 1 && col == 2, $"光标 (4) 在宽 4 下 = (1,2)（实得 ({row},{col})）");

        var (row2, col2) = CellModel.Cursor("中", 1, 10);
        Check(o, row2 == 0 && col2 == 2, $"单 CJK 光标 = (0,2)（实得 ({row2},{col2})）");
    }

    // ---------- 选区 / 生命周期钟 ----------

    private static void SelectionTests(TextWriter o)
    {
        o.WriteLine("[Selection/Clock]");
        var rows = new List<string> { "abcdef", "中文🙂xy", "第三行" };
        Check(o, ScrollActions.IsScroll(InputAction.WheelUp) && ScrollActions.IsScroll(InputAction.WheelDown)
                 && ScrollActions.IsScroll(InputAction.ScrollUp) && ScrollActions.IsScroll(InputAction.ScrollDown),
            "滚轮/PgUp/PgDn 都属于「滚动」（空闲时也要重绘）");
        Check(o, !ScrollActions.IsScroll(InputAction.Submit), "提交不算滚动");

        Check(o, Selection.Extract(rows, (0, 1, 1, 4)) == "bcdef\n中文",
            "跨行选区：行内起点 + 行首到 4 列（CJK 整字）");

        var (slice, start) = Selection.SliceCells("ab中文", 2, 6);
        Check(o, slice == "中文" && start == 2, $"按显示列切 CJK（实得 [{slice}]@{start}）");

        var (half, halfStart) = Selection.SliceCells("ab中文", 3, 6);
        Check(o, half == "中文" && halfStart == 2, $"起点落在宽字中间 ⇒ 整字带走（实得 [{half}]@{halfStart}）");

        var clock = new TaskClock();
        clock.BeginLifecycle();
        Thread.Sleep(20);
        clock.Stop();
        clock.BeginLifecycle();
        Check(o, clock.ElapsedClock == "00:00", $"新生命周期读秒归零（实得 {clock.ElapsedClock}）");
        var label = clock.Label(7);
        Check(o, label.StartsWith("T7", StringComparison.Ordinal), $"Task 标签用 T（实得 [{label}]）");
        Check(o, !label.Contains("生命周期", StringComparison.Ordinal), "叫 Task、不叫「生命周期」（主人 2026-10-24 令）");
        clock.Stop();
    }

    // ---------- 写盘线程：主循环不许被终端堵住（2026-10-25 中文输入法卡死根因） ----------

    private static void OutputWriterTests(TextWriter o)
    {
        o.WriteLine("[Writer]");

        var gate = new object();
        var written = new List<string>();
        var screen = new AnsiScreen(s =>
        {
            Thread.Sleep(150);          // 模拟「终端在读 IME 组词、暂时不读 pty」
            lock (gate)
            {
                written.Add(s);
            }
        });

        var started = Environment.TickCount64;
        for (var i = 1; i <= 5; i++)
        {
            screen.Draw("frame-" + i, 24, 1, 1);
        }

        var elapsed = Environment.TickCount64 - started;
        Check(o, elapsed < 100, $"写盘慢时 Draw 必须立即返回（实得 {elapsed}ms）");

        Thread.Sleep(600);
        lock (gate)
        {
            Check(o, written.Count <= 2, $"同屏旧帧应被合并丢弃（实得写了 {written.Count} 次）");
            Check(o, written.Count > 0 && written[^1].Contains("frame-5", StringComparison.Ordinal),
                "最后一帧必须是屏幕最新状态（frame-5）");
        }
    }

    // ---------- 状态栏计数（T = 本 task / S = 整个 session） ----------

    private static void MeterTests(TextWriter o)
    {
        o.WriteLine("[TaskMeter]");
        var meter = new TaskMeter(TaskTurns: 11, TaskEvents: 52, SessionTurns: 206, SessionEvents: 1083);
        Check(o, meter.Counts() == TuiStrings.F(TuiKey.Meter, "11", "206"),
            $"T/S 计数格式（实得 [{meter.Counts()}]）");
        Check(o, !meter.Counts().Contains(TuiStrings.T(TuiKey.EventsLabel), StringComparison.Ordinal),
            "状态栏不报事件数（主人 2026-10-25：用户不需要这个数据）");
        Check(o, !meter.Counts().Contains("tok", StringComparison.Ordinal),
            "状态栏不报 session 的消耗（token 归顶栏，且须标 scope）");
        Check(o, meter.TaskTurns <= meter.SessionTurns && meter.TaskEvents <= meter.SessionEvents,
            "本 task 的计数不可能超过 session 总数（口径自洽）");
    }

    // ---------- ② PasteModel：原子块 ----------

    private static void PasteModelTests(TextWriter o)
    {
        o.WriteLine("[PasteModel]");
        var model = new PasteModel();

        var lines320 = string.Join("\n", Enumerable.Range(1, 320).Select(i => $"第 {i} 行 / line {i} 🙂"));
        var marker = model.Add(lines320);

        Check(o, model.IsMarker(marker), "marker 已登记");
        Check(o, !model.IsMarker('a'), "普通字符不是 marker");

        var editorText = $"前缀{marker}后缀";
        var expanded = model.Expand(editorText);
        Check(o, expanded == $"前缀{lines320}后缀", "Expand 逐字节还原（含 CJK/emoji/换行）");
        Check(o, expanded.Split('\n').Length == 320, "展开后仍是 320 行");
        Check(o, model.Expand(marker.ToString()) == lines320, "单 marker 展开 = 原文");

        var m2 = model.Add("x");
        Check(o, m2 != marker, "两段粘贴得到不同 marker");
        Check(o, model.Expand(new string(new[] { marker, m2 })) == lines320 + "x", "多 marker 顺序展开");
    }

    // ---------- ③ InputDriver：UTF-8 / 括号粘贴 / 分块 ----------

    private static void InputDriverTests(TextWriter o)
    {
        o.WriteLine("[InputDriver]");

        using var d1 = new Utf8InputDriver();
        var ev1 = d1.Feed("中文🙂e\u0301");
        var text1 = string.Concat(ev1.OfType<TextEvent>().Select(e => e.Text));
        Check(o, text1 == "中文🙂e\u0301", $"CJK+emoji+组合符逐字节无 U+FFFD（实得 [{text1}]）");
        Check(o, !text1.Contains('\uFFFD'), "不含替换字符 U+FFFD");

        var longLine = new string('中', 100);
        var ev2 = d1.Feed(longLine);
        Check(o, string.Concat(ev2.OfType<TextEvent>().Select(e => e.Text)) == longLine, "无换行 100 个汉字整块到齐");

        var chunk = Encoding.UTF8.GetBytes("中文");
        var a = d1.Feed(new[] { chunk[0] });
        var b = d1.Feed(chunk.Skip(1).ToArray());
        var joined = string.Concat(a.Concat(b).OfType<TextEvent>().Select(e => e.Text));
        Check(o, joined == "中文", "分块喂入（劈开 UTF-8 序列）仍能还原");

        using var d2 = new Utf8InputDriver();
        var paste = string.Join("\n", Enumerable.Range(1, 320).Select(i => $"行{i}"));
        var framed = "\u001b[200~" + paste + "\u001b[201~";
        var ev3 = d2.Feed(framed);
        var pasteEvent = ev3.OfType<PasteEvent>().SingleOrDefault();
        Check(o, pasteEvent is not null, "括号粘贴被识别为 PasteEvent");
        Check(o, pasteEvent?.Text == paste, "粘贴正文逐字节等于原文（320 行）");

        using var d3 = new Utf8InputDriver();
        var ev4 = d3.Feed("\u001b[200~没收口的一段");
        Check(o, ev4.OfType<PasteEvent>().Any() == false, "未闭合粘贴不提前交付（等 500ms 收口）");

        using var d4 = new Utf8InputDriver();
        var ev5 = d4.Feed("a\u001b[Ab\u001b[3~c");
        var keys = ev5.OfType<KeyEvent>().Select(k => k.Key).ToList();
        Check(o, keys.Contains(InputKey.Up), "ESC[A 解析成 Up");
        Check(o, keys.Contains(InputKey.Delete), "ESC[3~ 解析成 Delete");
        Check(o, string.Concat(ev5.OfType<TextEvent>().Select(e => e.Text)) == "abc", "方向键不污染文本");

        using var d5 = new Utf8InputDriver();
        var ev6 = d5.Feed("x\u007Fy");
        Check(o, ev6.OfType<KeyEvent>().Count(k => k.Key == InputKey.Backspace) == 1, "DEL 解析成 Backspace");

        var ev7 = d5.Feed("a\nb");
        Check(o, ev7.OfType<KeyEvent>().Any(k => k.Key == InputKey.Newline), "Ctrl+J (\\n) 解析成 Newline（不是提交）");

        var ev8 = d5.Feed("a\u001b\rb");
        Check(o, ev8.OfType<KeyEvent>().Any(k => k.Key == InputKey.Newline), "Alt+Enter (ESC CR) 解析成 Newline");

        var ev9 = d5.Feed("\t");
        Check(o, ev9.OfType<KeyEvent>().Any(k => k.Key == InputKey.TabPreview), "Tab (0x09) 解析成 TabPreview");

        var ev10 = d5.Feed("\u001b[9;1:3u");
        Check(o, ev10.OfType<KeyEvent>().Any(k => k.Key == InputKey.TabRelease), "Kitty ESC[9;1:3u 解析成 TabRelease");

        var ev11 = d5.Feed("\u001b[97;1:1u");
        Check(o, string.Concat(ev11.OfType<TextEvent>().Select(e => e.Text)) == "a", "Kitty 可打印码 97 回到文本 a（开协议不丢字）");

        var ev12 = d5.Feed("\u001b[57350;1:1u");
        Check(o, ev12.OfType<KeyEvent>().Any(k => k.Key == InputKey.Left), "Kitty 功能码 57350 解析成 Left");

        var wheelUp = d5.Feed("\u001b[<64;10;5M");
        Check(o, wheelUp.OfType<WheelEvent>().Any(e => e.Delta > 0), "SGR 滚轮上滚解析成 WheelEvent(+1)");
        var wheelDown = d5.Feed("\u001b[<65;10;5M");
        Check(o, wheelDown.OfType<WheelEvent>().Any(e => e.Delta < 0), "SGR 滚轮下滚解析成 WheelEvent(-1)");
    }

    // ---------- ④ 编辑器文档（无需终端：直接对 TextDocument 断言） ----------

    private static void EditorDocumentTests(TextWriter o)
    {
        o.WriteLine("[TextDocument]");
        var doc = new Terminal.Gui.Editor.Document.TextDocument(string.Empty);
        doc.Insert(0, "a中🙂");
        Check(o, doc.Text == "a中🙂", "文档往返（CJK+emoji）");
        Check(o, doc.TextLength == 4, $"UTF-16 长度 = 4（实得 {doc.TextLength}）");

        var bridgeText = doc.Text;
        var start = bridgeText.Length - char.ConvertFromUtf32(0x1F642).Length;
        doc.Remove(start, bridgeText.Length - start);
        Check(o, doc.Text == "a中", "emoji 整个删除（不拆代理对）");

        var familyDoc = new Terminal.Gui.Editor.Document.TextDocument("a👨‍👩‍👧");
        var familyBridge = new EditorBridge(familyDoc) { Caret = familyDoc.TextLength };
        familyBridge.BackspaceElement();
        Check(o, familyDoc.Text == "a", "Backspace 删整个 ZWJ 家庭 emoji（grapheme 边界）");

        var combiningDoc = new Terminal.Gui.Editor.Document.TextDocument("e\u0301x");
        var combiningBridge = new EditorBridge(combiningDoc) { Caret = combiningDoc.TextLength };
        combiningBridge.BackspaceElement();
        Check(o, combiningDoc.Text == "e\u0301", "Backspace 保留 e+组合符，只删 x");

        var ctrl = new InputController(new EditorBridge(new Terminal.Gui.Editor.Document.TextDocument(string.Empty)), new PasteModel());
        var wheelAction = ctrl.Handle(new WheelEvent(+1), out _);
        Check(o, wheelAction == InputAction.WheelUp, "滚轮上滚 ⇒ WheelUp（历史向上）");

        ctrl.Handle(new KeyEvent(InputKey.TabPreview), out _);
        Check(o, ctrl.TopBandVisible, "Tab 按一下 ⇒ 顶部模块区显示");
        ctrl.Handle(new KeyEvent(InputKey.TabRelease), out _);
        Check(o, ctrl.TopBandVisible, "key-release 不关闭（切换语义）");
        ctrl.Handle(new KeyEvent(InputKey.TabPreview), out _);
        Check(o, !ctrl.TopBandVisible, "Tab 再按一下 ⇒ 关闭");
    }

    // ---------- ⑤ 决策卡：按 turn 的「意图 / 操作 / 结果」 ----------

    private static void TurnCardTests(TextWriter o)
    {
        o.WriteLine("[TurnCard]");

        var trace = new List<LifecycleTraceEntry>
        {
            new("E001", SessionEventKind.AgentOutput, "我先说明。\n[TAIL]\nsolve: 做 A\nstep: 做 B"),
            new("E002", SessionEventKind.ToolResult, "ok", "exec"),
            new("E003", SessionEventKind.ToolResult, "{\"path\":\"a.txt\"}", "write"),
            new("E004", SessionEventKind.AgentOutput, "完成了。\n[TAIL]\nsolve: 做 B\nstep: 收尾"),
        };

        var life = new TaskLifecycle
        {
            Id = 1,
            UserMessages = ["请做 A"],
            AnswersId = null,
            Status = TaskLifecycleStatus.Done,
            Turns = 2,
            ToolCalls = 2,
            Denied = 0,
            RiskClaims = 0,
            TerminalConflicts = 0,
            Decisions = [],
            Result = "[DONE] 完成",
            FirstSeq = 1,
            LastSeq = 4,
            Usage = null,
            Trace = trace,
            ReportRequested = true,
            ReportText = string.Empty,
        };

        var lines = TurnCardView.Build(life);
        Check(o, lines.Count >= 6, $"两轮 = 两段（每段两行）+ 报告（实得 {lines.Count} 行）");
        var intent = TuiStrings.T(TuiKey.Intent);
        var result = TuiStrings.T(TuiKey.Result);
        var changed1 = TuiStrings.F(TuiKey.ChangedPaths, 1);
        Check(o, lines[0] == intent + "做 A[[dim]](exec×1 · write×1 · " + changed1 + ")[[/]]", $"第一段意图 + 灰色操作（实得 [{lines[0]}]）");
        Check(o, lines[1].StartsWith(result + "我先说明。", StringComparison.Ordinal), $"第一段结果（实得 [{lines[1]}]）");
        Check(o, lines[2] == string.Empty, "两段之间空一行");
        Check(o, lines[3] == intent + "做 B", $"第二段用本轮自己的 solve（实得 [{lines[3]}]）");
        Check(o, lines[4].StartsWith(result + "完成了。", StringComparison.Ordinal), $"第二段结果（实得 [{lines[4]}]）");
        Check(o, lines.Any(l => l.StartsWith("决策报告 #1", StringComparison.Ordinal)), "末尾出决策报告头");
        Check(o, lines.Any(l => l.Contains("未提交决策报告", StringComparison.Ordinal)), "未交报告只贴框（fail-closed）");

        var terminalOnly = new TaskLifecycle
        {
            Id = 2,
            UserMessages = ["测试"],
            AnswersId = null,
            Status = TaskLifecycleStatus.Done,
            Turns = 1,
            ToolCalls = 0,
            Denied = 0,
            RiskClaims = 0,
            TerminalConflicts = 0,
            Decisions = [],
            Result = "[DONE] 测试完成",
            FirstSeq = 5,
            LastSeq = 5,
            Usage = null,
            Trace = [new LifecycleTraceEntry("E005", SessionEventKind.AgentOutput, "[DONE] 测试完成")],
            ReportRequested = false,
            ReportText = string.Empty,
        };

        var proseOnly = new TaskLifecycle
        {
            Id = 3,
            UserMessages = ["请把 A 做完"],
            AnswersId = null,
            Status = TaskLifecycleStatus.Running,
            Turns = 1,
            ToolCalls = 1,
            Denied = 0,
            RiskClaims = 0,
            TerminalConflicts = 0,
            Decisions = [],
            Result = string.Empty,
            FirstSeq = 10,
            LastSeq = 12,
            Usage = null,
            Trace =
            [
                new LifecycleTraceEntry("E010", SessionEventKind.AgentOutput, "我先把 A 拆成两步，再逐步执行。"),
                new LifecycleTraceEntry("E011", SessionEventKind.ToolResult, "ok", "exec"),
            ],
            ReportRequested = false,
            ReportText = string.Empty,
        };

        var proseLines = TurnCardView.Build(proseOnly);
        Check(o, proseLines[0].StartsWith(TuiStrings.T(TuiKey.Intent) + "我先把 A 拆成两步，再逐步执行。[[dim]](exec×1)[[/]]", StringComparison.Ordinal),
            $"无 [TAIL] 时用模型正文首行当意图 + 操作挂行尾（实得 [{proseLines[0]}]）");
        Check(o, !proseLines[0].Contains("请把 A 做完", StringComparison.Ordinal), "意图不回填用户命令");

        var planned = new TaskLifecycle
        {
            Id = 4,
            UserMessages = ["看看现在在哪"],
            AnswersId = null,
            Status = TaskLifecycleStatus.Running,
            Turns = 1,
            ToolCalls = 1,
            Denied = 0,
            RiskClaims = 0,
            TerminalConflicts = 0,
            Decisions = [],
            Result = string.Empty,
            FirstSeq = 20,
            LastSeq = 22,
            Usage = null,
            Trace =
            [
                new LifecycleTraceEntry("E020", SessionEventKind.AgentOutput,
                    "[PLAN] 我先确认当前所在目录。\n[TOOL] <call><name>exec</name><command>pwd</command></call>"),
                new LifecycleTraceEntry("E021", SessionEventKind.ToolResult, "/tmp", "exec"),
            ],
            ReportRequested = false,
            ReportText = string.Empty,
        };

        var planLines = TurnCardView.Build(planned);
        Check(o, planLines[0] == TuiStrings.T(TuiKey.Intent) + "我先确认当前所在目录。[[dim]](exec×1)[[/]]",
            $"v87 [PLAN] 白话意图 + 灰色操作（实得 [{planLines[0]}]）");
        Check(o, planLines[1] == TuiStrings.T(TuiKey.Result) + "/tmp", $"工具轮结果取最后一条 ToolResult（实得 [{planLines[1]}]）");

        var reported = new TaskLifecycle
        {
            Id = 5,
            UserMessages = ["看看现在在哪"],
            AnswersId = null,
            Status = TaskLifecycleStatus.Done,
            Turns = 2,
            ToolCalls = 1,
            Denied = 0,
            RiskClaims = 0,
            TerminalConflicts = 0,
            Decisions = [],
            Result = "[DONE] 完成",
            FirstSeq = 30,
            LastSeq = 33,
            Usage = null,
            Trace =
            [
                new LifecycleTraceEntry("E030", SessionEventKind.AgentOutput,
                    "[PLAN] 我先跑一次 pwd 看当前目录。\n[TOOL] <call><name>exec</name><command>pwd</command></call>"),
                new LifecycleTraceEntry("E031", SessionEventKind.ToolResult, "/tmp", "exec"),
                new LifecycleTraceEntry("E032", SessionEventKind.AgentOutput,
                    "[RESULT] 我跑了 pwd，看到当前目录是 /tmp。\n[DONE] 完成"),
            ],
            ReportRequested = false,
            ReportText = string.Empty,
        };

        var reportedLines = TurnCardView.Build(reported);
        Check(o, reportedLines[1] == TuiStrings.T(TuiKey.Result) + "我跑了 pwd，看到当前目录是 /tmp。",
            $"v88 [RESULT] 覆盖拼装式工具结果（实得 [{reportedLines[1]}]）");
        Check(o, reportedLines[4].StartsWith(TuiStrings.T(TuiKey.Result) + "完成", StringComparison.Ordinal),
            $"终局轮结果正常（实得 [{reportedLines[4]}]）");

        var decorated = TurnCardView.DecorateReport(
        [
            "决策报告 #1 ✓ 得解",
            "术语",
            "  AppRuntime = 运行时本体；中文也叫「运行时」。",
            "一、这一轮要解决什么",
            "  把 AppRuntime 的读数报出来。",
            "二、怎么解的",
            "  2.1 先看 AppRuntime 的状态",
        ]);
        Check(o, decorated.Contains("[[keyword]]术语[[/]]"), "「术语」标题用金（Keyword）");
        Check(o, decorated.Contains("[[keyword]]一、这一轮要解决什么[[/]]"), "章节标题用金（Keyword）");
        Check(o, decorated.Any(l => l.Contains("[[keyword]]AppRuntime[[/]]")), $"术语名用金（实得 [{string.Join(" | ", decorated)}]）");
        Check(o, decorated.Any(l => l.Contains("[[keyword]]AppRuntime[[/]] 的读数")), "正文里的概念也染色");
        var idxOne = decorated.ToList().IndexOf("[[keyword]]一、这一轮要解决什么[[/]]");
        Check(o, idxOne > 0 && decorated[idxOne - 1] == string.Empty, "段落之间空一行");

        // 意图 / 结果里的关键词（2026-10-25 主人报「卡上一直没看到 keyword」）：术语名必须也染到那两行。
        var wrapped = TurnCardView.WrapTerms("查清 科目浮层 的结构，别动 `科目浮层` 那段代码", ["科目浮层"]);
        Check(o, wrapped.Contains("[[keyword]]科目浮层[[/]] 的结构", StringComparison.Ordinal),
            $"意图 / 结果里出现关键词（实得 [{wrapped}]）");
        Check(o, wrapped.Contains("`科目浮层`", StringComparison.Ordinal),
            "反引号里的代码段不嵌关键词标记");

        var terminalLines = TurnCardView.Build(terminalOnly);
        Check(o, terminalLines.Count == 2, $"只有终局输出也成一段 · 两行（实得 {terminalLines.Count} 行）");
        Check(o, terminalLines[1] == TuiStrings.T(TuiKey.Result) + "测试完成", $"终局结果不被 ProseCut 吃掉、且不重复追加 E###（实得 [{terminalLines[1]}]）");
    }

    // ---------- ⑥ 配色：直接复用 TUI1 的 StyleTable ----------

    private static void StyleTests(TextWriter o)
    {
        o.WriteLine("[Style]");

        var intent = Tui2Style.Paint("意图：做 A", StyleTable.Standard);
        Check(o, intent.Contains("\u001b[38;5;117m", StringComparison.Ordinal), "意图用 Attention 色（TUI1 色号 117）");
        Check(o, intent.Contains("\u001b[0m", StringComparison.Ordinal), "SGR 有复位");

        var heading = Tui2Style.Paint("一、这一轮要解决什么", StyleTable.Standard);
        Check(o, heading.Contains("38;5;221", StringComparison.Ordinal), "章节标题用 Keyword 金（色号 221）");
        var concept = Tui2Style.Paint("[[keyword]]AppRuntime[[/]] 是运行时", StyleTable.Standard);
        Check(o, concept.Contains("38;5;221", StringComparison.Ordinal), "内联概念用 Keyword 金");

        var result = Tui2Style.Paint("结果：完成（E046）", StyleTable.Standard);
        Check(o, result.Contains("\u001b[38;5;115m", StringComparison.Ordinal), "结果用 Done 色（TUI1 色号 115）");
        Check(o, result.Contains("\u001b[38;5;102m", StringComparison.Ordinal), "E### 锚用 Dim 色（TUI1 色号 102）");

        var report = Tui2Style.Paint("决策报告 #1 ✓ 得解", StyleTable.Standard);
        Check(o, report.Contains("\u001b[1m", StringComparison.Ordinal), "报告标题用 Strong");
        Check(o, report.Contains("\u001b[38;5;115m", StringComparison.Ordinal), "得解徽章用 Done 色");

        var divider = Tui2Style.Paint("─ 编辑 ─────────", StyleTable.Standard);
        Check(o, divider.Contains("\u001b[38;5;102m", StringComparison.Ordinal), "编辑分隔线用 Dim 色");

        var status = Tui2Style.Paint("[就绪] 轮 0 · 事件 1", StyleTable.Standard);
        Check(o, status.Contains("\u001b[38;5;102m", StringComparison.Ordinal), "底部状态条用 Dim 色");

        var user = Tui2Style.ToRich("you > 你好");
        Check(o, user.Text == "you > 你好" && user.Spans.Count == 1 && user.Spans[0].Role == StyleRole.You,
            "用户行 `you > ` 整行 You（斜体）");

        var tui2 = Tui2Style.Detect("always", "256");
        var operation = Tui2Style.Paint("操作：exec×1", tui2);
        Check(o, operation.Contains("\u001b[38;5;71m", StringComparison.Ordinal), "操作（指令/工具）用 Command 薄荷（色号 71）");

        var path = Tui2Style.ToRich("见 /home/user/a.cs");
        Check(o, path.Spans.Any(sp => sp.Role == StyleRole.Path), "绝对路径自动标成 Path");
        var pathPainted = Tui2Style.Paint("见 /home/user/a.cs", tui2);
        Check(o, pathPainted.Contains("\u001b[38;5;221m", StringComparison.Ordinal), "TUI2 路径回到金（色号 221）");
        var ocPath = Tui2Style.Paint("见 /home/user/a.cs", StyleTable.Standard);
        Check(o, ocPath.Contains("\u001b[38;5;222m", StringComparison.Ordinal), "OC 的表不受影响（仍是 222 土黄）");

        var frame1 = ThinkSnake.Frame(0);
        var frame2 = ThinkSnake.Frame(300);
        Check(o, frame1.Length == 8, $"思考动画恒为 8 格（实得 {frame1.Length}）");
        Check(o, frame1 != frame2, "思考动画随时间变化");
        Check(o, frame1.All(ch => ch == ' ' || (ch >= '\u2800' && ch <= '\u28FF')), "思考动画只含 Braille/空格");

        var thinking = Tui2Style.ToRich("[运行中] 轮 1 · 远端模型正在思考… " + frame1);
        Check(o, thinking.Spans.Count == 1 && thinking.Spans[0].Role == StyleRole.Dim, "思考行整行 Dim（状态栏灰色）");

        var wideTable = new List<string>
        {
            "| 类别 | 发现 | 锚 |",
            "|---|---|---|",
            "| 发现 | " + new string('x', 80) + " | E001 |",
        };
        var fitted = TableReflow.Fit(wideTable, 40);
        Check(o, fitted.All(line => CellModel.Width(line) <= 40), "决策报告表格按终端宽度重排（不超宽）");
        Check(o, fitted.Count >= 3, "表格重排后仍有内容");

        var off = Tui2Style.Paint("意图：做 A", StyleTable.Disabled);
        Check(o, off == "意图：做 A", $"关闭配色时逐字等于原文（实得 [{off}]）");
    }

    // ---------- ⑦ 面板命令识别 ----------

    private static void CommandTests(TextWriter o)
    {
        o.WriteLine("[Command]");
        Check(o, PanelRouter.IsCommand("/reset"), "/reset 被识别为命令");
        Check(o, PanelRouter.IsCommand("/reset --start"), "/reset --start 被识别为命令");
        Check(o, PanelRouter.IsCommand("/help"), "/help 被识别为命令");
        Check(o, !PanelRouter.IsCommand("/home/user/file.cs"), "绝对路径仍当普通文本（不被命令吃掉）");
        Check(o, !PanelRouter.IsCommand("//help"), "//help 是转义文本，不是命令");
        Check(o, PanelRouter.Unescape("//help") == "/help", "//help 发送时还原成 /help");
    }

    private static void Check(TextWriter o, bool ok, string what)
    {
        if (!ok) _failures++;
        o.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
    }
}
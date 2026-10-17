using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **<c>[TOOL]</c> 解析 + 工具本体（read / list / write / edit / exec）的闸门测试**。
/// <para>
/// 只测两件事：<b>解析只做机械活</b>（一次回复最多 4 次、按序全取 / JSON-in-text 要严谨 / 不猜不纠错），
/// 以及<b>每个工具的边界判据</b>（越界、不唯一、白名单、上限）。审批与事件流在 <c>ToolFaceTests</c>。
/// </para>
/// <para><b>负例必须有牙</b>：每条闸门都配一个「故意做坏 ⇒ 必须红」的反例。</para>
/// </summary>
public sealed class ToolReportTests
{
    [Fact]
    public void 未捕获异常_落盘一份现场_且永不自己抛()
    {
        // 2026-09-22 03:49 真机 SIGABRT：只留了一个 .ips（全是原生帧，没有托管异常名与栈），
        // 而 TUI 退出时恢复终端 ⇒ 屏上那段 `Unhandled exception …` 一滚就没了 —— 手里只剩「它崩了」。
        // 这条闸门钉两件事：① 现场真的落盘（异常全文 + 会话指针 + 流末）；② **落盘自己不许抛**。
        using var work = new SnapshotTestWorkspace("crash-dump");
        var target = Path.Combine(work.Root, "last-crash.txt");

        // 指针与流都放在临时目录（**不碰真实 ~/.agentruntime**）：判据才可复算。
        var realStream = Path.Combine(work.Root, "stream.xml");
        StreamFixtures.Write(realStream,
            StreamFixtures.Fragment(1, "UserInput", "崩溃前最后一句", tag: "E001"),
            StreamFixtures.Fragment(2, "AgentOutput", "崩溃前最后一段", tag: "E002"));
        var pointerPath = Path.Combine(work.Root, "session.xml");
        new AgentRuntime.Core.Frozen.SessionPointerStore(pointerPath).Save(
            new AgentRuntime.Core.Frozen.SessionPointer(realStream, false, DateTimeOffset.Now, 1, null));

        AgentRuntime.Tui.CrashDump.Write(new InvalidOperationException("闸门用的假异常"), "/tmp/fake.json", target, pointerPath);

        Assert.True(File.Exists(target));
        var text = File.ReadAllText(target);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("闸门用的假异常", text, StringComparison.Ordinal);
        Assert.Contains("## 会话指针", text, StringComparison.Ordinal);
        Assert.Contains("## 流末", text, StringComparison.Ordinal);
        Assert.Contains("崩溃前最后一句", text, StringComparison.Ordinal);
        Assert.Contains("/tmp/fake.json", text, StringComparison.Ordinal);

        // ② 落点写不进去（拿目录当文件）⇒ 不抛（诊断器不能变成第二个崩溃源）。
        var asDirectory = Path.Combine(work.Root, "adir");
        Directory.CreateDirectory(asDirectory);
        AgentRuntime.Tui.CrashDump.Write(new InvalidOperationException("x"), null, asDirectory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- 解析 ----------------

    [Fact]
    public void 解析_没有块就是零动作()
    {
        foreach (var text in new[] { null, "", "   ", "普通回复，没有工具块。", "[TOOLISH] read {}" })
        {
            var result = ToolReport.Parse(text);
            Assert.Equal(ToolParseStatus.None, result.Status);
            Assert.Null(result.Call);
            Assert.Equal(0, result.Count);
        }
    }

    [Fact]
    public void 解析_漏写块头的工具元素_响亮拒而不是零动作()
    {
        // 2026-10-11 真机 M1：模型重发的块是裸 <call>…</call>（**漏了 [TOOL] 块头**）
        // ⇒ 旧路 Parse=None ⇒ ToolRunner 走 Nothing（**零事件**）⇒ 模型判「工具结果通道没接线」，
        // 烧 8 轮后交 [NEED-USER]（模型本身没错，错只是漏写了一个标记）。
        foreach (var text in new[]
                 {
                     "重发一个合规元素。\n\n<call><name>read</name><path>/tmp/a.txt</path></call>",
                     "<read><path>/tmp/a.txt</path><maxLines>3</maxLines></read>",
                     "先说明，再给块：\n<call><name>exec</name><command>echo hi</command></call>\n\n[TAIL]\nsolve: x",
                 })
        {
            var result = ToolReport.Parse(text);
            Assert.Equal(ToolParseStatus.Malformed, result.Status);
            Assert.Equal(0, result.Calls.Count);
            Assert.NotNull(result.Error);
            Assert.Contains("块头", result.Error, StringComparison.Ordinal);
        }

        // 负例（不许误伤）：「引述」不算「真调用」：
        // ① 只提标签名（没配对元素）；② **配对了但没有 <name> 也没有参数**（正文里引述一个形状）。
        foreach (var prose in new[]
                 {
                     "元素可以是 <call> 或工具名本身 —— 详见协议。",
                     "形状是 `<call>…</call>`，刚才漏了那个标记。",
                     "把 <read> 换成 <list> 即可。",
                 })
        {
            Assert.Equal(ToolParseStatus.None, ToolReport.Parse(prose).Status);
        }
    }

    [Fact]
    public void 解析_单次调用_名字与参数原样取回()
    {
        var result = ToolReport.Parse("我先看一下。\n[TOOL] read {\"path\":\"docs/a.md\",\"maxLines\":20}\n");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(1, result.Count);
        Assert.Equal("read", result.Call!.Name);
        Assert.Equal("{\"path\":\"docs/a.md\",\"maxLines\":20}", result.Call.ArgumentsJson);
        Assert.Equal("read {\"path\":\"docs/a.md\",\"maxLines\":20}", result.Call.Raw);
    }

    [Fact]
    public void 解析_零参调用_登记过的工具要跑得通()
    {
        // 用户 2026-09-29 19:56 令（缺口 A②）：`[TOOL] index`（行式、零参）此前被当「空块」拒。
        // 判据：登记过的工具、零参数必须落 Ok（至于这把工具要不要参数，由工具自己响亮报）。
        var result = ToolReport.Parse("[TOOL] index");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(1, result.Count);
        Assert.Equal("index", result.Call!.Name);
        Assert.Equal(string.Empty, result.Call.ArgumentsJson);
    }

    [Fact]
    public void 解析_空块_没有名字仍必拒()
    {
        // 反面守住：放宽零参**不许**放过「连名字都没有」的块。
        var result = ToolReport.Parse("[TOOL]");

        Assert.Equal(ToolParseStatus.Malformed, result.Status);
        Assert.Contains("空块", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析_多次调用_按发出顺序全取回()
    {
        // v14（用户 2026-09-22 定）：一次回复允许最多 ProtocolText.ToolMaxCallsPerReply 次调用 ——
        // 为什么：真机实测一场简单提问 9 轮，其中 4 轮是「读一点、看一点、再读一点」，每轮都要重发整份上下文。
        var result = ToolReport.Parse(
            "[TOOL] read {\"path\":\"a\"}\n[TOOL] write {\"path\":\"b\",\"content\":\"x\"}");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Count);
        Assert.Equal(["read", "write"], result.Calls.Select(static c => c.Name));   // 顺序 = 发出顺序
        Assert.True(result.HasCalls);
        Assert.Null(result.Call);                                                   // 多调用时没有「那一个」
    }

    [Fact]
    public void 解析_超过执行额度不再整篇拒_改由宿主排队()
    {
        // 2026-09-25 口径变更：解析器只守**硬上限**（防炸）；「每轮跑几个」是宿主的**执行额度**
        // （超出 ⇒ 排队，见 ToolQueueTests）。旧口径（>4 ⇒ 整篇拒）会把「模型偶尔多发几个」
        // 升级成「那一轮的动作全丢」，重发还要再买一轮整份上下文。
        var cap = ProtocolText.ToolMaxCallsPerReply;
        var text = string.Join('\n', Enumerable.Range(0, cap + 1).Select(i => $"[TOOL] read {{\"path\":\"f{i}\"}}"));

        var result = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(cap + 1, result.Count);
        Assert.Equal(cap + 1, result.Calls.Count);        // 全取回（按发出顺序）；谁跑谁排队由宿主定
    }

    [Fact]
    public void 解析_超过硬上限_整篇拒()
    {
        // 硬上限是**防炸**的安全边界，与执行额度不是同一个数（fail-closed 不变）。
        var hardCap = ToolReport.ToolParseAbsoluteMax;
        var text = string.Join('\n', Enumerable.Range(0, hardCap + 1).Select(i => $"[TOOL] read {{\"path\":\"f{i}\"}}"));

        var result = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.TooMany, result.Status);
        Assert.Empty(result.Calls);
        Assert.Contains("解析硬上限", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析_多调用里任一块不合语法_整篇拒且一块都不跑()
    {
        // fail-closed 的延伸：半执行的状态比不做更难收拾 ⇒ 一个块坏 ⇒ 全拒，返空清单。
        var result = ToolReport.Parse("[TOOL] read {\"path\":\"a\"}\n[TOOL] read {\"path\"");

        Assert.Equal(ToolParseStatus.Malformed, result.Status);
        Assert.Empty(result.Calls);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 解析_尾随文字不算参数_但括号不配平即拒绝()
    {
        // 尾随解释：切出配平的那一段 JSON（容错只发生在这里，不改写 JSON 本身）。
        var ok = ToolReport.Parse("[TOOL] read {\"path\":\"a.txt\"} 先看看这个文件");
        Assert.Equal(ToolParseStatus.Ok, ok.Status);
        Assert.Equal("{\"path\":\"a.txt\"}", ok.Call!.ArgumentsJson);

        // 字符串里的括号不算括号（否则 {"content":"}"} 会被切成半个对象）。
        var tricky = "{\"path\":\"a\",\"content\":\"}\"}";
        var nested = ToolReport.Parse("[TOOL] write " + tricky);
        Assert.Equal(ToolParseStatus.Ok, nested.Status);
        Assert.Equal(tricky, nested.Call!.ArgumentsJson);
        // 反例 1：大括号不配平。
        Assert.Equal(ToolParseStatus.Malformed, ToolReport.Parse("[TOOL] read {\"path\":\"a\"").Status);

        // 反例 2：参数不是对象。
        Assert.Equal(ToolParseStatus.Malformed, ToolReport.Parse("[TOOL] read [\"a\"]").Status);

        // 反例 3：只写块头没写动作。
        Assert.Equal(ToolParseStatus.Malformed, ToolReport.Parse("[TOOL]").Status);
    }

    [Fact]
    public void 解析_参数跨行也能取到_但遇到下一个块头即停()
    {
        // B1：参数跨行 = **多行块**（`content<<` … `>>`），不再靠 JSON 括号配平。
        var result = ToolReport.Parse("[TOOL] write\npath: a.txt\ncontent<<\n正文\n>>\n[FOCUS] E001");
        Assert.Equal(ToolParseStatus.Ok, result.Status);
        var args = ToolArgs.Parse(result.Call!.ArgumentsJson);
        Assert.Equal("正文", args.RequireString("content"));

        // 参数段里夹了别的块头 ⇒ 不吞（PITFALLS #30 的同一类错）。
        var malformed = ToolReport.Parse("[TOOL] write\n[TAIL] 任务A");
        Assert.Equal(ToolParseStatus.Malformed, malformed.Status);
    }

    [Fact]
    public void 解析_真机形状_多行正文塞进JSON_必须报出_空行与换行_这一事实()
    {
        // 2026-09-22 真机（坑 #130，五份夹具同形）：模型把多行正文直接塞进 JSON 字符串。
        // 旧话术只答「JSON 的键要带引号」⇒ 被拒的模型改不对（实测近两卷 5/5 被拒全是这一类）。
        // 闸门：报错说**解析器观察到的事实** + 一条可执行修法，不把病因猜成「引号」。
        var q = '"';
        var blankInside = "[TOOL] write {" + q + "path" + q + ":" + q + "/tmp/x.txt" + q + ","
            + q + "content" + q + ":" + q + "第一行" + "\n\n- 落点：区栈与白板之间" + q + "} risk: none";

        // B1（2026-09-29 用户定案）：**旧 JSON 形状已退役**。切段仍在（解析器只切形状），
        // 但**真值判定**落在 ToolArgs.Parse —— 它必须点明「没转义的换行」并指出行式出路。
        var parsed = ToolReport.Parse(blankInside);
        Assert.Equal(ToolParseStatus.Ok, parsed.Status);

        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(parsed.Call!.ArgumentsJson));
        Assert.Contains("没转义的换行", ex.Message, StringComparison.Ordinal);
        Assert.Contains("元素形状", ex.Message, StringComparison.Ordinal);
        Assert.Contains("写到文件", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("键要带引号", ex.Message, StringComparison.Ordinal);

        // 另一半：没有空行、换行落在字符串里（大括号仍配平）⇒ 解析层放过，到 `ToolArgs` 才被 JSON 拒。
        var noBlank = "[TOOL] write {" + q + "path" + q + ":" + q + "/tmp/y" + q + ","
            + q + "content" + q + ":" + q + "第一行" + "\n" + "第二行" + q + "} risk: none";
        var parsed2 = ToolReport.Parse(noBlank);
        Assert.Equal(ToolParseStatus.Ok, parsed2.Status);
        var ex2 = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(parsed2.Call!.ArgumentsJson));
        Assert.Contains("没转义的换行", ex2.Message, StringComparison.Ordinal);
    }

    // ---------------- 参数体检（ToolArgs） ----------------

    [Fact]
    public void 参数_重复键与未声明键与非对象一律拒()
    {
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("{\"path\":\"a\",\"path\":\"b\"}"));   // 重复键
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("\"a\""));                            // 不是对象
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(""));                                 // 空
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("{path:a}"));                         // 非法 JSON

        var args = ToolArgs.Parse("{\"path\":\"a\"}");
        args.EnsureOnly("path", "maxLines");                                                     // 已声明的键：通过
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("{\"pth\":\"a\"}").EnsureOnly("path")); // 未声明的键
    }

    [Fact]
    public void 参数_摘要只依赖工具名与规范化参数()
    {
        var a = ToolArgs.Parse("{\"path\":\"a\",\"maxLines\":2}");
        var b = ToolArgs.Parse("{ \"maxLines\": 2, \"path\": \"a\" }");   // 键序 / 空白不同，语义相同

        Assert.Equal(a.Canonical, b.Canonical);
        Assert.Equal(a.Digest("read"), b.Digest("read"));
        Assert.NotEqual(a.Digest("read"), a.Digest("list"));              // 同名不同工具 ⇒ 不同键
    }

    // ---------------- 路径真身（S1；**不做围栏** —— 用户 2026-09-16 03:00 定） ----------------

    [Fact]
    public void 路径_不做围栏_任意绝对路径都算出真身()
    {
        // 不再有"根"这个概念：能不能动由**审批**说了算，不由路径说了算 —— 系统目录也要能算出真身。
        Assert.True(File.Exists(ToolPaths.Normalize("/etc/passwd")));

        using var work = new SnapshotTestWorkspace("tool-paths");
        var file = Path.Combine(work.Root, "sub", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        // 相对路径 ⇒ 以 cwd 为基准；`.` / `..` 归一。
        Assert.Equal(Path.GetFullPath("./a.txt"), ToolPaths.Normalize("./a.txt"));
        // ⚠️ 期望值**不能**用 Path.GetFullPath 算：那是**另一套算法**（只做词法规范化、不解符号链接），
        // 而 macOS 上 temp 根（/var）本身是符号链接 ⇒ 两种拼法同时为真（PITFALLS #139 / §十·54：只比字符串必假）。
        // 判据改用**语义行为**：`..` 被解开后，与直接写目标名归一为**同一条真身**。
        Assert.Equal(
            ToolPaths.Normalize(Path.Combine(Path.GetTempPath(), "y")),
            ToolPaths.Normalize(Path.Combine(Path.GetTempPath(), "x", "..", "y")));

        // `..` 不被"拦住"，而是被**解开**：给出的就是真身（审批面看到的就是它）——这正是 S1 要防的"审批欺骗"。
        var up = ToolPaths.Normalize(Path.Combine(work.Root, "sub", "..", "a.txt"));
        Assert.Equal(ToolPaths.Normalize(work.Root), Path.GetDirectoryName(up));   // 两侧同口径（真身），不比字面拼法（§十·54）
        Assert.DoesNotContain("..", up, StringComparison.Ordinal);

        // ~ 展开到主目录真身。
        Assert.Equal(ToolPaths.HomeDirectory(), ToolPaths.Normalize("~"));
        Assert.Equal(Path.Combine(ToolPaths.HomeDirectory(), "x"), ToolPaths.Normalize("~/x"));

        // 符号链接 ⇒ **解析到最终目标**：同一个文件不许有两个"路径身份"（那正是审批欺骗想要的效果）。
        var real = Path.Combine(work.Root, "real.txt");
        File.WriteAllText(real, "x");
        var link = Path.Combine(work.Root, "link.txt");
        File.CreateSymbolicLink(link, real);
        Assert.Equal(ToolPaths.Normalize(real), ToolPaths.Normalize(link));
    }

    [Fact]
    public void 路径_判不出真身即拒绝_绝不猜()
    {
        Assert.Throws<ToolUsageException>(() => ToolPaths.Normalize(""));
        Assert.Throws<ToolUsageException>(() => ToolPaths.Normalize("   "));
        Assert.Throws<ToolUsageException>(() => ToolPaths.Normalize("~nobody/x"));   // ~user 判不出 ⇒ 拒
    }

    // ---------------- 工具本体 ----------------

    [Fact]
    public async Task 读_有行数上限且截断必须明说()
    {
        using var work = new SnapshotTestWorkspace("tool-read");
        var path = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(path, "1\n2\n3\n4\n5\n", Ct);

        var limits = ToolLimits.Default;
        var read = new ReadTool();
        var context = new ToolContext(limits, 0, null);

        var argsJson = $"{{\"path\":\"{path}\"}}";
        var args = ToolArgs.Parse(argsJson);
        var all = await read.ExecuteAsync(context, args, Ct);
        Assert.True(all.Ok);
        // B1：结果头的参数段 = **规范化行式**（Canonical 是唯一口径）。
        Assert.StartsWith($"read {args.Canonical} → 5 行", all.Text, StringComparison.Ordinal);
        Assert.Contains("5", all.Text, StringComparison.Ordinal);

        var clipped = await read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"maxLines\":2}}"), Ct);
        Assert.True(clipped.Truncated);
        Assert.Contains("2/5 行", clipped.Text, StringComparison.Ordinal);
        Assert.Contains("截断", clipped.Text, StringComparison.Ordinal);

        // 反例：maxLines 给了 0 ⇒ 拒（不静默当默认）。
        await Assert.ThrowsAsync<ToolUsageException>(() =>
            read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"maxLines\":0}}"), Ct).AsTask());
    }

    [Fact]
    public async Task 读_offset_定点读后段_截断给出下一段起点()
    {
        using var work = new SnapshotTestWorkspace("tool-read-offset");
        var path = Path.Combine(work.Root, "long.txt");
        await File.WriteAllTextAsync(path, "1\n2\n3\n4\n5\n", Ct);

        var read = new ReadTool();
        var context = new ToolContext(ToolLimits.Default, 0, null);

        // 第 3 行起 ⇒ 只拿 3~5（前两行**不许**漏出来）
        var tail = await read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"offset\":3}}"), Ct);
        Assert.True(tail.Ok);
        Assert.Contains("第 3~5 行 / 共 5 行", tail.Text, StringComparison.Ordinal);
        Assert.EndsWith("3\n4\n5", tail.Text, StringComparison.Ordinal);

        // offset + limit（与 [OC] 同名）⇒ 截断并把**下一段起点**写进结果
        var clipped = await read.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"offset\":2,\"limit\":2}}"), Ct);
        Assert.True(clipped.Truncated);
        Assert.Contains("第 2~3 行 / 共 5 行", clipped.Text, StringComparison.Ordinal);
        Assert.Contains("用 offset=4 接着读", clipped.Text, StringComparison.Ordinal);

        // 越过文件末尾 ⇒ 是**结果**（不是拒绝），且明说为空
        var beyond = await read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"offset\":9}}"), Ct);
        Assert.True(beyond.Ok);
        Assert.Contains("为空", beyond.Text, StringComparison.Ordinal);

        // 反例：offset=0 ⇒ 拒（offset 从 **1** 起）
        await Assert.ThrowsAsync<ToolUsageException>(() =>
            read.ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"offset\":0}}"), Ct).AsTask());
    }

    [Fact]
    public async Task 读_字符窗截断_头部行号等于实际可见行数且指针给下一行()
    {
        // 2026-09-22（用户令 A）：单条结果上限 8,000 → 2,000 字符。降上限的前提是**指针准** ——
        // 旧写法先取行窗、再用字符窗砍正文 ⇒ 头部写「400/632 行」而实际只看得到 ~240 行，
        // 模型按头部下结论就会漏内容（PITFALLS「两层截断别拿一层当另一层」）。
        using var work = new SnapshotTestWorkspace("tool-read-char-cap");
        var path = Path.Combine(work.Root, "long.txt");
        await File.WriteAllLinesAsync(
            path, Enumerable.Range(1, 200).Select(i => $"{i:D3} 这是一行用来把窗口撑到字符上限的文字"), Ct);

        var context = new ToolContext(new ToolLimits { MaxOutputChars = 400 }, 0, null);   // 窗口读用 400；整读上限被抬到 40,000
        var result = await new ReadTool().ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\"}}"), Ct);

        Assert.True(result.Truncated);
        var lines = result.Text.Split('\n');
        var head = lines[0];
        var shown = int.Parse(head[(head.IndexOf('→') + 1)..].Trim().Split('/')[0]);   // 头部声称的行数
        var visible = lines.Skip(1).Count(static l => l.Contains("这是一行", StringComparison.Ordinal));
        Assert.Equal(shown, visible);                                                  // 头部行号 == 实际可见量

        // C1（2026-09-26）：指针必须是**能走通的** —— read 的「全文」就是磁盘上这个文件本身，
        // 所以真指针 = 同文件 + 下一个 offset；旧话术里的「全文在 /trace」是**假指针**
        // （/trace 是 TUI 看生命周期轨迹的命令，拿不到这条结果的原文），必须消失。
        Assert.Contains("接着读同一文件", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("全文在 /trace", result.Text, StringComparison.Ordinal);

        // 指针可走通：照它给的 offset 读，能真的拿到下一段（且与上一段不重叠）。
        var next = await new ReadTool().ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{path}\",\"offset\":{visible + 1}}}"), Ct);
        Assert.True(next.Ok);
        // B1（2026-09-29）：回显现在是**行式 canonical**（键按序排、每行一条）—— 意图不变：头部 = 规范调用 + 区间。
        Assert.StartsWith($"read offset: {visible + 1}\npath: {path} → 第 {visible + 1}~", next.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 读写的失败话术_说清错在哪并带上基准目录()
    {
        // 2026-09-22 实测：一道简单提问里 **17 次**工具返回是失败，多是把**目录**当文件读（`read "…/projects/AgentRuntime"`）
        // 而旧话术只说「文件不存在」⇒ 模型以为路径写错，又猜了 5 轮路径。错要说得具体，并给出相对路径的基准。
        using var work = new SnapshotTestWorkspace("tool-fail-message");
        var context = new ToolContext(ToolLimits.Default, 0, null);
        var dir = Path.Combine(work.Root, "sub");
        Directory.CreateDirectory(dir);

        var readDir = await new ReadTool().ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{dir}\"}}"), Ct);
        Assert.False(readDir.Ok);
        Assert.Contains("目录", readDir.Text, StringComparison.Ordinal);
        Assert.Contains("list", readDir.Text, StringComparison.Ordinal);

        var missing = await new ReadTool().ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{Path.Combine(work.Root, "nope.txt")}\"}}"), Ct);
        Assert.False(missing.Ok);
        Assert.Contains("基准目录", missing.Text, StringComparison.Ordinal);          // 相对路径从哪算，写明白

        var listFile = await new ListTool().ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{Path.Combine(work.Root, "a.txt")}\"}}"), Ct);
        Assert.False(listFile.Ok);
        Assert.Contains("目录不存在", listFile.Text, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "x", Ct);
        var listFile2 = await new ListTool().ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{Path.Combine(work.Root, "a.txt")}\"}}"), Ct);
        Assert.False(listFile2.Ok);
        Assert.Contains("文件", listFile2.Text, StringComparison.Ordinal);
        Assert.Contains("read", listFile2.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写与改_精确替换_不唯一即拒绝()
    {
        using var work = new SnapshotTestWorkspace("tool-edit");
        var limits = ToolLimits.Default;
        var context = new ToolContext(limits, 0, null);

        var sub = Path.Combine(work.Root, "sub", "a.txt");
        var dup = Path.Combine(work.Root, "dup.txt");

        var write = new WriteTool();
        var writeOutcome = await write.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{sub}\",\"content\":\"甲\\n乙\\n\"}}"), Ct);
        Assert.True(writeOutcome.Ok);
        Assert.Equal("甲\n乙\n", await File.ReadAllTextAsync(Path.Combine(work.Root, "sub", "a.txt"), Ct));

        var edit = new EditTool();
        var replaced = await edit.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{sub}\",\"oldText\":\"乙\",\"newText\":\"丙\"}}"), Ct);
        Assert.True(replaced.Ok);
        Assert.Equal("甲\n丙\n", await File.ReadAllTextAsync(Path.Combine(work.Root, "sub", "a.txt"), Ct));

        // 反例 1：匹配 0 次 ⇒ 失败（是**结果**，不是拒绝），文件不动。
        var missing = await edit.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{sub}\",\"oldText\":\"丁\",\"newText\":\"戊\"}}"), Ct);
        Assert.False(missing.Ok);

        // 反例 2：匹配 2 次 ⇒ 拒绝（判不出改哪一处）；文件一个字节没动。
        await File.WriteAllTextAsync(Path.Combine(work.Root, "dup.txt"), "甲甲", Ct);
        await Assert.ThrowsAsync<ToolUsageException>(() => edit.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{dup}\",\"oldText\":\"甲\",\"newText\":\"乙\"}}"), Ct).AsTask());
        Assert.Equal("甲甲", await File.ReadAllTextAsync(Path.Combine(work.Root, "dup.txt"), Ct));

        // 反例 3：oldText 为空串 ⇒ 拒（会在任意位置匹配）。
        await Assert.ThrowsAsync<ToolUsageException>(() => edit.ExecuteAsync(
            context, ToolArgs.Parse($"{{\"path\":\"{dup}\",\"oldText\":\"\",\"newText\":\"乙\"}}"), Ct).AsTask());

        // 反例 4：未声明的键 ⇒ 拒。
        await Assert.ThrowsAsync<ToolUsageException>(() => edit.ExecuteAsync(
            context, ToolArgs.Parse("{\"path\":\"dup.txt\",\"oldText\":\"甲\",\"newText\":\"乙\",\"all\":true}"), Ct).AsTask());
    }

    [Fact]
    public void exec_已在最小集_且需批()
    {
        // 用户 2026-09-16 12:58 定：**exec 请回来**（[WB] 要自持 ⇒ 必须能自己跑 svn 与语料重建脚本）。
        // 03:00 的「没有跑命令这一项」口径已作废；安全框架后补。
        Assert.True(ToolNames.IsKnown("exec"));
        Assert.Contains("exec", ToolNames.ListText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(typeof(ToolNames).Assembly.GetType("AgentRuntime.Core.Tooling.ExecTool"));
        Assert.Equal(ToolRisk.Mutating, ToolNames.RiskOf("exec"));
        Assert.True(ToolNames.RequiresApproval("exec"));   // 改本机状态 ⇒ 需批
    }

    [Fact]
    public async Task exec_真的跑得动_且非零退出是结果不是拒绝()
    {
        using var work = new SnapshotTestWorkspace("tool-exec");
        var limits = ToolLimits.Default;
        var context = new ToolContext(limits, 0, null);
        var exec = new ExecTool();

        // 正例：stdout 原样带回 + exit 0。
        var ok = await exec.ExecuteAsync(context, ToolArgs.Parse("{\"command\":\"echo hello-exec\"}"), Ct);
        Assert.True(ok.Ok);
        Assert.Contains("exit 0", ok.Text, StringComparison.Ordinal);
        Assert.Contains("hello-exec", ok.Text, StringComparison.Ordinal);

        // 非 0 退出 ⇒ **结果**（跑了但失败），不是拒绝。
        var failed = await exec.ExecuteAsync(context, ToolArgs.Parse("{\"command\":\"exit 3\"}"), Ct);
        Assert.False(failed.Ok);
        Assert.Contains("exit 3", failed.Text, StringComparison.Ordinal);

        // 反例：command 缺失 / 空 / timeoutSeconds 非法 ⇒ 拒（体检在审批之前）。
        await Assert.ThrowsAsync<ToolUsageException>(() => exec.ExecuteAsync(
            context, ToolArgs.Parse("{\"command\":\"   \"}"), Ct).AsTask());
        await Assert.ThrowsAsync<ToolUsageException>(() => exec.ExecuteAsync(
            context, ToolArgs.Parse("{\"command\":\"echo x\",\"timeoutSeconds\":0}"), Ct).AsTask());

        // 输出超上限 ⇒ **截断并明说**。
        var clamped = new ToolContext(new ToolLimits { MaxOutputChars = 40 }, 0, null);
        var big = await exec.ExecuteAsync(clamped, ToolArgs.Parse("{\"command\":\"printf '12345678901234567890123456789012345678901234567890'\"}"), Ct);
        Assert.True(big.Truncated);
        Assert.Contains("截断", big.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void exec_审批面给完整命令原文与工作目录()
    {
        using var work = new SnapshotTestWorkspace("tool-exec-face");
        var args = ToolArgs.Parse("{\"command\":\"svn status /tmp/x\"}");
        var face = new ExecTool().Preview(args, ToolLimits.Default);
        var text = string.Join("\n", face.Render());

        Assert.Contains("svn status /tmp/x", text, StringComparison.Ordinal);   // 逐字原文
        Assert.Contains("工作目录", text, StringComparison.Ordinal);
        Assert.Contains("超时", text, StringComparison.Ordinal);
        Assert.Contains("[审批]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 列目录_排序稳定且目录带尾斜杠()
    {
        using var work = new SnapshotTestWorkspace("tool-list");
        await File.WriteAllTextAsync(Path.Combine(work.Root, "b.txt"), "b", Ct);
        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "a", Ct);
        Directory.CreateDirectory(Path.Combine(work.Root, "sub"));

        var outcome = await new ListTool().ExecuteAsync(
            new ToolContext(ToolLimits.Default, 0, null), ToolArgs.Parse($"{{\"path\":\"{work.Root}\"}}"), Ct);

        Assert.True(outcome.Ok);
        Assert.Contains("a.txt", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("sub/", outcome.Text, StringComparison.Ordinal);
        Assert.True(outcome.Text.IndexOf("a.txt", StringComparison.Ordinal)
                    < outcome.Text.IndexOf("b.txt", StringComparison.Ordinal));
    }

    // ---------------- 事件标签（封闭集） ----------------

    [Fact]
    public void 事件标签_工具结果与拒绝各是一个名字()
    {
        // 封闭集里只有这两个（解析大小写不敏感），且各有稳定的标签文本。
        Assert.Equal(SessionEventKind.ToolResult, SessionEventKinds.Parse("ToolResult"));
        Assert.Equal(SessionEventKind.ToolDenied, SessionEventKinds.Parse("tooldenied"));
        Assert.Null(SessionEventKinds.Parse("tool_call"));

        Assert.Equal("[TOOLRESULT]", new SessionEvent(1, SessionEventKind.ToolResult, "x").Label);
        Assert.Equal("[TOOLDENIED]", new SessionEvent(1, SessionEventKind.ToolDenied, "x").Label);
    }

    // ---------------- 解析边界：只认顶层块（2026-09-28） ----------------

    /// <summary>块头字面量（取解析器常量，别在测试里另抄一份 —— 抄一份就会跟着漂）。</summary>
    private static string Block => ToolReport.Prefix;

    /// <summary>
    /// **含工具块示例的文件 ⇒ 只执行顶层那一次调用**（2026-09-28 现场）。
    /// <para>病因：旧实现整篇逐行 StartsWith ⇒ 回复里举例 / 摘抄都会被当成真调用执行（幻影调用 + 队列暴涨）。
    /// 判据：它是不是被「引起来」的文本。</para>
    /// </summary>
    [Fact]
    public void 解析_围栏里的块头只是示例_只有顶层块被执行()
    {
        var text = string.Join('\n',
        [
            "我把示例写进文件：",
            "```",
            Block + " read {\"path\":\"/etc/hosts\"}",
            "```",
            Block + " write {\"path\":\"note.md\",\"content\":\"x\"}",
        ]);

        var result = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(1, result.Count);                  // 围栏里的那个不算
        Assert.Equal("write", result.Call!.Name);       // 只执行顶层那次

        // 未闭合 ⇒ 其后**全部**算示例（fail-safe：宁可不执行，也不执行到「被引用的一段」）。
        var open = string.Join('\n',
        [
            "示例：",
            "```",
            Block + " read {\"path\":\"/a\"}",
            Block + " exec {\"command\":\"rm -rf /tmp/nope\"}",
        ]);
        Assert.Equal(ToolParseStatus.None, ToolReport.Parse(open).Status);
    }

    [Fact]
    public void 解析_缩进式代码块与引用块里的块头_都不算调用()
    {
        var indented = string.Join('\n',
        [
            "示例文件内容：",
            "    " + Block + " read {\"path\":\"/a\"}",
            Block + " write {\"path\":\"b\",\"content\":\"y\"}",
        ]);
        var a = ToolReport.Parse(indented);
        Assert.Equal(1, a.Count);
        Assert.Equal("write", a.Call!.Name);

        var quoted = string.Join('\n',
        [
            "> " + Block + " read {\"path\":\"/a\"}",
            Block + " read {\"path\":\"/b\"}",
        ]);
        var b = ToolReport.Parse(quoted);
        Assert.Equal(1, b.Count);
        Assert.Equal("{\"path\":\"/b\"}", b.Call!.ArgumentsJson);
    }

    [Fact]
    public void 解析_顶层块照常_反向自检_新判据没误伤()
    {
        // 反向：新判据只排除「被引起来」的文本；波浪号围栏同样算示例，顶层块一次都不能少。
        var text = string.Join('\n',
        [
            "先看一眼。",
            "~~~",
            Block + " read {\"path\":\"skipped.md\"}",
            "~~~",
            Block + " read {\"path\":\"docs/a.md\"}",
            Block + " read {\"path\":\"docs/b.md\"}",
        ]);

        var result = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Count);
        Assert.Equal(["read", "read"], result.Calls.Select(static c => c.Name));
    }

    [Fact]
    public void 参数_零参工具的空参数放行_其余仍拒_缺口A2()
    {
        // 用户 2026-09-29 19:56 令（缺口 A②）：解析层放行后，**工具层**也必须放行 ——
        // 现场：`[TOOL] index` 被解析成 Ok，却在 ToolArgs 这层被「工具参数为空」拦掉。
        var ok = ToolArgs.Parse(string.Empty, ToolNames.Index);
        Assert.Empty(ok.Names);

        // 反面守住：其余工具空参数照样响亮拒（fail-closed 不变）。
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(string.Empty, ToolNames.Read));
        Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(string.Empty));   // 连工具名都不知道 ⇒ 照拒
    }

    // ---------------- E1（2026-10-10 真机）：多块回复里坏块被静默丢 ----------------

    [Fact]
    public void 多块回复_其中一块带裸小于号_逐块归一后不丢块()
    {
        // 真机 E1（M6/M9/M10 三处）：一条回复 2 个 [TOOL] 块，exec 的命令里带**裸 `<`**（`wc -c < /path`）
        // ⇒ 旧路只在「一块都取不到」时才归一 ⇒ 这块被 TakeAll 的探针语义**静默跳过**（read 回了、exec 无声无痕）。
        // 修后：**逐块「严格→归一」** ⇒ 两块都取到。
        var reply = string.Concat(
            "[TOOL]\n<call><name>exec</name><command>wc -c < /tmp/x; wc -l < /tmp/x</command></call>\n\n",
            "[TOOL]\n<call><name>read</name><path>/tmp/x</path></call>\n");

        var r = ToolReport.Parse(reply);

        Assert.Equal(ToolParseStatus.Ok, r.Status);
        Assert.Equal(2, r.Calls.Count);                          // 旧行为：1（exec 被无声丢掉）
        Assert.Contains(r.Calls, c => c.Name == ToolNames.Exec);
        Assert.Contains(r.Calls, c => c.Name == ToolNames.Read);
    }

    [Fact]
    public void 取不出的块_整篇拒_绝不静默丢()
    {
        // 反面（应红）：块头后面真跟着元素、却连归一也取不出 ⇒ **整篇拒**并说清修法，**不许无声消失**。
        var reply = string.Concat(
            "[TOOL]\n<call><name>exec</name><command>oops</wrong>\n\n",     // 闭合标签不匹配 ⇒ 取不出
            "[TOOL]\n<call><name>read</name><path>/tmp/x</path></call>\n");   // 这一块是好的

        var r = ToolReport.Parse(reply);

        Assert.Equal(ToolParseStatus.Malformed, r.Status);       // 旧行为：Ok + 1 个调用（另一块静默丢）
        Assert.Contains("绝不静默丢", r.Error!, StringComparison.Ordinal);
    }
}

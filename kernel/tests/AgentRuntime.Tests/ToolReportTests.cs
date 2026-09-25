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

        AgentRuntime.Tui.CrashDump.Write(new InvalidOperationException("闸门用的假异常"), "/tmp/fake.json", target);

        Assert.True(File.Exists(target));
        var text = File.ReadAllText(target);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("闸门用的假异常", text, StringComparison.Ordinal);
        Assert.Contains("## 会话指针", text, StringComparison.Ordinal);
        Assert.Contains("## 流末", text, StringComparison.Ordinal);
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
    public void 解析_多次调用_按发出顺序全取回()
    {
        // v14（主人 2026-09-22 定）：一次回复允许最多 ProtocolText.ToolMaxCallsPerReply 次调用 ——
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
    public void 解析_超过上限即拒绝_不取前几个()
    {
        var cap = ProtocolText.ToolMaxCallsPerReply;
        var text = string.Join('\n', Enumerable.Range(0, cap + 1).Select(i => $"[TOOL] read {{\"path\":\"f{i}\"}}"));

        var result = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.TooMany, result.Status);
        Assert.Equal(cap + 1, result.Count);
        Assert.Empty(result.Calls);                       // 刻意**不取前几个**（不替模型挑）
        Assert.Contains($"最多 {cap} 次", result.Error, StringComparison.Ordinal);
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
        var result = ToolReport.Parse("[TOOL] write\n{\"path\":\"a.txt\",\n \"content\":\"正文\"}\n[FOCUS] E001");
        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Contains("\"content\"", result.Call!.ArgumentsJson, StringComparison.Ordinal);

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

        var malformed = ToolReport.Parse(blankInside);

        Assert.Equal(ToolParseStatus.Malformed, malformed.Status);
        Assert.Contains("空行会终止参数", malformed.Error!, StringComparison.Ordinal);
        Assert.Contains("引号没闭合", malformed.Error!, StringComparison.Ordinal);
        Assert.Contains("多行正文不要塞进单行 JSON", malformed.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("键要带引号", malformed.Error!, StringComparison.Ordinal);

        // 另一半：没有空行、换行落在字符串里（大括号仍配平）⇒ 解析层放过，到 `ToolArgs` 才被 JSON 拒。
        var noBlank = "[TOOL] write {" + q + "path" + q + ":" + q + "/tmp/y" + q + ","
            + q + "content" + q + ":" + q + "第一行" + "\n" + "第二行" + q + "} risk: none";
        var parsed = ToolReport.Parse(noBlank);
        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(parsed.Call!.ArgumentsJson));
        Assert.Contains("没转义的换行", ex.Message, StringComparison.Ordinal);
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

    // ---------------- 路径真身（S1；**不做围栏** —— 主人 2026-09-16 03:00 定） ----------------

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
        var all = await read.ExecuteAsync(context, ToolArgs.Parse(argsJson), Ct);
        Assert.True(all.Ok);
        Assert.StartsWith($"read {argsJson} → 5 行", all.Text, StringComparison.Ordinal);
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
        // 2026-09-22（主人令 A）：单条结果上限 8,000 → 2,000 字符。降上限的前提是**指针准** ——
        // 旧写法先取行窗、再用字符窗砍正文 ⇒ 头部写「400/632 行」而实际只看得到 ~240 行，
        // 模型按头部下结论就会漏内容（PITFALLS「两层截断别拿一层当另一层」）。
        using var work = new SnapshotTestWorkspace("tool-read-char-cap");
        var path = Path.Combine(work.Root, "long.txt");
        await File.WriteAllLinesAsync(
            path, Enumerable.Range(1, 200).Select(i => $"{i:D3} 这是一行用来把窗口撑到字符上限的文字"), Ct);

        var context = new ToolContext(new ToolLimits { MaxOutputChars = 400 }, 0, null);
        var result = await new ReadTool().ExecuteAsync(context, ToolArgs.Parse($"{{\"path\":\"{path}\"}}"), Ct);

        Assert.True(result.Truncated);
        var lines = result.Text.Split('\n');
        var head = lines[0];
        var shown = int.Parse(head[(head.IndexOf('→') + 1)..].Trim().Split('/')[0]);   // 头部声称的行数
        var visible = lines.Skip(1).Count(static l => l.Contains("这是一行", StringComparison.Ordinal));
        Assert.Equal(shown, visible);                                                  // 头部行号 == 实际可见量
        Assert.Contains($"用 offset={visible + 1} 接着读", result.Text, StringComparison.Ordinal);   // 指针 = 下一行
        Assert.Contains("全文在 /trace", result.Text, StringComparison.Ordinal);
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
        // 主人 2026-09-16 12:58 定：**exec 请回来**（[WB] 要自持 ⇒ 必须能自己跑 svn 与语料重建脚本）。
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
}

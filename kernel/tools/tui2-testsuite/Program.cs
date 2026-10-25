using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AgentRuntime.Tui2;

namespace AgentRuntime.Tui2.Testsuite;

/// <summary>
/// **TUI 输入区域功能测试套件**（真 TTY · 真驱动 · 真编辑器 · 真渲染）。
/// <para>
/// 判据：每条用例都通过 <c>tmux</c> 分配一个真 pty，把**原始字节**（含 bracketed paste / 半个 UTF-8 /
/// 半个转义序列）打进 <c>AgentRuntime.Tui2 --input-test</c>，再逐字节核对落盘的编辑器正文。
/// 「屏上看着没问题」不算证据 —— CJK 宽度错一格、320 行少一行，肉眼看不出来。
/// </para>
/// <para>用法：<c>dotnet run --project tools/tui2-testsuite -c Release [-- --soak 10] [--skip-build]</c></para>
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;
    private static string _dll = string.Empty;
    private static string _dotnet = "dotnet";
    private static bool _verbose;
    private static int _failures;
    private static int _cases;

    private static string RunCapture(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return text;
        }
        catch { return string.Empty; }
    }

    private static int Main(string[] args)
    {
        _root = FindRepoRoot();
        _dll = Path.Combine(_root, "src/AgentRuntime.Tui2/bin/Release/net10.0/AgentRuntime.Tui2.dll");
        _dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        _verbose = args.Contains("--verbose");
        var skipBuild = args.Contains("--skip-build");
        var soak = ArgValue(args, "--soak") is { } s && int.TryParse(s, out var secs) ? secs : 8;

        Console.WriteLine("[suite] repo  = " + _root);
        Console.WriteLine("[suite] dll   = " + _dll);

        if (!skipBuild)
        {
            var rc = Run("dotnet", _root, "build", "src/AgentRuntime.Tui2/AgentRuntime.Tui2.csproj", "-c", "Release", "-v", "q", "--nologo");
            if (rc != 0)
            {
                Console.WriteLine("[suite] 编译失败 ⇒ 直接判失败");
                return 10;
            }
        }

        if (!File.Exists(_dll))
        {
            Console.WriteLine("[suite] 找不到 " + _dll);
            return 10;
        }

        ComponentSelfTest();
        FunctionalCases(soak);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"SUITE: PASS（{_cases}/{_cases} 条功能用例 + 组件自测全绿）"
            : $"SUITE: FAIL（{_failures} 条失败 / {_cases} 条功能用例）");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------- 组件自测 ----------------

    private static void ComponentSelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("== 组件自测（--selftest） ==");
        var (rc, stdout, stderr) = RunCapture(_dotnet, _root, _dll, "--selftest");
        var ok = rc == 0 && stdout.Contains("SELFTEST: PASS", StringComparison.Ordinal) && !stdout.Contains("FAIL ", StringComparison.Ordinal);
        Check(ok, "组件自测全绿（rc=" + rc + "）");
        if (!ok)
        {
            Console.WriteLine(stdout);
            Console.WriteLine(stderr);
        }
        else if (_verbose)
        {
            Console.WriteLine(stdout);
        }
    }

    // ---------------- 功能用例 ----------------

    private static void FunctionalCases(int soakSeconds)
    {
        Console.WriteLine();
        Console.WriteLine("== 功能用例（真 pty） ==");

        Case("typing-cjk-emoji", (t, d) =>
        {
            var text = "中文🙂abc";
            t.SendText(text);
            Thread.Sleep(400);
            var screen = t.Capture();
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == text, $"逐字节等于 [{text}]（实得 [{e.Expanded}]）");
            Check(!e.HasReplacementChar, "不含 U+FFFD");
            Check(e.BytesRead > 0, "输入驱动确实读到字节（BytesRead>0）");
            Check(screen.Contains("abc"), "屏幕上真的出现了输入内容");
        });

        Case("long-line-200-cjk", (t, d) =>
        {
            var text = new string('中', 200) + "END";
            t.SendText(text);
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == text, "200 个汉字 + END 逐字节到齐");
            Check(!e.HasReplacementChar, "不含 U+FFFD");
            Check(CellModelWidth(e.Expanded) == 403, "显示宽度 = 403 列（2×200+3）");
        });

        Case("arrow-home-end", (t, d) =>
        {
            t.SendText("ab");
            t.SendLeft();
            t.SendText("X");
            t.SendHome();
            t.SendText("S");
            t.SendEnd();
            t.SendText("E");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "SaXbE", $"方向键/Home/End 后 = SaXbE（实得 [{e.Expanded}]）");
        });

        Case("backspace-zwj-emoji", (t, d) =>
        {
            t.SendText("a👨‍👩‍👧b");
            t.SendBackspace();
            t.SendBackspace();
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "a", $"一次退格删整个 ZWJ 家庭 emoji（实得 [{e.Expanded}]）");
        });

        Case("backspace-combining", (t, d) =>
        {
            t.SendText("e\u0301x");
            t.SendBackspace();
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "e\u0301", $"退格只删 x，保留 e+组合符（实得 [{e.Expanded}]）");
        });

        Case("paste-atomic", (t, d) =>
        {
            var paste = Lines320();
            t.SendPaste(paste);
            Thread.Sleep(400);
            var screen = t.Capture();
            Check(screen.Contains("粘贴 320 行"), "屏上只画一个原子块占位");
            t.SendBackspace();
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Pastes == 1, "识别为 1 次粘贴");
            Check(e.Expanded == string.Empty, $"一次退格删整段（实得 {e.Expanded.Length} 字符）");
        });

        Case("paste-expand", (t, d) =>
        {
            var paste = Lines320();
            t.SendPaste(paste);
            t.SendText("TAIL");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == paste + "TAIL", "提交前逐字节展开（含 320 行 + 尾随文本）");
            Check(e.Expanded.Split('\n').Length == 320, "展开后仍是 320 行");
            Check(!e.HasReplacementChar, "不含 U+FFFD");
        });

        Case("paste-unclosed-idle", (t, d) =>
        {
            t.SendPasteStart();
            t.SendText("未闭合正文");
            Thread.Sleep(900);
            t.SendText("Z");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "未闭合正文Z", $"500ms 空闲收口后再收后续文本（实得 [{e.Expanded}]）");
        });

        Case("split-utf8", (t, d) =>
        {
            // “中” = E4 B8 AD；把 UTF-8 序列劈成两次写。
            t.SendBytes(new byte[] { 0xE4, 0xB8 });
            Thread.Sleep(150);
            t.SendBytes(new byte[] { 0xAD });
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "中", $"半个 UTF-8 序列跨读不丢、不出 U+FFFD（实得 [{e.Expanded}]）");
        });

        Case("split-escape", (t, d) =>
        {
            t.SendText("ab");
            t.SendBytes(new byte[] { 0x1B });
            Thread.Sleep(120);
            t.SendText("[D");
            t.SendText("X");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "aXb", $"半个转义序列（ESC | [D）仍识别为 Left（实得 [{e.Expanded}]）");
        });

        Case("ctrl-c-guard", (t, d) =>
        {
            t.SendText("Q");
            t.SendCtrlC();
            Thread.Sleep(2500); // 越过 2 秒双击窗口
            t.SendText("R");
            t.SendCtrlC();
            Thread.Sleep(120);
            t.SendCtrlC();
            var e = t.WaitDump();
            Check(e.Expanded == "QR", "一下 Ctrl+C 只提示、不退出（文本仍在）");
            Check(e.ExitReason == "quit", "两下 Ctrl+C 退出");
        });

        Case("newline-keys", (t, d) =>
        {
            t.SendText("a");
            t.SendBytes(new byte[] { 0x0A }); // Ctrl+J
            t.SendText("b");
            t.SendBytes(new byte[] { 0x1B, 0x0D }); // Alt+Enter
            t.SendText("c");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "a\nb\nc", $"Ctrl+J / Alt+Enter 都是换行而不是提交（实得 [{Escape(e.Expanded)}]）");
        });

        Case("tab-toggle", (t, d) =>
        {
            t.SendBytes(new byte[] { 0x09 });
            Thread.Sleep(350);
            var shown = t.Capture();
            Check(shown.Contains("6 模块") && shown.Contains("白板"), "Tab 按一下显示顶部模块区（含 6 模块）");

            Thread.Sleep(900);
            var still = t.Capture();
            Check(still.Contains("6 模块"), "不会因时间窗口自动隐藏（切换语义）");

            t.SendBytes(new byte[] { 0x09 });
            Thread.Sleep(350);
            var hidden = t.Capture();
            Check(!hidden.Contains("6 模块"), "Tab 再按一下关闭");

            t.SendFd();
            var e = t.WaitDump();
            Check(!e.TopBandVisible, "退出时顶栏已关闭");
        });

        Case("tab-release-ignored", (t, d) =>
        {
            t.SendBytes(new byte[] { 0x09 });
            Thread.Sleep(250);
            t.SendBytes(new byte[] { 0x1B, 0x5B, 0x39, 0x3B, 0x31, 0x3A, 0x33, 0x75 }); // ESC[9;1:3u
            Thread.Sleep(300);
            var shown = t.Capture();
            Check(shown.Contains("6 模块"), "key-release 不关闭（只看按一下）");

            t.SendBytes(new byte[] { 0x09 });
            Thread.Sleep(300);
            var hidden = t.Capture();
            Check(!hidden.Contains("6 模块"), "再按一下才关闭");

            t.SendFd();
            t.WaitDump();
        });

        Case("editor-autogrow", (t, d) =>
        {
            t.SendText(new string('中', 100));
            Thread.Sleep(500);
            t.SendText(new string('中', 100));
            t.SendFd();
            var e = t.WaitDump();
            Check(e.InitialEditorRows == 1, $"编辑区默认 1 行（实得 {e.InitialEditorRows}）");
            Check(e.EditorRows >= 4, $"200 汉字自动长到 ≥4 行（实得 {e.EditorRows}）");
        });

        Case("decision-card", (t, d) =>
        {
            Thread.Sleep(400);
            var screen = t.Capture();
            Check(screen.Contains("意图：整理 TUI 输入层"), "第一段意图上屏");
            Check(screen.Contains("意图：整理 TUI 输入层(exec×1 · read×2)"), "第一段操作以灰括号挂在意图行尾");
            Check(screen.Contains("结果：已确认 poll(2) 返回 POLLNVAL（E001）"), "第一段结果 + E### 锚上屏");
            Check(screen.Contains("意图：补齐决策卡"), "第二段意图上屏（turn 顺序）");
            Check(screen.Contains("决策报告 #1"), "末尾决策报告上屏");

            t.SendFd();
            t.WaitDump();
        });

        Case("mouse-select-copy", (t, d) =>
        {
            // 会话首行 = "[input-test] 真 TTY 输入验收（Ctrl+D 结束）"；按住左键拖 13 列后松开。
            t.SendBytes(new byte[] { 0x1b, (byte)'[', (byte)'<', (byte)'0', (byte)';', (byte)'1', (byte)';', (byte)'1', (byte)'M' });
            Thread.Sleep(120);
            t.SendBytes(Encoding.ASCII.GetBytes("\u001b[<32;14;1M"));
            Thread.Sleep(120);
            t.SendBytes(Encoding.ASCII.GetBytes("\u001b[<0;14;1m"));
            Thread.Sleep(500);
            var screen = t.Capture();
            // 2026-10-23 新口径：松开后**保留高亮**（像网页），复制已在松开那一刻完成 ⇒ 状态栏显示「已选中」。
            Check(screen.Contains("已选中", StringComparison.Ordinal), "松开后选区保留（状态栏显示「已选中 N 字」）");
            var clip = RunCapture("pbpaste");
            Check(clip.Trim().Length > 0, "松开即已写入剪贴板（⌘V 可直接粘贴）");
            t.SendFd();
            t.WaitDump();
        });

        Case("submit-clears", (t, d) =>
        {
            t.SendText("提交内容");
            t.SendEnter();
            Thread.Sleep(300);
            var screen = t.Capture();
            Check(screen.Contains("you > 提交内容"), "用户行带 `you > ` 前缀上屏");
            t.SendText("第二段");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Submissions.Count == 1 && e.Submissions[0] == "提交内容", "Enter 提交正文，编辑器清空");
            Check(e.Expanded == "第二段", $"提交后还能继续输入（实得 [{e.Expanded}]）");
        });

        Case("resize", (t, d) =>
        {
            t.SendText("resize中文");
            Thread.Sleep(300);
            t.Resize(60, 20);
            Thread.Sleep(3000);
            t.SendText("OK");
            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == "resize中文OK", "80×30 → 60×20 之后输入不丢、不花");
            Check(!e.HasReplacementChar, "resize 后不含 U+FFFD");
        });

        Case("resize-redraw", (t, d) =>
        {
            // 主人 2026-10-25：resize 后屏幕长时间不按新尺寸重画（旧实现尺寸缓存 30s + 每 10 帧才刷）。
            // 判据：0.5 秒内状态栏必须出现在**新尺寸的最后一行**（= 已按新行列布局重画）。
            t.SendText("x");
            Thread.Sleep(300);
            t.Resize(56, 18);
            Thread.Sleep(500);
            var lines = t.Capture().Replace("\r", string.Empty).Split('\n');
            var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToArray();
            var last = nonEmpty.Length > 0 ? nonEmpty[^1] : string.Empty;
            Check(last.Contains("Tab 切模块区", StringComparison.Ordinal),
                $"resize 后 0.5s 内状态栏按新尺寸重画（末行=[{(last.Length > 70 ? last[..70] : last)}]）");
        });

        Case("soak-" + soakSeconds + "s", (t, d) =>
        {
            var expected = new StringBuilder();
            for (var i = 1; i <= soakSeconds; i++)
            {
                var chunk = $"第{i}段-";
                t.SendText(chunk);
                expected.Append(chunk);
                Thread.Sleep(400);
            }

            t.SendFd();
            var e = t.WaitDump();
            Check(e.Expanded == expected.ToString(), $"{soakSeconds} 秒连续输入逐字节到齐");
            Check(e.Frames < 5000, $"主循环不空转（Frames={e.Frames}）");
        });
    }

    private static void Case(string name, Action<Tmux, string> body)
    {
        _cases++;
        var dir = Path.Combine(Path.GetTempPath(), "tui2-suite-" + Environment.ProcessId + "-" + name);
        Directory.CreateDirectory(dir);
        var dump = Path.Combine(dir, "dump.json");
        var ready = Path.Combine(dir, "ready.json");
        var err = Path.Combine(dir, "stderr.txt");
        var socket = "tui2suite-" + Environment.ProcessId + "-" + name;
        var extra = name == "decision-card" ? "--card-demo" : string.Empty;
        var t = new Tmux(socket, _root, _dotnet, _dll, dump, ready, err, name, extra);

        Console.WriteLine();
        Console.WriteLine("-- " + name);
        try
        {
            t.Start();
            body(t, dir);
        }
        catch (Exception ex)
        {
            Check(false, "用例异常：" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            t.Dispose();
            if (_verbose && File.Exists(err))
            {
                var text = File.ReadAllText(err);
                if (text.Length > 0) Console.WriteLine("[stderr] " + text);
            }
        }
    }

    // ---------------- 断言 / 工具 ----------------

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
    }

    private static string Lines320() =>
        string.Join("\n", Enumerable.Range(1, 320).Select(i => $"第 {i} 行 / line {i} 🙂"));

    private static int CellModelWidth(string text) => CellModel.Width(text);

    private static string Escape(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到 AgentRuntime.slnx（请从仓库内运行）");
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Run(string file, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + file);
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static (int Rc, string Out, string Err) RunCapture(string file, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + file);
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }

    /// <summary>一个独立的 tmux server + 一个跑 Tui2 的 pty。</summary>
    private sealed class Tmux : IDisposable
    {
        private readonly string _socket;
        private readonly string _root;
        private readonly string _dotnet;
        private readonly string _dll;
        private readonly string _dump;
        private readonly string _ready;
        private readonly string _err;
        private readonly string _name;
        private readonly string _extra;

        public Tmux(string socket, string root, string dotnet, string dll, string dump, string ready, string err, string name, string extra = "")
        {
            _socket = socket;
            _root = root;
            _dotnet = dotnet;
            _dll = dll;
            _dump = dump;
            _ready = ready;
            _err = err;
            _name = name;
            _extra = extra;
        }

        public void Start()
        {
            var cmd = $"'{_dotnet}' '{_dll}' --input-test {_extra} --dump-editor '{_dump}' --ready-file '{_ready}' --timeout 90 2>'{_err}'";
            var rc = TmuxRun("new-session", "-d", "-s", _name, "-x", "100", "-y", "30", "-c", _root, cmd);
            if (rc != 0) throw new InvalidOperationException("tmux new-session 失败");

            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(_ready)) return;
                Thread.Sleep(50);
            }

            throw new TimeoutException("等待 ready 文件超时：" + _ready);
        }

        public void SendText(string text) => SendBytes(Encoding.UTF8.GetBytes(text));

        public void SendBytes(byte[] bytes)
        {
            const int chunk = 256;
            for (var off = 0; off < bytes.Length; off += chunk)
            {
                var args = new List<string> { "send-keys", "-t", _name, "-H" };
                for (var i = off; i < Math.Min(off + chunk, bytes.Length); i++) args.Add(bytes[i].ToString("x2"));
                if (TmuxRun(args.ToArray()) != 0) throw new InvalidOperationException("tmux send-keys 失败");
            }
        }

        public void SendPaste(string text)
        {
            SendPasteStart();
            SendText(text);
            SendBytes(new byte[] { 0x1B, 0x5B, 0x32, 0x30, 0x31, 0x7E });
        }

        public void SendPasteStart() => SendBytes(new byte[] { 0x1B, 0x5B, 0x32, 0x30, 0x30, 0x7E });

        public void SendFd() => SendBytes(new byte[] { 0x04 });

        public void SendCtrlC() => SendBytes(new byte[] { 0x03 });

        public void SendEnter() => SendBytes(new byte[] { 0x0D });

        public void SendBackspace() => SendBytes(new byte[] { 0x7F });

        public void SendLeft() => SendBytes(new byte[] { 0x1B, 0x5B, 0x44 });

        public void SendHome() => SendBytes(new byte[] { 0x1B, 0x5B, 0x48 });

        public void SendEnd() => SendBytes(new byte[] { 0x1B, 0x5B, 0x46 });

        public void Resize(int cols, int rows) => TmuxRun("resize-window", "-t", _name, "-x", cols.ToString(), "-y", rows.ToString());

        public string Capture()
        {
            var (rc, output, _) = TmuxCapture("capture-pane", "-p", "-t", _name);
            return rc == 0 ? output : string.Empty;
        }

        public Evidence WaitDump()
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(_dump))
                {
                    try
                    {
                        var r = XElement.Load(_dump);
                        string S(string name) => r.Element(name)?.Value ?? string.Empty;
                        long L(string name) => long.TryParse(S(name), out var v) ? v : 0;
                        int I(string name) => int.TryParse(S(name), out var v) ? v : 0;
                        bool B(string name) => bool.TryParse(S(name), out var v) && v;

                        return new Evidence(
                            S("ExpandedText"),
                            S("EditorText"),
                            S("ExitReason"),
                            L("BytesRead"),
                            L("EventsParsed"),
                            I("Frames"),
                            I("Pastes"),
                            I("InitialEditorRows"),
                            I("EditorRows"),
                            B("TopBandVisible"),
                            B("HasReplacementChar"),
                            ReadSubmissions(r));
                    }
                    catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
                    {
                        // 文件正在写 / 半截 XML：下一轮再读。
                    }
                }

                Thread.Sleep(50);
            }

            throw new TimeoutException("等待落盘证据超时");
        }

        private static IReadOnlyList<string> ReadSubmissions(XElement r) =>
            r.Element("Submissions")?.Elements("Item").Select(x => x.Value).ToList() ?? new List<string>();

        public void Dispose()
        {
            TmuxRun("kill-server");
        }

        private int TmuxRun(params string[] args)
        {
            var psi = new ProcessStartInfo("tmux") { UseShellExecute = false };
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add(_socket);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }

        private (int Rc, string Out, string Err) TmuxCapture(params string[] args)
        {
            var psi = new ProcessStartInfo("tmux") { UseShellExecute = false };
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add(_socket);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p is null) return (-1, string.Empty, string.Empty);
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output, error);
        }
    }

    private sealed record Evidence(
        string Expanded,
        string EditorText,
        string ExitReason,
        long BytesRead,
        long EventsParsed,
        int Frames,
        int Pastes,
        int InitialEditorRows,
        int EditorRows,
        bool TopBandVisible,
        bool HasReplacementChar,
        IReadOnlyList<string> Submissions);
}

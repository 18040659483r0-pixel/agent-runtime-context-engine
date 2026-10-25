using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace AgentRuntime.Tui2;

/// <summary>
/// **输入区域功能测试宿主**（<c>--input-test</c>）：真 TTY + 真 InputDriver + 真 TextDocument + 真渲染，
/// 但不 Boot RuntimeHost、不联网。跑完后把编辑器正文落到 **XML**，供测试套件逐字节比对。
/// <para>
/// 与真机 App 共用 <see cref="InputController"/> 与 <see cref="TuiRenderer"/>：
/// 测试跑的就是界面上那条输入路径，不存在「测试一套、真机另一套」。
/// </para>
/// </summary>
public static class InputTest
{
    public sealed record Evidence(
        string Mode,
        double Seconds,
        string ExitReason,
        long BytesRead,
        long EventsParsed,
        int Frames,
        int Pastes,
        int InitialEditorRows,
        int EditorRows,
        bool TopBandVisible,
        string EditorText,
        string ExpandedText,
        IReadOnlyList<string> Submissions,
        IReadOnlyList<string> Notices,
        bool HasReplacementChar,
        string? Fatal);

    public static int Run(Tui2Options options)
    {
        var started = Stopwatch.StartNew();
        Utf8InputDriver? driver = null;
        var screen = new AnsiScreen();
        var frames = 0;
        var submissions = new List<string>();
        var notices = new List<string>();
        var exitReason = "timeout";
        string? fatal = null;
        var editorText = string.Empty;
        var expanded = string.Empty;
        var pastes = 0;
        var bytes = 0L;
        var events = 0L;
        var initialRows = 0;
        var editorRows = 0;
        var topBandVisible = false;

        var conversation = new List<string> { "[input-test] 真 TTY 输入验收（Ctrl+D 结束）" };
        if (options.CardDemo) conversation.AddRange(TurnCardView.Demo());
        var doc = new Terminal.Gui.Editor.Document.TextDocument(string.Empty);
        var bridge = new EditorBridge(doc);
        var paste = new PasteModel();
        var controller = new InputController(bridge, paste);
        controller.Notice += line => notices.Add(line);
        var topBand = TopBandProvider.Demo();
        var selectionCtl = new SelectionController();
        var style = Tui2Style.Detect(options.Color, options.ColorDepth);

        try
        {
            screen.Enter();
            ScreenSize.Start();   // SIGWINCH + 后台量尺（resize 当帧跟上，主循环不等）
            driver = new Utf8InputDriver();
            WriteReady(options.ReadyPath);
            TuiRenderer.Render(screen, conversation, doc, paste, "input-test", "轮 0 · 事件 0", bridge.Caret, null, style);
            initialRows = TuiRenderer.LastEditorRows;

            var until = DateTime.UtcNow.AddSeconds(options.TimeoutSeconds);
            while (DateTime.UtcNow < until)
            {
                var ev = driver.Read();
                if (ev is MouseEvent mouse)
                {
                    selectionCtl.Handle(mouse);
                }
                else if (ev is KeyEvent { Key: InputKey.Escape } && selectionCtl.Selection.Active)
                {
                    selectionCtl.Clear();   // 与真机 App 同一条规矩：Esc 先取消选区
                }
                else if (ev is not null)
                {
                    var action = controller.Handle(ev, out var body);
                    if (action == InputAction.Submit && body is not null)
                    {
                        submissions.Add(body);
                        conversation.Add(string.Empty);
                        conversation.Add("you > " + body);
                        conversation.Add(string.Empty);
                    }
                    else if (action == InputAction.Quit) { exitReason = "quit"; break; }
                }

                TuiRenderer.Render(screen, conversation, doc, paste, "input-test", "轮 0 · 事件 0", bridge.Caret,
                    controller.TopBandVisible ? topBand : null, style,
                    summary: selectionCtl.Selection.Active
                        ? $"已选中 {Selection.Extract(TuiRenderer.LastConversationTexts, selectionCtl.Selection.Range).Length} 字 · ⌘C 复制 · Esc 取消"
                        : selectionCtl.RecentNotice(),
                    selection: selectionCtl.Selection);
                frames++;
            }

            editorText = bridge.Text;
            expanded = controller.ExpandedText;
            pastes = controller.PasteCount;
            bytes = driver.BytesRead;
            events = driver.EventsParsed;
            editorRows = TuiRenderer.LastEditorRows;
            topBandVisible = controller.TopBandVisible;
        }
        catch (Exception ex)
        {
            fatal = ex.GetType().Name + ": " + ex.Message;
            exitReason = "fatal";
            Utf8InputDriver.Trace("input-test fatal: " + ex);
            return 3;
        }
        finally
        {
            driver?.Dispose();
            screen.Leave();

            var evidence = new Evidence(
                "input-test",
                Math.Round(started.Elapsed.TotalSeconds, 3),
                exitReason,
                bytes,
                events,
                frames,
                pastes,
                initialRows,
                editorRows,
                topBandVisible,
                editorText,
                expanded,
                submissions,
                notices,
                (editorText + expanded).Contains('\uFFFD'),
                fatal);

            Write(options.DumpPath, evidence);
        }

        return 0;
    }

    private static void WriteReady(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var root = new XElement("Ready",
            new XAttribute("ready", true),
            new XAttribute("pid", Environment.ProcessId),
            new XAttribute("cols", ScreenSize.Get().Cols),
            new XAttribute("rows", ScreenSize.Get().Rows),
            new XAttribute("at", DateTimeOffset.Now.ToString("O")));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, root.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
    }

    private static void Write(string? path, Evidence evidence)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var root = new XElement("Tui2Evidence",
            new XElement("Mode", evidence.Mode),
            new XElement("Seconds", evidence.Seconds),
            new XElement("ExitReason", evidence.ExitReason),
            new XElement("BytesRead", evidence.BytesRead),
            new XElement("EventsParsed", evidence.EventsParsed),
            new XElement("Frames", evidence.Frames),
            new XElement("Pastes", evidence.Pastes),
            new XElement("InitialEditorRows", evidence.InitialEditorRows),
            new XElement("EditorRows", evidence.EditorRows),
            new XElement("TopBandVisible", evidence.TopBandVisible),
            new XElement("EditorText", evidence.EditorText),
            new XElement("ExpandedText", evidence.ExpandedText),
            new XElement("Submissions", evidence.Submissions.Select(x => new XElement("Item", x))),
            new XElement("Notices", evidence.Notices.Select(x => new XElement("Item", x))),
            new XElement("HasReplacementChar", evidence.HasReplacementChar),
            new XElement("Fatal", evidence.Fatal ?? string.Empty));

        File.WriteAllText(path, root.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
    }
}

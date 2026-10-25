using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiSpike;

internal static class Program
{
    private static int _draws;

    private static int SelfTest()
    {
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
            if (!ok) Environment.ExitCode = 1;
        }

        Check(TerminalCellLayout.Width("中") == 2, "CJK width 2");
        Check(TerminalCellLayout.Width("a") == 1, "ASCII width 1");
        Check(TerminalCellLayout.Width("🙂") == 2, "emoji width 2");
        Check(TerminalCellLayout.Wrap("中文中文中", 4).Count == 3, "CJK wrap 3 lines");
        var b = new InputBuffer();
        b.Insert("a中🙂e\u0301");
        b.Backspace();
        Check(b.Text == "a中🙂", "backspace combining");
        b.Backspace();
        Check(b.Text == "a中", "backspace emoji");
        var wrap = new InputBuffer();
        wrap.Insert("aaa中");
        var c = CursorModel.Of(wrap, 4);
        Check(c == (1, 2), $"cursor at row1 col2 (got {c})");
        return Environment.ExitCode;
    }

    private static int RunEditorSpike()
    {
        Application.Init();
        try
        {
            var win = new Window { Title = "TUI Spike · Terminal.Gui.Editor 2.5.7" };
            var editor = new Terminal.Gui.Editor.Editor
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(2),
                Document = new Terminal.Gui.Editor.Document.TextDocument(string.Empty),
            };
            var status = new Label { X = 0, Y = Pos.Bottom(editor), Text = "F2=save  F3=stats  Esc=quit" };
            win.Add(editor, status);
            Application.KeyDown += (_, e) =>
            {
                if (e.KeyCode == KeyCode.F2)
                {
                    var text = editor.Document.Text;
                    File.WriteAllText("/tmp/tui-editor-input.txt", text, new UTF8Encoding(false));
                    Log($"editor saved bytes={Encoding.UTF8.GetByteCount(text)} lines={text.Count(c => c == '\n') + 1}");
                    e.Handled = true;
                }
                else if (e.KeyCode == KeyCode.F3)
                {
                    var text = editor.Document.Text;
                    Log($"editor stats bytes={Encoding.UTF8.GetByteCount(text)} lines={text.Count(c => c == '\n') + 1}");
                    e.Handled = true;
                }
                else if (e.KeyCode == KeyCode.Esc)
                {
                    Application.RequestStop(win);
                    e.Handled = true;
                }
            };
            editor.SetFocus();
            Application.Run(win);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private static void Log(string line)
    {
        try
        {
            File.AppendAllText("/tmp/tui-spike.log", $"{DateTime.Now:HH:mm:ss.fff} {line}\n", new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return SelfTest();
        }

        if (args.Contains("--editor"))
        {
            return RunEditorSpike();
        }

        Application.Init();
        try
        {
            var win = new Window { Title = "TUI Spike · Terminal.Gui 2.5" };
            var text = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(2),
                EnterKeyAddsLine = true,
            };
            var status = new Label
            {
                X = 0,
                Y = Pos.Bottom(text),
                Text = "Type / paste here. F2=save text  F3=log  Esc=quit",
            };
            win.Add(text, status);
            win.DrawComplete += (_, _) =>
            {
                _draws++;
                if (_draws % 20 == 0)
                {
                    Log($"draws={_draws}");
                }
            };
            Application.KeyDown += (_, e) =>
            {
                Log($"key={e.KeyCode} rune={(e.AsRune.ToString())} text={e.AssociatedText ?? "-"}");
                if (e.KeyCode == KeyCode.F2)
                {
                    var value = text.Text?.ToString() ?? string.Empty;
                    File.WriteAllText("/tmp/tui-spike-input.txt", value, new UTF8Encoding(false));
                    Log($"saved bytes={Encoding.UTF8.GetByteCount(value)} lines={value.Count(c => c == '\n') + 1}");
                    e.Handled = true;
                }
                else if (e.KeyCode == KeyCode.F3)
                {
                    Log($"text bytes={Encoding.UTF8.GetByteCount(text.Text?.ToString() ?? string.Empty)}");
                    e.Handled = true;
                }
                else if (e.KeyCode == KeyCode.Esc)
                {
                    Application.RequestStop(win);
                    e.Handled = true;
                }
            };
            Log("start");
            Application.Run(win);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }
}

namespace AgentRuntime.Tui2;

/// <summary>Tui2 的命令行选项（**只保留真正要用的那几个**；其余开关一律交给现 TUI）。</summary>
public sealed class Tui2Options
{
    /// <summary><c>--config &lt;path&gt;</c>（默认 = RuntimeHost.LocateDefaultConfig()）。</summary>
    public string? Config { get; private set; }

    /// <summary><c>--selftest</c>：跑纯逻辑 + 输入管线自测（不碰终端、不联网），返回码 = 失败数。</summary>
    public bool SelfTest { get; private set; }

    /// <summary><c>--autotest &lt;秒&gt;</c>：真机自动验收（真 TTY + 真输入驱动，落证据后退出）。</summary>
    public int AutoTestSeconds { get; private set; }

    /// <summary><c>--evidence &lt;path&gt;</c>：autotest 的证据落点（默认 /tmp/tui2-evidence.json）。</summary>
    public string EvidencePath { get; private set; } = "/tmp/tui2-evidence.json";

    /// <summary><c>--input-test</c>：输入区域功能测试宿主（真 TTY + 真驱动 + 真 Editor，不 Boot 内核）。</summary>
    public bool InputTest { get; private set; }

    /// <summary><c>--dump-editor &lt;path&gt;</c>：input-test 结束时把编辑器正文落成 JSON。</summary>
    public string? DumpPath { get; private set; }

    /// <summary><c>--ready-file &lt;path&gt;</c>：input-test 就绪后写这个文件（测试套件据此开始注入）。</summary>
    public string? ReadyPath { get; private set; }

    /// <summary><c>--timeout &lt;秒&gt;</c>：input-test 最长存活时间（默认 120）。</summary>
    public int TimeoutSeconds { get; private set; } = 120;

    /// <summary><c>--card-demo</c>：input-test 里铺一段决策卡演示（验布局，不连模型）。</summary>
    public bool CardDemo { get; private set; }

    /// <summary><c>--color auto|always|never</c>（默认 auto；与 TUI1 同名）。</summary>
    public string Color { get; private set; } = "auto";

    /// <summary><c>--color-depth auto|16|256|truecolor</c>（默认 auto；与 TUI1 同名）。</summary>
    public string ColorDepth { get; private set; } = "auto";

    /// <summary><c>--verbose</c>：把轮内诊断也写进会话区。</summary>
    public bool Verbose { get; private set; }

    public bool Help { get; private set; }

    /// <summary>不认识的位置参数（报出来但不起会话）。</summary>
    public IReadOnlyList<string> Rest { get; private set; } = Array.Empty<string>();

    public static Tui2Options Parse(string[] args)
    {
        var o = new Tui2Options();
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--selftest":
                    o.SelfTest = true;
                    break;
                case "--autotest":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var secs))
                    {
                        o.AutoTestSeconds = secs;
                        i++;
                    }
                    break;
                case "--evidence":
                    if (i + 1 < args.Length) o.EvidencePath = args[++i];
                    break;
                case "--input-test":
                    o.InputTest = true;
                    break;
                case "--dump-editor":
                    if (i + 1 < args.Length) o.DumpPath = args[++i];
                    break;
                case "--ready-file":
                    if (i + 1 < args.Length) o.ReadyPath = args[++i];
                    break;
                case "--timeout":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var timeout)) o.TimeoutSeconds = timeout;
                    i++;
                    break;
                case "--card-demo":
                    o.CardDemo = true;
                    break;
                case "--color":
                    if (i + 1 < args.Length) o.Color = args[++i];
                    break;
                case "--color-depth":
                    if (i + 1 < args.Length) o.ColorDepth = args[++i];
                    break;
                case "--verbose":
                    o.Verbose = true;
                    break;
                case "--help" or "-h" or "--?":
                    o.Help = true;
                    break;
                case "--config":
                    if (i + 1 < args.Length) o.Config = args[++i];
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        o.Rest = rest;
        return o;
    }

    public static void PrintUsage()
    {
        Console.Out.WriteLine("AgentRuntime.Tui2 —— Terminal.Gui.Editor 宿主（方案 B）");
        Console.Out.WriteLine();
        Console.Out.WriteLine("用法：");
        Console.Out.WriteLine("  AgentRuntime.Tui2 [--config <path>] [--color auto|always|never] [--color-depth auto|16|256|truecolor] [--verbose]");
        Console.Out.WriteLine("  AgentRuntime.Tui2 --selftest        # 纯逻辑/输入管线自测（rc = 失败数）");
        Console.Out.WriteLine("  AgentRuntime.Tui2 --autotest 20 --evidence /tmp/e.json   # 真机自动验收");
        Console.Out.WriteLine("  AgentRuntime.Tui2 --input-test --dump-editor /tmp/d.json --ready-file /tmp/r.json --timeout 60 [--card-demo]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("键位：Enter=提交 · Ctrl+J 或 Alt+Enter=换行 · Tab=显示/关闭顶部模块区 · Ctrl+C=中断/两下退出 · Ctrl+D=退出 · Ctrl+L=重画");
        Console.Out.WriteLine("斜杠命令：/help 查看全部（/reset、/start、/stack、/tail、/draft、/focus、/trace、/result、/modules、/quit …）");
        Console.Out.WriteLine("粘贴：大段正文收成一个原子块（退格一次整段删，提交时逐字节展开）。");
    }
}

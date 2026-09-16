using AgentRuntime.Hosting;

namespace AgentRuntime.Tui;

/// <summary>
/// TUI 的命令行选项（与 CLI 同名的开关**语义一致**：都交给 <see cref="HostOverrides"/> 落到配置上）。
/// </summary>
internal sealed class TuiOptions
{
    public string? Config { get; private set; }

    public string? Modules { get; private set; }

    public bool Bare { get; private set; }

    public bool Vacuum { get; private set; }

    public string? Domains { get; private set; }

    public string? StreamPath { get; private set; }

    public string? ApiKeyFile { get; private set; }

    public bool NoSnapshot { get; private set; }

    public bool FocusClear { get; private set; }

    public string? FocusTags { get; private set; }

    public string? FocusPolicy { get; private set; }

    public string? TailReport { get; private set; }

    public string? DraftReport { get; private set; }

    public bool Verbose { get; private set; }

    public bool Help { get; private set; }

    /// <summary><c>--ui split|plain</c> 的原值（null = 默认 split，但会在不合适的环境自动降级）。</summary>
    public string? Ui { get; private set; }

    /// <summary><c>--snapshot &lt;file&gt;</c>：无头渲染一帧到文件（双栏布局可复算 / 可 diff）。</summary>
    public string? SnapshotPath { get; private set; }

    /// <summary><c>--frame-size WxH</c>：无头帧尺寸（默认 96x30，即笔记本半屏目标）。</summary>
    public string? FrameSize { get; private set; }

    /// <summary><c>--cols N</c>：无头帧列数（与 <c>--frame-size</c> 可混用）。</summary>
    public int? Cols { get; private set; }

    /// <summary><c>--rows N</c>：无头帧行数。</summary>
    public int? Rows { get; private set; }

    /// <summary>无法识别的位置参数（TUI 不收裸消息：启动就跑一轮不属于「宿主」语义）。</summary>
    public List<string> Rest { get; } = [];

    public static TuiOptions Parse(string[] args)
    {
        var options = new TuiOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    options.Config = args[++i];
                    break;
                case "--modules" when i + 1 < args.Length:
                    options.Modules = args[++i];
                    break;
                case "--bare":
                    options.Bare = true;
                    break;
                case "--vacuum":
                    options.Vacuum = true;
                    break;
                case "--domains" when i + 1 < args.Length:
                    options.Domains = args[++i];
                    break;
                case "--stream" when i + 1 < args.Length:
                    options.StreamPath = args[++i];
                    break;
                case "--api-key-file" when i + 1 < args.Length:
                    options.ApiKeyFile = args[++i];
                    break;
                case "--no-snapshot":
                    options.NoSnapshot = true;
                    break;
                case "--focus" when i + 1 < args.Length:
                    options.FocusTags = args[++i];
                    break;
                case "--focus-clear":
                    options.FocusClear = true;
                    break;
                case "--focus-policy" when i + 1 < args.Length:
                    options.FocusPolicy = args[++i];
                    break;
                case "--tail-report" when i + 1 < args.Length:
                    options.TailReport = args[++i];
                    break;
                case "--draft-report" when i + 1 < args.Length:
                    options.DraftReport = args[++i];
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--ui" when i + 1 < args.Length:
                    options.Ui = args[++i];
                    break;
                case "--snapshot" when i + 1 < args.Length:
                    options.SnapshotPath = args[++i];
                    break;
                case "--frame-size" when i + 1 < args.Length:
                    options.FrameSize = args[++i];
                    break;
                case "--cols" when i + 1 < args.Length:
                    options.Cols = ParseInt(args[++i], "--cols");
                    break;
                case "--rows" when i + 1 < args.Length:
                    options.Rows = ParseInt(args[++i], "--rows");
                    break;
                case "--help" or "-h":
                    options.Help = true;
                    break;
                default:
                    options.Rest.Add(args[i]);
                    break;
            }
        }

        return options;
    }

    /// <summary>整数参数解析（非法即报错，不静默取默认）。</summary>
    private static int ParseInt(string value, string option) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{option} 需要整数，当前为 \"{value}\"。");

    /// <summary>转成宿主覆盖（**与 CLI 同一个类型**：两边语义不可能各说各话）。</summary>
    public HostOverrides ToOverrides() => new()
    {
        Modules = Modules,
        Bare = Bare,
        Vacuum = Vacuum,
        Domains = Domains,
        StreamPath = StreamPath,
        ApiKeyFile = ApiKeyFile,
        NoSnapshot = NoSnapshot,
        FocusClear = FocusClear,
        FocusTags = FocusTags,
        FocusPolicy = FocusPolicy,
        TailReport = TailReport,
        DraftReport = DraftReport,
    };
}

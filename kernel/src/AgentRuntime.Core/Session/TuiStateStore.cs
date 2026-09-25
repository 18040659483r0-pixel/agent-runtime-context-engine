namespace AgentRuntime.Core.Session;

/// <summary>
/// **TUI 的人类设置**（v12，主人 2026-09-20 15:3x 要的「记住设置」）。
/// <para>目前只有一项：**就地展开的区**（右栏目录里哪几行把正文摊在自己那一行下面）。</para>
/// </summary>
/// <param name="InlineExpanded">就地展开的区名（如 <c>R4</c> / <c>R5</c> / <c>R3</c>；次序由读取方规范化）。</param>
public sealed record TuiState(IReadOnlyList<string> InlineExpanded);

/// <summary>
/// **TUI 设置的存储区**（默认 <c>~/.agentruntime/tui.json</c>）。
/// <para>
/// <b>坏文件怎么处置？—— 降级当默认，不报错</b>（PITFALLS #24 的判据：这份状态**能重建**：
/// 默认 = 白板 / 草稿 / 焦点三组就地展开）。真正的真相源不在这里 ⇒ 读坏了不该拦人进 TUI。
/// </para>
/// <para>写入**原子**（临时文件 + 改名）：任何时刻磁盘上要么是旧的完整设置、要么是新的。</para>
/// </summary>
public sealed class TuiStateStore
{
    /// <summary>默认文件名（<c>~/.agentruntime/tui.json</c>；目录与 tail / draft / snapshot 同一处）。</summary>
    public const string DefaultFileName = "tui.json";

    /// <summary>默认位置：<c>~/.agentruntime/tui.json</c>（组合根不传路径时用它）。</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentruntime",
        DefaultFileName);

    public TuiStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>设置文件路径（绝对路径）。</summary>
    public string Path { get; }

    /// <summary>
    /// 读设置：文件不存在 / 读不动 / 坏内容 ⇒ <c>null</c>（= 用默认，**不报错**）。
    /// <para>为什么不报错：这是可重建的人类偏好，不是真相源（对比 <c>CurrentTailStore</c> 的相反口径）。</para>
    /// </summary>
    public TuiState? TryLoad()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var state = System.Text.Json.JsonSerializer.Deserialize<TuiState>(File.ReadAllText(Path));
            return state is null ? null : state with { InlineExpanded = state.InlineExpanded ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>原子写入（临时文件 + 改名）。</summary>
    public void Save(TuiState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(state));

        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        File.Move(temp, Path);
    }
}

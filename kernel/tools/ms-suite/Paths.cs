// 共享：项目根定位（找含 AgentRuntime.slnx 的目录）—— 与 `RuntimeRulesTests.RepoRoot()` 同法。
namespace MsSuite;

internal static class Paths
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    /// <summary>极简取参：`--name value` / `--name=value`。</summary>
    public static string Arg(string[] args, string name, string fallback = "")
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return fallback;
    }

    public static bool Flag(string[] args, string name) => args.Contains(name);
}

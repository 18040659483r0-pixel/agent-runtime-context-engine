using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace AgentRuntime.Tools.Deploy;

/// <summary>
/// 部署版安装器：把「裸基线」装到目标电脑，并管理 provider / 还原初始库 / 自检 / 启动。
/// <para>路径纪律：所有相对路径都从**本程序所在包根**解析（向上找 config.xml），不看 shell 的 cwd；
/// 包内布局固定（app/ · templates/ · whitebox/workspace · frozen · config.xml），整个目录搬走仍成立。</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Help();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "init" => Init(args[1..]),
                "provider" => Provider(args[1..]),
                "reset" => Reset(args[1..]),
                "check" => Check(args[1..]),
                "run" => Run(args[1..]),
                "doctor" => Doctor(args[1..]),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[deploy] {ex.Message}");
            return 1;
        }
    }

    // ---------------- init ----------------

    private static int Init(string[] a)
    {
        var target = Arg(a, 0) ?? throw new InvalidDataException("用法：deploy init <目标目录> [--force]");
        var force = Flag(a, "--force");
        var root = PackageRoot();
        var tpl = Templates(root);

        if (!Directory.Exists(Path.Combine(tpl, "workspace")))
        {
            throw new InvalidDataException($"找不到模板：{Path.Combine(tpl, "workspace")}");
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !force)
        {
            throw new InvalidDataException($"目标目录非空：{target}（确认要覆盖请加 --force）");
        }

        Directory.CreateDirectory(target);

        // 目标机要自包含：连运行时二进制一起带过去（包根 app/ 存在时）。
        var appSrc = Path.Combine(root, "app");
        if (Directory.Exists(appSrc)
            && !Path.GetFullPath(target).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            CopyTree(appSrc, Path.Combine(target, "app"));
        }

        CopyTree(Path.Combine(tpl, "workspace"), Path.Combine(target, "whitebox", "workspace"));
        foreach (var name in new[] { "config.xml", "run.sh", "run-cli.sh", "deploy.sh", "README.md", "LICENSE", "NOTICE.md" })
        {
            var src = Path.Combine(tpl, name);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(target, name), overwrite: true);
            }
        }

        Directory.CreateDirectory(Path.Combine(target, "state"));

        // 冻结语料：优先从包根复制（已有），否则提示生成。
        var frozen = Path.Combine(root, "frozen");
        if (Directory.Exists(frozen))
        {
            CopyTree(frozen, Path.Combine(target, "frozen"));
        }

        // 冻结版本账本 + 项目文档根：让派生/校验在目标机上也成立。
        var fb = Path.Combine(root, "tools", "frozen-build");
        if (Directory.Exists(fb))
        {
            CopyTree(fb, Path.Combine(target, "tools", "frozen-build"));
        }

        var docs = Path.Combine(tpl, "docs");
        if (Directory.Exists(docs))
        {
            CopyTree(docs, Path.Combine(target, "docs"));
        }

        // 初始种子：reset 要用的两份基线（知识 / 决策）随包走。
        foreach (var name in new[] { "knowledge.initial", "decision.initial" })
        {
            var src = Path.Combine(root, name);
            if (Directory.Exists(src))
            {
                CopyTree(src, Path.Combine(target, name));
            }
        }

        Console.WriteLine($"[deploy] 已初始化：{Path.GetFullPath(target)}");
        Console.WriteLine("  下一步：deploy provider --target <目录> --base-url <url> --model <model> --api-key-env <ENV>");
        Console.WriteLine("         然后：deploy check --target <目录>；deploy run --target <目录>");
        return 0;
    }

    // ---------------- provider ----------------

    private static int Provider(string[] a)
    {
        var target = Target(a);
        var cfg = Path.Combine(target, "config.xml");
        if (!File.Exists(cfg))
        {
            throw new InvalidDataException($"找不到配置：{cfg}（先 deploy init）");
        }

        var doc = XDocument.Load(cfg);
        var r = doc.Root ?? throw new InvalidDataException("config.xml 没有根元素");
        var changed = 0;

        changed += Set(r, "Provider", Value(a, "--provider"));
        changed += Set(r, "BaseUrl", Value(a, "--base-url"));
        changed += Set(r, "Model", Value(a, "--model"));
        changed += Set(r, "ApiKeyEnv", Value(a, "--api-key-env"));
        changed += Set(r, "ApiKeyFile", Value(a, "--api-key-file"));
        changed += Set(r, "ContextWindow", Value(a, "--context-window"));

        if (changed == 0)
        {
            Console.WriteLine("未给任何项。可用：--base-url --model --api-key-env --api-key-file --context-window");
            return 2;
        }

        doc.Save(cfg);
        Console.WriteLine($"[deploy] 已更新 {cfg}（{changed} 项）。密钥只走 env / 文件路径，不写进配置。");
        return 0;
    }

    // ---------------- reset ----------------

    private static int Reset(string[] a)
    {
        var target = Target(a);
        var ws = Path.Combine(target, "whitebox", "workspace");
        var tpl = Templates(PackageRoot());
        var all = Flag(a, "--all") || (!Flag(a, "--knowledge") && !Flag(a, "--decision"));
        var did = 0;

        if (all || Flag(a, "--knowledge"))
        {
            var init = Path.Combine(PackageRoot(), "knowledge.initial", "knowledge.md");
            if (!File.Exists(init))
            {
                init = Path.Combine(tpl, "knowledge.initial", "knowledge.md");
            }

            if (!File.Exists(init))
            {
                throw new InvalidDataException("找不到 knowledge.initial/knowledge.md");
            }

            File.Copy(init, Path.Combine(ws, "knowledge", "knowledge.md"), overwrite: true);
            did++;
        }

        if (all || Flag(a, "--decision"))
        {
            var dir = Path.Combine(PackageRoot(), "decision.initial");
            if (!Directory.Exists(dir))
            {
                dir = Path.Combine(tpl, "decision.initial");
            }

            foreach (var name in new[] { "DECISION-LIBRARY.md", "USER-DECISION-ENTRIES.md", "PROJECT-DESIGN-PRINCIPLES.md" })
            {
                var src = Path.Combine(dir, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(ws, name), overwrite: true);
                }
            }

            did++;
        }

        // 重建派生层
        var (dotnet, cli) = FindCli(target);
        if (cli is not null)
        {
            var rc = Exec(dotnet, [cli, "--config", Path.Combine(target, "config.xml"), "--kn-build", ws]);
            Console.WriteLine($"[deploy] 派生层重建 rc={rc}");
        }

        Console.WriteLine($"[deploy] 已还原初始库（{did} 组）。");
        return 0;
    }

    // ---------------- check ----------------

    private static int Check(string[] a)
    {
        var target = Target(a);
        var ws = Path.Combine(target, "whitebox", "workspace");
        var (dotnet, cli) = FindCli(target);
        if (cli is null)
        {
            throw new InvalidDataException("找不到 AgentRuntime.Cli（部署包应有 app/cli/AgentRuntime.Cli.dll）");
        }

        var rc1 = Exec(dotnet, [cli, "--config", Path.Combine(target, "config.xml"), "--kn-check", ws]);
        var rc2 = Exec(dotnet, [cli, "--config", Path.Combine(target, "config.xml"), "--frozen-corpus", ws, "--frozen-corpus-check"]);
        Console.WriteLine(rc1 == 0 && rc2 == 0 ? "[deploy] check 全绿 ✅" : $"[deploy] check 红：kn={rc1} frozen={rc2}");
        return rc1 == 0 && rc2 == 0 ? 0 : 1;
    }

    // ---------------- run ----------------

    private static int Run(string[] a)
    {
        var target = Target(a);
        var cfg = Path.Combine(target, "config.xml");
        var (dotnet, cli) = FindAnyRuntime(target, Flag(a, "--cli"));
        if (cli is null)
        {
            throw new InvalidDataException("找不到运行时（app/tui 或 app/cli）");
        }

        var extra = a.SkipWhile(x => x.StartsWith("--", StringComparison.Ordinal) || x == target).ToList();
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false };
        psi.ArgumentList.Add(cli);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfg);
        foreach (var x in extra)
        {
            psi.ArgumentList.Add(x);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---------------- doctor ----------------

    private static int Doctor(string[] a)
    {
        var target = Target(a);
        var ok = true;
        var dotnetVer = ExecCapture("dotnet", ["--version"]).Trim();
        Console.WriteLine($"dotnet：{(dotnetVer.Length > 0 ? dotnetVer : "缺失")}");
        ok &= dotnetVer.Length > 0;

        var cfg = Path.Combine(target, "config.xml");
        Console.WriteLine($"config：{(File.Exists(cfg) ? cfg : "缺失")}");
        ok &= File.Exists(cfg);

        var ws = Path.Combine(target, "whitebox", "workspace");
        var kn = Path.Combine(ws, "knowledge", "knowledge.md");
        Console.WriteLine($"知识活版：{(File.Exists(kn) ? kn : "缺失")}");
        ok &= File.Exists(kn);

        var (dotnet, cli) = FindCli(target);
        Console.WriteLine($"CLI：{(cli ?? "缺失")}");
        ok &= cli is not null;

        if (File.Exists(cfg))
        {
            var doc = XDocument.Load(cfg);
            var file = doc.Root?.Element("ApiKeyFile")?.Value;
            var env = doc.Root?.Element("ApiKeyEnv")?.Value;
            var resolved = file is null ? null : file.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Console.WriteLine($"provider：{doc.Root?.Element("BaseUrl")?.Value} / {doc.Root?.Element("Model")?.Value}");
            Console.WriteLine($"key：env {env ?? "—"} · file {(resolved is not null && File.Exists(resolved) ? "存在" : "不存在")}");

            if (resolved is not null && !File.Exists(resolved) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env ?? "")))
            {
                Console.WriteLine("  ⚠️ 还没有可用的 key（按 README 写 key 文件或设环境变量）");
            }
        }

        Console.WriteLine(ok ? "[deploy] doctor：布局 OK" : "[deploy] doctor：有缺失（见上）");
        return ok ? 0 : 1;
    }

    // ---------------- helpers ----------------

    private static string PackageRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config.xml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    }

    private static string Templates(string root)
    {
        var local = Path.Combine(root, "templates");
        if (Directory.Exists(local)) return local;
        var repo = Path.Combine(root, "tools", "deploy", "templates");
        return Directory.Exists(repo) ? repo : local;
    }

    private static string Target(string[] a) =>
        Path.GetFullPath(Value(a, "--target") ?? Arg(a, 0) ?? PackageRoot());

    private static (string Dotnet, string? Cli) FindCli(string target)
    {
        var candidates = new[]
        {
            Path.Combine(target, "app", "cli", "AgentRuntime.Cli.dll"),
            Path.Combine(target, "src", "AgentRuntime.Cli", "bin", "Release", "net10.0", "AgentRuntime.Cli.dll"),
            Path.Combine(target, "src", "AgentRuntime.Cli", "bin", "Debug", "net10.0", "AgentRuntime.Cli.dll"),
        };
        return ("dotnet", candidates.FirstOrDefault(File.Exists));
    }

    private static (string Dotnet, string? Cli) FindAnyRuntime(string target, bool cliWanted)
    {
        var cli = Path.Combine(target, "app", "cli", "AgentRuntime.Cli.dll");
        var tui = Path.Combine(target, "app", "tui", "AgentRuntime.Tui.dll");
        var pick = cliWanted ? cli : (File.Exists(tui) ? tui : cli);
        return ("dotnet", File.Exists(pick) ? pick : FindCli(target).Cli);
    }

    private static int Set(XElement root, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var el = root.Element(name);
        if (el is null)
        {
            root.Add(new XElement(name, value));
        }
        else
        {
            el.Value = value;
        }

        return 1;
    }

    private static string? Value(string[] a, string name)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == name && i + 1 < a.Length) return a[i + 1];
            if (a[i].StartsWith(name + "=", StringComparison.Ordinal)) return a[i][(name.Length + 1)..];
        }

        return null;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? Arg(string[] a, int index) =>
        index >= 0 && index < a.Length && !a[index].StartsWith("--", StringComparison.Ordinal) ? a[index] : null;

    private static int Exec(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var x in args) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string ExecCapture(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var x in args) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return text;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"[deploy] 未知命令：{cmd}");
        Help();
        return 2;
    }

    private static int Help()
    {
        Console.WriteLine("""
            Agent Runtime 部署器（裸基线）
              deploy init <目标目录> [--force]     复制模板 + 配置 + 启动脚本到目标机
              deploy provider [--target <目录>]    设定 provider（--base-url --model --api-key-env --api-key-file --context-window）
              deploy reset [--target <目录>]       还原初始库（--knowledge / --decision / --all）
              deploy check [--target <目录>]       知识树 + 冻结语料自检
              deploy run [--target <目录>] [--cli] 启动 TUI（或 --cli）
              deploy doctor [--target <目录>]      环境 / key / 布局体检
            路径：一律从**包根**（本程序所在目录向上找到 config.xml）解析，不看 shell cwd。
            """);
        return 0;
    }
}

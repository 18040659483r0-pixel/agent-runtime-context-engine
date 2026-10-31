using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Workspace 语料管理器（Mac / POSIX 版）—— 与 [001] 的 Windows 版同接口、同规则文件。
// 三个能力：new（生成部署版）/ backup（备份当前语料）/ restore（还原），外加 selftest 与 rule-check。
// 规则唯一来源：deploy-template.json（与 Windows 版逐字对齐；rule-check 会比对两端）。

var cmd = args.Length > 0 ? args[0] : "help";
var map = new Dictionary<string, string?>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
    var key = args[i][2..];
    string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
    map[key] = value;
}

var here = AppContext.BaseDirectory;
var toolDir = FindToolDir();
var rules = Rules.Load(map.TryGetValue("rules", out var rp) && rp is not null ? rp : Path.Combine(toolDir, "deploy-template.json"));

try
{
    switch (cmd)
    {
        case "new":
            var target = Require("target");
            var template = map.TryGetValue("template", out var tp) && tp is not null ? tp : Path.Combine(toolDir, "deployment-template");
            return Corpus.New(template, target, map.ContainsKey("reset"), rules);

        case "backup":
            return Corpus.Backup(Require("workspace"), Require("backup"), rules);

        case "restore":
            return Corpus.Restore(Require("from"), Require("to"));

        case "rule-check":
            return Corpus.RuleCheck(rules, map.TryGetValue("windows", out var wp) ? wp : null);

        case "selftest":
            return Corpus.SelfTest(toolDir);

        default:
            Console.WriteLine("""
                wcm —— Workspace 语料管理器（Mac / POSIX）

                用法：
                  wcm new     --target <ws> [--template <dir>] [--reset]   生成部署版语料（--reset 先清空目标）
                  wcm backup  --workspace <ws> --backup <dir>              备份当前语料（排除运行态/密钥/bin·obj/.svn）
                  wcm restore --from <dir> --to <ws>                       从备份还原（合并，不删目标已有文件）
                  wcm rule-check [--windows <json>]                        与 Windows 版规则文件逐字比对
                  wcm selftest                                            沙盒自测（生成→备份→破坏→还原→比对）

                公共参数：--rules <deploy-template.json>
                """);
            return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("[wcm] " + ex.Message);
    return 2;
}

string Require(string key) =>
    map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : throw new ArgumentException($"缺少 --{key}");

string FindToolDir()
{
    var dir = new DirectoryInfo(here);
    while (dir is not null
           && !(File.Exists(Path.Combine(dir.FullName, "deploy-template.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "deployment-template"))))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? here;
}

internal sealed record Rules(IReadOnlyList<string> Include, IReadOnlyList<string> CreateDirs, IReadOnlyList<string> ExcludeFromBackup)
{
    public static Rules Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"规则文件不存在：{path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new Rules(Read(root, "include"), Read(root, "createDirs"), Read(root, "excludeFromBackup"));
    }

    private static List<string> Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : [];
}

internal static class Corpus
{
    // 运行态 / 版本库 / 构建产物：**永远不备份**（与 Windows 版 robocopy /XD 一致）。
    private static readonly string[] AlwaysDirExcludes = ["state", ".svn", "bin", "obj", ".git"];
    private const string AlwaysFileExcludes = ".tui2.lock";

    public static int New(string template, string target, bool reset, Rules rules)
    {
        if (!Directory.Exists(template)) throw new DirectoryNotFoundException($"模板目录不存在：{template}");
        GuardNotSystemPath(target);

        if (reset && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        foreach (var name in rules.Include)
        {
            var src = Path.Combine(template, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(target, name), overwrite: true);
        }

        foreach (var dir in rules.CreateDirs) Directory.CreateDirectory(Path.Combine(target, dir));

        var probe = rules.Include.FirstOrDefault();
        var ok = probe is not null && File.Exists(Path.Combine(target, probe));
        Console.WriteLine($"[wcm] 部署版已生成：{Path.GetFullPath(target)}（include {rules.Include.Count} · dirs {rules.CreateDirs.Count}）");
        return ok ? 0 : 1;
    }

    public static int Backup(string workspace, string backup, Rules rules)
    {
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException($"workspace 不存在：{workspace}");
        GuardNotSystemPath(backup);
        if (IsInside(backup, workspace)) throw new InvalidOperationException("备份目录不能放在 workspace 内（会把自己备份进去）");

        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        Directory.CreateDirectory(backup);

        var dirs = new HashSet<string>(AlwaysDirExcludes, StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(new[] { AlwaysFileExcludes }, StringComparer.OrdinalIgnoreCase);
        foreach (var name in rules.ExcludeFromBackup)
        {
            var p = Path.Combine(workspace, name);
            if (Directory.Exists(p)) dirs.Add(name); else files.Add(name);
        }

        var copied = CopyTree(workspace, backup, dirs, files);
        Console.WriteLine($"[wcm] 已备份 {copied} 个文件 → {Path.GetFullPath(backup)}（排除 {dirs.Count} 类目录 / {files.Count} 个文件）");
        return 0;
    }

    public static int Restore(string from, string to)
    {
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"备份不存在：{from}");
        GuardNotSystemPath(to);
        Directory.CreateDirectory(to);

        var copied = CopyTree(from, to, new HashSet<string>(AlwaysDirExcludes, StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"[wcm] 已从备份还原 {copied} 个文件 → {Path.GetFullPath(to)}（合并，不删目标已有文件）");
        return 0;
    }

    /// <summary>与 Windows 版的 deploy-template.json 逐字比对（规则不允许分叉）。</summary>
    public static int RuleCheck(Rules rules, string? windowsPath)
    {
        var mine = Path.Combine(AppContext.BaseDirectory, "deploy-template.json");
        if (!File.Exists(mine)) mine = Path.Combine(Directory.GetCurrentDirectory(), "deploy-template.json");

        var other = windowsPath ?? GuessWindowsRules();
        if (other is null || !File.Exists(other))
        {
            Console.WriteLine("[wcm] 找不到 Windows 版规则文件（可用 --windows <path> 指定）⇒ 跳过比对");
            return 0;
        }

        var a = File.ReadAllText(mine).Replace("\r\n", "\n");
        var b = File.ReadAllText(other).Replace("\r\n", "\n");
        if (a == b)
        {
            Console.WriteLine($"[wcm] 规则一致：{Path.GetFileName(other)}");
            return 0;
        }

        Console.Error.WriteLine("[wcm] 规则不一致 —— 两端 deploy-template.json 必须逐字对齐：");
        Console.Error.WriteLine("  mac : " + mine);
        Console.Error.WriteLine("  win : " + other);
        return 1;
    }

    public static int SelfTest(string toolDir)
    {
        var box = Path.Combine(toolDir, "test-sandbox");
        if (Directory.Exists(box)) Directory.Delete(box, true);
        Directory.CreateDirectory(box);

        var rules = Rules.Load(Path.Combine(toolDir, "deploy-template.json"));
        var tpl = Path.Combine(toolDir, "deployment-template");
        var ws = Path.Combine(box, "ws");
        var bk = Path.Combine(box, "backup");

        var results = new List<(string Name, bool Ok)>();
        void Check(string name, bool ok) { results.Add((name, ok)); Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}"); }

        Console.WriteLine("[wcm] selftest");
        var rc = New(tpl, ws, reset: true, rules);
        Check("生成部署版（首次）", rc == 0 && File.Exists(Path.Combine(ws, rules.Include[0])));
        foreach (var d in rules.CreateDirs) Check($"结构目录 {d}", Directory.Exists(Path.Combine(ws, d)));

        // 模拟积累：语料改动 + 运行态 + 密钥 + 构建产物
        File.WriteAllText(Path.Combine(ws, "memory", "day.md"), "today");
        Directory.CreateDirectory(Path.Combine(ws, "state"));
        File.WriteAllText(Path.Combine(ws, "state", "stream.xml"), "<x/>");
        File.WriteAllText(Path.Combine(ws, ".tui2.lock"), "lock");
        File.WriteAllText(Path.Combine(ws, "deepseek apikey.txt"), "sk-SECRET");
        Directory.CreateDirectory(Path.Combine(ws, "bin"));
        File.WriteAllText(Path.Combine(ws, "bin", "a.dll"), "binary");

        Check("备份成功", Backup(ws, bk, rules) == 0);
        Check("备份含语料改动", File.Exists(Path.Combine(bk, "memory", "day.md")));
        Check("备份不含 state/", !Directory.Exists(Path.Combine(bk, "state")));
        Check("备份不含 .tui2.lock", !File.Exists(Path.Combine(bk, ".tui2.lock")));
        Check("备份不含密钥", !File.Exists(Path.Combine(bk, "deepseek apikey.txt")));
        Check("备份不含 bin/", !Directory.Exists(Path.Combine(bk, "bin")));

        // 破坏 + 还原
        File.Delete(Path.Combine(ws, "memory", "day.md"));
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "broken");
        Check("还原成功", Restore(bk, ws) == 0);
        Check("还原回语料", File.ReadAllText(Path.Combine(ws, "memory", "day.md")) == "today");
        Check("还原覆盖被破坏文件", File.ReadAllText(Path.Combine(ws, "AGENTS.md")).Length > 10);

        // 重置
        Check("--reset 清空目标", New(tpl, ws, reset: true, rules) == 0 && !File.Exists(Path.Combine(ws, "memory", "day.md")));

        Directory.Delete(box, true);
        var pass = results.Count(x => x.Ok);
        Console.WriteLine($"[wcm] {pass}/{results.Count} PASS");
        return pass == results.Count ? 0 : 1;
    }

    private static int CopyTree(string from, string to, HashSet<string> dirExcludes, HashSet<string> fileExcludes)
    {
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, entry);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => dirExcludes.Contains(p))) continue;
            if (!Directory.Exists(entry) && fileExcludes.Contains(Path.GetFileName(entry))) continue;

            var dst = Path.Combine(to, rel);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(dst);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(entry, dst, overwrite: true);
            count++;
        }

        return count;
    }

    private static bool IsInside(string child, string parent)
    {
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.Ordinal);
    }

    private static void GuardNotSystemPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
        if (full is "" or "/" || string.Equals(full, home, StringComparison.Ordinal))
            throw new InvalidOperationException($"拒绝操作系统目录：{full}");
    }

    private static string? GuessWindowsRules()
    {
        // whitebox/tools/workspace-corpus-manager → ../../.. = AgentWorkFlow（父级），再拼 Windows 项目路径。
        var tool = new DirectoryInfo(AppContext.BaseDirectory);
        while (tool is not null && tool.Name != "workspace-corpus-manager") tool = tool.Parent;
        if (tool is null) return null;
        var repo = tool.Parent?.Parent?.Parent?.FullName;   // workspace-corpus-manager → tools → whitebox → AgentWorkFlow
        return repo is null ? null : Path.Combine(repo, "software-company", "projects", "whiteboxwinx64", "tools", "workspace-corpus-manager", "deploy-template.json");
    }
}

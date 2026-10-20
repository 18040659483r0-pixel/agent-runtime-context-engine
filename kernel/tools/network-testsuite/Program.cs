using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentRuntime.NetworkTestSuite;

/// <summary>
/// **I 系列 · 网络工具性能测试套件**（与 M / W 系列并列）。
/// <para>
/// 每个场景 = 一句真实任务 → 官方 provider 跑一次完整 agent 轮 → 从流里取**工具事实**、
/// 从 <c>&lt;stream&gt;.usage.xml</c> 取**真实 token**、从进程取**墙钟** → 逐条判绿并出表。
/// </para>
/// <para>测试不碰真实工作区：配置副本、流、下载目录、状态文件全部落在 <c>.stage1/net-suite/&lt;run&gt;/</c>。</para>
/// </summary>
public static class Program
{
    private static readonly Regex NameRegex = new(@"<name>\s*([^<]+?)\s*</name>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ToolBlockRegex = new(@"\[TOOL\]", RegexOptions.Compiled);

    public static async Task<int> Main(string[] args)
    {
        var repo = FindRepoRoot();
        var outRoot = Path.Combine(repo, ".stage1", "net-suite", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    list = true;
                    break;
                case "--only" when i + 1 < args.Length:
                    foreach (var id in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        only.Add(id);
                    }

                    break;
                case "--out" when i + 1 < args.Length:
                    outRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("用法：dotnet run --project tools/network-testsuite [-- --list|--only I1,I2|--out <dir>]");
                    return 0;
            }
        }

        var scenarios = Scenarios.All(repo, outRoot).Where(s => only.Count == 0 || only.Contains(s.Id)).ToArray();
        if (list)
        {
            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"{scenario.Id}\t{scenario.Title}");
            }

            return 0;
        }

        Directory.CreateDirectory(outRoot);
        var suiteConfig = Path.Combine(outRoot, "config.xml");
        BuildSuiteConfig(repo, outRoot, suiteConfig);

        var cli = Path.Combine(repo, "src", "AgentRuntime.Cli", "bin", "Debug", "net10.0", "AgentRuntime.Cli.dll");
        if (!File.Exists(cli))
        {
            Console.Error.WriteLine($"找不到 CLI：{cli}（先 dotnet build AgentRuntime.slnx）");
            return 2;
        }

        Console.WriteLine($"[I 系列] 网络工具性能测试套件 · {scenarios.Length} 个场景");
        Console.WriteLine($"  repo   : {repo}");
        Console.WriteLine($"  out    : {outRoot}");
        Console.WriteLine($"  config : {suiteConfig}");
        Console.WriteLine();

        var results = new List<ScenarioResult>();
        foreach (var scenario in scenarios)
        {
            var result = await RunAsync(repo, cli, suiteConfig, outRoot, scenario);
            results.Add(result);
            PrintResult(result);
        }

        WriteReport(outRoot, results);
        var passed = results.Count(r => r.Ok);
        Console.WriteLine();
        Console.WriteLine($"[I 系列] {passed}/{results.Count} 通过 · report.md / report.xml → {outRoot}");
        return passed == results.Count ? 0 : 1;
    }

    private static async Task<ScenarioResult> RunAsync(
        string repo,
        string cli,
        string suiteConfig,
        string outRoot,
        Scenario scenario)
    {
        var dir = Path.Combine(outRoot, scenario.Id);
        Directory.CreateDirectory(dir);
        var stream = Path.Combine(dir, "stream.xml");
        var downloads = Path.Combine(dir, "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(dir, "prompt.txt"), scenario.Prompt);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(cli);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(suiteConfig);
        startInfo.ArgumentList.Add("--stream");
        startInfo.ArgumentList.Add(stream);
        startInfo.ArgumentList.Add("--auto-continue");
        startInfo.ArgumentList.Add("16");
        startInfo.ArgumentList.Add(scenario.Prompt);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 CLI。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已退出
            }
        }

        stopwatch.Stop();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        File.WriteAllText(Path.Combine(dir, "stdout.txt"), stdout);
        File.WriteAllText(Path.Combine(dir, "stderr.txt"), stderr);

        var facts = RunFacts.Load(stream);
        var check = scenario.Check(facts, downloads);
        return new ScenarioResult(
            scenario.Id,
            scenario.Title,
            check.Ok,
            check.Detail,
            facts,
            stopwatch.Elapsed.TotalSeconds,
            process.ExitCode,
            dir);
    }

    private static void PrintResult(ScenarioResult result)
    {
        var mark = result.Ok ? "PASS" : "FAIL";
        var facts = result.Facts;
        Console.WriteLine(
            $"{result.Id,-3} {mark,-4} {result.Title,-18} " +
            $"rounds={facts.Rounds} tools={facts.ToolCalls} deny={facts.Denials} " +
            $"prompt={facts.PromptTokens} cached={facts.CachedTokens} completion={facts.CompletionTokens} total={facts.TotalTokens} " +
            $"wall={result.WallSeconds:F1}s");
        Console.WriteLine($"     {result.Detail}");
    }

    private static void WriteReport(string outRoot, IReadOnlyList<ScenarioResult> results)
    {
        var md = new StringBuilder();
        md.AppendLine("# I 系列 · 网络工具性能测试结果");
        md.AppendLine();
        md.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        md.AppendLine($"- 场景：{results.Count} · 通过：{results.Count(r => r.Ok)}");
        md.AppendLine();
        md.AppendLine("| # | 场景 | 结果 | 轮 | 工具 | 拒绝 | prompt | cached | completion | total | 墙钟 | 判据 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var result in results)
        {
            md.AppendLine(
                $"| {result.Id} | {result.Title} | {(result.Ok ? "✅" : "❌")} | {result.Facts.Rounds} | " +
                $"{result.Facts.ToolCalls} | {result.Facts.Denials} | {result.Facts.PromptTokens} | " +
                $"{result.Facts.CachedTokens} | {result.Facts.CompletionTokens} | {result.Facts.TotalTokens} | " +
                $"{result.WallSeconds:F1}s | {Escape(result.Detail)} |");
        }

        md.AppendLine();
        md.AppendLine(
            $"**合计**：prompt={results.Sum(r => r.Facts.PromptTokens)} · " +
            $"cached={results.Sum(r => r.Facts.CachedTokens)} · " +
            $"completion={results.Sum(r => r.Facts.CompletionTokens)} · " +
            $"total={results.Sum(r => r.Facts.TotalTokens)} · " +
            $"墙钟={results.Sum(r => r.WallSeconds):F1}s");
        File.WriteAllText(Path.Combine(outRoot, "report.md"), md.ToString());

        var xml = new XElement("NetworkSuiteReport",
            new XAttribute("generated", DateTimeOffset.Now.ToString("O")),
            new XAttribute("passed", results.Count(r => r.Ok)),
            new XAttribute("total", results.Count),
            results.Select(r => new XElement("Scenario",
                new XAttribute("id", r.Id),
                new XAttribute("ok", r.Ok ? "1" : "0"),
                new XElement("title", r.Title),
                new XElement("detail", r.Detail),
                new XElement("rounds", r.Facts.Rounds),
                new XElement("toolCalls", r.Facts.ToolCalls),
                new XElement("denials", r.Facts.Denials),
                new XElement("promptTokens", r.Facts.PromptTokens),
                new XElement("cachedTokens", r.Facts.CachedTokens),
                new XElement("completionTokens", r.Facts.CompletionTokens),
                new XElement("totalTokens", r.Facts.TotalTokens),
                new XElement("wallSeconds", r.WallSeconds.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("dir", r.Dir))));
        xml.Save(Path.Combine(outRoot, "report.xml"));
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static void BuildSuiteConfig(string repo, string outRoot, string target)
    {
        var doc = XDocument.Load(Path.Combine(repo, "config.xml"));
        var root = doc.Root ?? throw new InvalidDataException("config.xml 没有根元素。");

        var network = root.Element("Network") ?? new XElement("Network");
        network.RemoveNodes();
        network.Add(new XElement("SearchBackend", "wikipedia"));
        network.Add(new XElement("SearchEndpoint"));
        network.Add(new XElement("SearchApiKeyEnv"));
        network.Add(new XElement("TimeoutSeconds", 30));
        network.Add(new XElement("MaxResponseBytes", 2_000_000));
        network.Add(new XElement("MaxRedirects", 5));
        network.Add(new XElement("MaxConcurrency", 4));
        network.Add(new XElement("CacheTtlSeconds", 60));
        network.Add(new XElement("DownloadRoots", new XElement("string", Path.Combine(outRoot, "I4", "downloads"))));
        network.Add(new XElement("AllowedDownloadTypes",
            new XElement("string", "application/pdf"),
            new XElement("string", "text/plain"),
            new XElement("string", "text/html"),
            new XElement("string", "application/json")));
        network.Add(new XElement("Credentials"));
        root.Element("Network")?.Remove();
        root.Add(network);

        // 不碰真实工作区的状态。
        var snapshot = root.Element("Snapshot");
        if (snapshot is null)
        {
            snapshot = new XElement("Snapshot");
            root.Add(snapshot);
        }

        snapshot.SetElementValue("Enabled", "false");

        var worker = root.Element("Worker");
        if (worker is null)
        {
            worker = new XElement("Worker");
            root.Add(worker);
        }

        worker.SetElementValue("Enabled", "false");

        // 用部署包里的**最小种子工作区**（不拿仓里的记忆/知识做测试；也避免把套件跑成 35k prompt）。
        var seedWorkspace = Path.Combine(repo, "dist", "alpha-0.1.1", "whitebox", "workspace");
        var suiteWorkspace = Path.Combine(outRoot, "workspace");
        if (Directory.Exists(seedWorkspace))
        {
            CopyDirectory(seedWorkspace, suiteWorkspace);
        }

        // 配置里的相对路径按**配置文件目录**解析 ⇒ 套件配置在 .stage1 下，必须全部改成绝对路径。
        var frozen = root.Element("Frozen");
        if (frozen is not null)
        {
            frozen.SetElementValue("Root", Path.Combine(repo, "dist", "alpha-0.1.1", "frozen"));
        }

        var knowledge = root.Element("Knowledge");
        if (knowledge is not null)
        {
            knowledge.SetElementValue("Repo", string.Empty);
        }

        var lifecycle = root.Element("Lifecycle");
        if (lifecycle is not null)
        {
            lifecycle.SetElementValue("Workspace", suiteWorkspace);
            lifecycle.SetElementValue("Pitfalls", Path.Combine(suiteWorkspace, "docs", "PITFALLS.md"));
            lifecycle.SetElementValue("PitfallsInbox", Path.Combine(suiteWorkspace, "docs", "TOOLFACE-PITFALLS.md"));
            lifecycle.SetElementValue("Handover", Path.Combine(outRoot, "state", "handover.xml"));
        }

        root.Element("Stream")?.SetElementValue("Path", Path.Combine(outRoot, "state", "stream.xml"));
        root.Element("CurrentTail")?.SetElementValue("StorePath", Path.Combine(outRoot, "state", "tail"));
        root.Element("DynamicDraft")?.SetElementValue("StorePath", Path.Combine(outRoot, "state", "draft"));
        root.Element("Focus")?.SetElementValue("Path", Path.Combine(outRoot, "state", "focus.xml"));
        root.Element("Snapshot")?.SetElementValue("Path", Path.Combine(outRoot, "state", "snapshot.xml"));
        root.Element("ToolQueue")?.SetElementValue("StorePath", string.Empty);

        doc.Save(target);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根（AgentRuntime.slnx）。");
    }
}

public sealed record Scenario(string Id, string Title, string Prompt, Func<RunFacts, string, CheckResult> Check);

public sealed record ScenarioResult(
    string Id,
    string Title,
    bool Ok,
    string Detail,
    RunFacts Facts,
    double WallSeconds,
    int ExitCode,
    string Dir);

public sealed record CheckResult(bool Ok, string Detail);

public sealed class RunFacts
{
    public required IReadOnlyList<string> ToolResults { get; init; }
    public required IReadOnlyList<string> ToolDenied { get; init; }
    public required IReadOnlyList<string> AgentOutputs { get; init; }
    public required IReadOnlyList<string> ToolNames { get; init; }
    public int Rounds { get; init; }
    public int PromptTokens { get; init; }
    public int CachedTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }

    public int ToolCalls => ToolResults.Count;
    public int Denials => ToolDenied.Count;
    public string AllResults => string.Join("\n", ToolResults);

    public static RunFacts Load(string streamPath)
    {
        var toolResults = new List<string>();
        var denied = new List<string>();
        var outputs = new List<string>();
        var names = new List<string>();
        var rounds = 0;
        var prompt = 0;
        var cached = 0;
        var completion = 0;
        var total = 0;

        if (File.Exists(streamPath))
        {
            var content = File.ReadAllText(streamPath);
            var lastEnd = content.LastIndexOf("</Event>", StringComparison.Ordinal);
            if (lastEnd >= 0)
            {
                content = content[..(lastEnd + "</Event>".Length)];
            }

            try
            {
                var document = XDocument.Parse("<root>" + content + "</root>");
                foreach (var element in document.Root!.Elements("Event"))
                {
                    var kind = element.Element("Kind")?.Value ?? string.Empty;
                    var text = element.Element("Text")?.Value ?? string.Empty;
                    switch (kind)
                    {
                        case "ToolResult":
                            toolResults.Add(text);
                            if (!string.IsNullOrWhiteSpace(element.Element("Source")?.Value))
                            {
                                names.Add(element.Element("Source")!.Value);
                            }

                            break;
                        case "ToolDenied":
                            denied.Add(text);
                            break;
                        case "AgentOutput":
                            outputs.Add(text);
                            break;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // 流被截断 / 半写：保留已解析出的部分（读数不因坏尾丢光）。
            }
        }

        var usage = streamPath + ".usage.xml";
        if (File.Exists(usage))
        {
            foreach (var line in File.ReadLines(usage))
            {
                if (!line.StartsWith("<Turn", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var element = XElement.Parse(line);
                    rounds++;
                    prompt += Int(element, "PromptTokens");
                    cached += Int(element, "CachedTokens");
                    completion += Int(element, "CompletionTokens");
                    total += prompt + completion;
                }
                catch (System.Xml.XmlException)
                {
                }
            }
        }

        return new RunFacts
        {
            ToolResults = toolResults,
            ToolDenied = denied,
            AgentOutputs = outputs,
            ToolNames = names,
            Rounds = rounds,
            PromptTokens = prompt,
            CachedTokens = cached,
            CompletionTokens = completion,
            TotalTokens = total,
        };
    }

    private static int Int(XElement element, string name) =>
        int.TryParse(element.Element(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    public static MatchCollection NameMatches(string text) =>
        Regex.Matches(text, @"<name>\s*([^<]+?)\s*</name>", RegexOptions.Singleline);
}

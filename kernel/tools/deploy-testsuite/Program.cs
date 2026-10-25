using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace AgentRuntime.Tools.DeployTest;

/// <summary>
/// 部署测试套件：按 `deploy` 流程在沙盒里装一次，官方 DeepSeek 真机跑一轮，再验证 reset / 搬移。
/// <para>语料 = 部署版（包内 templates/workspace 与 frozen）；二进制 = 当前源码最新（--refresh 先 publish）。</para>
/// 用法：DeployTest [--run] [--package <dir>] [--proj <dir>] [--out <dir>] [--no-refresh] [--keep]
/// </summary>
public static class Program
{
    private static readonly string[] Required =
    [
        "config.xml", "deploy.sh", "run.sh", "run-cli.sh",
        "app/tui/AgentRuntime.Tui.dll", "app/cli/AgentRuntime.Cli.dll", "app/deploy/Deploy.dll",
        "whitebox/workspace/knowledge/knowledge.md",
        "whitebox/workspace/AGENTS.md", "whitebox/workspace/USER.md", "whitebox/workspace/SOUL.md",
        "frozen/knowledge/global.md", "templates/workspace/AGENTS.md",
    ];

    public static int Main(string[] args)
    {
        var proj = Arg(args, "--proj") ?? RepoRoot();
        var package = Arg(args, "--package") ?? Path.Combine(proj, "dist", "alpha-0.1");
        var outDir = Arg(args, "--out") ?? Path.Combine(proj, ".stage1", "deploy-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var run = Flag(args, "--run");
        var refresh = !Flag(args, "--no-refresh");
        var keep = Flag(args, "--keep");

        Console.WriteLine("[deploy-test] 部署测试套件");
        Console.WriteLine($"  proj   : {proj}");
        Console.WriteLine($"  package: {package}");
        Console.WriteLine($"  out    : {outDir}");
        Console.WriteLine($"  refresh: {refresh}（publish 最新二进制）");
        if (!run)
        {
            Console.WriteLine("  D1 包完整性 · D2 init 沙盒 · D3 provider=官方 DS · D4 doctor/check · D5 真机 TUI · D6 取数去重 · D7 reset · D8 搬移");
            Console.WriteLine("  加 --run 才真跑（会调 provider）。");
            return 0;
        }

        if (!Directory.Exists(package))
        {
            Console.Error.WriteLine($"[deploy-test] 找不到部署包：{package}");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var sandbox = Path.Combine(outDir, "sandbox");
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }

        Directory.CreateDirectory(sandbox);
        var pkg = Path.Combine(sandbox, "pkg");
        var target = Path.Combine(sandbox, "target");
        var moved = Path.Combine(sandbox, "moved");
        var rows = new List<Row>();

        if (refresh)
        {
            var rc = Refresh(proj, package);
            if (rc != 0)
            {
                Console.Error.WriteLine("[deploy-test] publish 失败 ⇒ 停");
                return rc;
            }
        }

        CopyTree(package, pkg);
        // 沙盒包不带旧状态
        foreach (var d in new[] { "state" })
        {
            var stale = Path.Combine(pkg, d);
            if (Directory.Exists(stale)) Directory.Delete(stale, true);
        }

        var deploy = Path.Combine(pkg, "app", "deploy", "Deploy.dll");
        var cli = Path.Combine(pkg, "app", "cli", "AgentRuntime.Cli.dll");
        var tui = Path.Combine(pkg, "app", "tui", "AgentRuntime.Tui.dll");

        // D1 包完整性
        var missing = Required.Where(r => !File.Exists(Path.Combine(pkg, r.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        rows.Add(new Row("D1", "包完整性", missing.Count == 0, missing.Count == 0 ? $"{Required.Length} 件齐" : "缺：" + string.Join(", ", missing), 0));

        // D2 init 沙盒
        var (d2rc, d2out) = Exec("dotnet", [deploy, "init", target]);
        rows.Add(new Row("D2", "init 沙盒", d2rc == 0 && File.Exists(Path.Combine(target, "config.xml")), Tail(d2out, 300), 0));

        // D3 provider = 官方 DS
        var (d3rc, d3out) = Exec("dotnet", [deploy, "provider", "--target", target,
            "--base-url", "https://api.deepseek.com", "--model", "deepseek-v4-flash",
            "--api-key-env", "AGENTRUNTIME_API_KEY", "--api-key-file", "~/.agentruntime/api_key",
            "--context-window", "128000"]);
        var cfg = XDocument.Load(Path.Combine(target, "config.xml"));
        var baseUrl = cfg.Root?.Element("BaseUrl")?.Value ?? "";
        var model = cfg.Root?.Element("Model")?.Value ?? "";
        var keyEnv = cfg.Root?.Element("ApiKeyEnv")?.Value ?? "";
        var keyFile = cfg.Root?.Element("ApiKeyFile")?.Value ?? "";
        var providerOk = d3rc == 0 && baseUrl == "https://api.deepseek.com" && model == "deepseek-v4-flash"
                         && keyEnv == "AGENTRUNTIME_API_KEY" && keyFile.Contains("api_key", StringComparison.Ordinal);
        rows.Add(new Row("D3", "provider=官方 DS", providerOk, $"{baseUrl} / {model} / env={keyEnv} / file={keyFile}", 0));

        // D4 doctor + check（部署语料）
        var (docRc, docOut) = Exec("dotnet", [Path.Combine(target, "app", "deploy", "Deploy.dll"), "doctor", "--target", target]);
        var (chkRc, chkOut) = Exec("dotnet", [Path.Combine(target, "app", "deploy", "Deploy.dll"), "check", "--target", target]);
        rows.Add(new Row("D4", "doctor + check", docRc == 0 && chkRc == 0,
            $"doctor={docRc} check={chkRc} · {Tail(chkOut, 220)}", 0));

        // D5 真机 TUI（官方 DS；读 L1 第一条）
        var knFile = Path.Combine(target, "whitebox", "workspace", "knowledge", "knowledge.md");
        var prompt = $"[DTS:v1] 读 {knFile}，找到第一条 `<!--kn: L1-` 标记的节点，逐字回贴它的 id 与正文；只做这一件事。\n";
        var started = DateTime.Now;
        var (tuiRc, tuiOut) = RunTui(tui, Path.Combine(target, "app", "tui"), Path.Combine(target, "config.xml"), prompt, 420);
        var stream = Path.Combine(target, "state", "stream.xml");
        var events = File.Exists(stream) ? ParseEvents(stream) : [];
        var knEvents = events.Count(e => e.Kind == "AgentOutput");
        var toolResults = events.Count(e => e.Kind == "ToolResult");
        var rejects = events.Count(e => e.Kind == "ToolDenied");
        var done = tuiOut.Contains("[DONE]", StringComparison.Ordinal) || events.Any(e => e.Kind == "AgentOutput" && e.Text.Contains("[DONE]", StringComparison.Ordinal));
        var saysId = tuiOut.Contains("L1-", StringComparison.Ordinal);
        var d5ok = tuiRc == 0 && done && saysId && knEvents > 0;
        rows.Add(new Row("D5", "真机 TUI（官方 DS）", d5ok,
            $"rc={tuiRc} 轮={knEvents} 工具={toolResults} 拒={rejects} done={done} id={saysId} · {Math.Round((DateTime.Now - started).TotalSeconds, 1)}s", 0));

        // D6 取数去重（CLI 离线）
        var proof = Path.Combine(sandbox, "kn-proof.xml");
        var (k1rc, k1) = Exec("dotnet", [cli, "--config", Path.Combine(target, "config.xml"), "--stream", proof, "--kn-use", "L3-iso-001"]);
        var (k2rc, k2) = Exec("dotnet", [cli, "--config", Path.Combine(target, "config.xml"), "--stream", proof, "--kn-use", "L3-iso-001"]);
        var proofText = File.Exists(proof) ? File.ReadAllText(proof) : "";
        var firstKn = proofText.Contains("<Kind>Knowledge</Kind>", StringComparison.Ordinal);
        var secondRef = proofText.Contains("kn-ref", StringComparison.Ordinal) && proofText.Contains("<Kind>Hint</Kind>", StringComparison.Ordinal);
        rows.Add(new Row("D6", "取数去重", k1rc == 0 && k2rc == 0 && firstKn && secondRef,
            $"首次Knowledge={firstKn} 二次kn-ref={secondRef} · {Tail(k2, 160)}", 0));

        // D7 reset（知识 / 决策）后 check
        var before = Sha256(knFile);
        File.AppendAllText(knFile, "\n<!-- 测试污染 -->\n");
        var (r1rc, r1) = Exec("dotnet", [Path.Combine(target, "app", "deploy", "Deploy.dll"), "reset", "--target", target, "--all"]);
        var after = Sha256(knFile);
        var (chk2rc, chk2) = Exec("dotnet", [Path.Combine(target, "app", "deploy", "Deploy.dll"), "check", "--target", target]);
        rows.Add(new Row("D7", "reset 还原初始库", r1rc == 0 && before == after && chk2rc == 0,
            $"sha 还原={before == after} check={chk2rc} · {Tail(r1, 160)}", 0));

        // D8 搬移（模拟另一台电脑）
        CopyTree(target, moved);
        var (mvRc, mvOut) = Exec("dotnet", [Path.Combine(moved, "app", "deploy", "Deploy.dll"), "doctor", "--target", moved]);
        var (mvcRc, mvcOut) = Exec("dotnet", [Path.Combine(moved, "app", "deploy", "Deploy.dll"), "check", "--target", moved]);
        rows.Add(new Row("D8", "整目录搬移", mvRc == 0 && mvcRc == 0,
            $"doctor={mvRc} check={mvcRc} · {Tail(mvcOut, 160)}", 0));

        var root = new XElement("DeployTests",
            new XAttribute("out", outDir),
            new XAttribute("package", pkg),
            rows.Select(r => new XElement("Row",
                new XAttribute("id", r.Id),
                new XAttribute("name", r.Name),
                new XAttribute("ok", r.Ok ? "1" : "0"),
                new XElement("Detail", r.Detail))));
        var xmlPath = Path.Combine(outDir, "report.xml");
        File.WriteAllText(xmlPath, new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString() + "\n", new UTF8Encoding(false));

        foreach (var r in rows)
        {
            Console.WriteLine($"{r.Id}\t{(r.Ok ? "PASS" : "FAIL")}\t{r.Name}\t{r.Detail}");
        }

        var passed = rows.Count(r => r.Ok);
        Console.WriteLine($"\n[deploy-test] {passed}/{rows.Count} 通过 · report.xml → {xmlPath}");
        if (!keep)
        {
            // 保留证据（transcript/report），但清掉 1.9 MB 级的沙盒副本，避免堆积。
            TryDelete(Path.Combine(sandbox, "moved"));
            TryDelete(Path.Combine(sandbox, "pkg"));
        }

        return passed == rows.Count ? 0 : 1;
    }

    // ---------------- publish 最新二进制 ----------------

    private static int Refresh(string proj, string package)
    {
        var jobs = new (string Project, string Out)[]
        {
            ("src/AgentRuntime.Tui", "app/tui"),
            ("src/AgentRuntime.Cli", "app/cli"),
            ("tools/deploy", "app/deploy"),
        };

        foreach (var (project, rel) in jobs)
        {
            Console.WriteLine($"[deploy-test] publish {project} → {rel}");
            var (rc, output) = Exec("dotnet", ["publish", Path.Combine(proj, project), "-c", "Release",
                "-o", Path.Combine(package, rel), "--nologo", "-v", "q"]);
            if (rc != 0)
            {
                Console.Error.WriteLine(Tail(output, 1200));
                return rc;
            }
        }

        return 0;
    }

    // ---------------- TUI ----------------

    private static (int Rc, string Text) RunTui(string dll, string cwd, string cfg, string stdinText, int timeoutSec)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfg);
        psi.ArgumentList.Add("--ui");
        psi.ArgumentList.Add("plain");

        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdinText);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(true); } catch { }
            return (-1, "TIMEOUT(" + timeoutSec + "s)");
        }

        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    // ---------------- events ----------------

    private sealed record Ev(string Kind, string Text);

    private static List<Ev> ParseEvents(string path)
    {
        var list = new List<Ev>();
        try
        {
            var root = XElement.Parse("<root>" + File.ReadAllText(path, Encoding.UTF8) + "</root>");
            foreach (var ev in root.Elements())
            {
                list.Add(new Ev(ev.Element("Kind")?.Value ?? "", ev.Element("Text")?.Value ?? ""));
            }
        }
        catch
        {
            // 坏流不抛：由上层判据表现为 0 条。
        }

        return list;
    }

    // ---------------- utils ----------------

    private sealed record Row(string Id, string Name, bool Ok, string Detail, int Rc);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Environment.CurrentDirectory;
    }

    private static string? Arg(string[] a, string name)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == name && i + 1 < a.Length) return a[i + 1];
            if (a[i].StartsWith(name + "=", StringComparison.Ordinal)) return a[i][(name.Length + 1)..];
        }

        return null;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static (int Rc, string Text) Exec(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }

    private static string Tail(string s, int n) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
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

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}

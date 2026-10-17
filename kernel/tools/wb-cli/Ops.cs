// 发布/同步/工具族（原 tools/pre-publish-scan.sh · tools/pack-transfer.sh · whitebox/workspace/scripts/wb-sync.sh · img2jpg.sh）。
using System.Text;
using System.Text.RegularExpressions;

namespace WbCli;

internal static class Ops
{
}

internal static class Scan
{
    private static readonly string[] SkipDirs = [".svn", ".git", "bin", "obj"];

    /// <summary>具名豁免：这些文件本身就是脱敏器/扫描器/规则清单，或测试里的假密钥字面量。</summary>
    private const string AllowRe =
        "benchmark/AgentRuntime\\.Benchmark/PublicCorpusCli\\.cs|benchmark/AgentRuntime\\.Benchmark/LockCorpusCli\\.cs|" +
        "benchmark/tools/build-public-corpus\\.py|CorpusLibrary\\.cs|tools/pre-publish-scan\\.sh|" +
        "tools/pre-publish-patterns\\.txt|tools/build-public-pitfalls\\.py|RuntimeConfigurationTests\\.cs|" +
        "OpenAICompatibleClientTests\\.cs";

    /// <summary>knowledge.md 的**条目标记行**（整行严格匹配）里的 `src:` 字段是归档定位 ⇒ 豁免整行。</summary>
    private const string KnowledgeMarkerRe =
        @"^[^:]+:[0-9]+:<!--l4: [^|]* \| src: /" + @"Users/[^|]* \| ends_with_newline: (true|false) \| mode: (indexed|hybrid) -->$";

    public static int Run(string[] a)
    {
        var patterns = Path.Combine(Repo.Root(), "tools", "pre-publish-patterns.txt");
        if (!File.Exists(patterns))
        {
            Console.Error.WriteLine($"[扫描] ❌ 缺禁字清单：{patterns}（单一来源，不可缺）");
            return 2;
        }

        var pathRules = new List<string>();
        var hard = new List<string>();
        var soft = new List<string>();
        foreach (var raw in File.ReadAllLines(patterns, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) { continue; }
            var f = raw.Split('\t');
            if (f.Length < 3) { continue; }
            switch (f[0])
            {
                case "path": pathRules.Add(f[1]); break;
                case "content": hard.Add(f[1]); break;
                case "soft": soft.Add(f[1]); break;
            }
        }

        if (hard.Count == 0)
        {
            Console.Error.WriteLine($"[扫描] ❌ 禁字清单里没有 content 规则：{patterns}（不许空规则通过）");
            return 2;
        }

        var target = a.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? "";
        if (target.Length == 0 || !Directory.Exists(target))
        {
            Console.Error.WriteLine("用法: wb-cli scan <待发布目录>");
            return 2;
        }

        var hardRe = new Regex(string.Join("|", hard), RegexOptions.Compiled);
        var softRe = new Regex(string.Join("|", soft), RegexOptions.Compiled);
        var allowRe = new Regex(AllowRe, RegexOptions.Compiled);
        var markerRe = new Regex(KnowledgeMarkerRe, RegexOptions.Compiled);
        var fail = 0;

        Console.WriteLine("== 硬红线 · 目录/文件 ==");
        foreach (var bad in pathRules)
        {
            var hits = Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains("/.git/", StringComparison.Ordinal) && Path.GetFileName(p).Contains(bad, StringComparison.Ordinal))
                .Take(5)
                .ToList();
            if (hits.Count > 0)
            {
                Console.WriteLine($"[扫描] ❌ 命中：{bad}");
                foreach (var h in hits) { Console.WriteLine("    " + h); }
                fail = 1;
            }
        }

        Console.WriteLine("== 硬红线 · 内容 ==");
        var hardHits = new List<string>();
        var softHits = new List<string>();
        var shownHard = 0;
        var shownSoft = 0;
        foreach (var file in Enumerate(target))
        {
            var n = 0;
            foreach (var line in ReadLines(file))
            {
                n++;
                var combined = $"{file}:{n}:{line}";
                if (hardRe.IsMatch(line) && !allowRe.IsMatch(combined) && !markerRe.IsMatch(combined))
                {
                    if (shownHard++ < 20) { hardHits.Add(combined); }
                    fail = 1;
                }

                if (softRe.IsMatch(line) && !allowRe.IsMatch(combined) && shownSoft++ < 10)
                {
                    softHits.Add(combined);
                }
            }
        }

        if (hardHits.Count > 0)
        {
            Console.WriteLine("[扫描] ❌ 命中隐私串：");
            foreach (var h in hardHits) { Console.WriteLine("    " + h); }
        }

        Console.WriteLine("== 软提示 · 人名/用户名 ==");
        if (softHits.Count > 0)
        {
            Console.WriteLine("[扫描] ⚠️ 以下位置出现人名/用户名（人工判断是否可公开）：");
            foreach (var s in softHits) { Console.WriteLine("    " + s); }
        }

        Console.WriteLine(fail == 0 ? "[扫描] ✅ 硬红线 0 命中，可发布" : "[扫描] ❌ 禁止发布");
        return fail;
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (SkipDirs.Any(d => f.Contains($"/{d}/", StringComparison.Ordinal))) { continue; }
            yield return f;
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch { yield break; }

        if (text.Contains('\0')) { yield break; }   // 二进制（同 `grep -I`）
        foreach (var l in text.Replace("\r\n", "\n").Split('\n')) { yield return l; }
    }
}

internal static class Pack
{
    public static int Run(string[] a)
    {
        var root = Repo.Root();
        var tier = Repo.Arg(a, "--tier", "A");
        var outDir = Repo.Arg(a, "--out", Path.Combine(root, "dist"));

        var (corpus, cfg) = tier switch
        {
            "A" => ("frozen-private", "config.private.xml"),
            "B" => ("frozen-private-b", "config.private-b.xml"),
            _ => ("", ""),
        };
        if (corpus.Length == 0)
        {
            Console.Error.WriteLine("--tier 只能是 A 或 B");
            return 2;
        }

        var corpusDir = Path.Combine(root, corpus);
        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"缺语料目录 {corpus}（先生成：AgentRuntime.Cli --frozen-corpus whitebox/workspace --frozen-out {corpus} --frozen-tier {tier}）");
            return 2;
        }

        var manifest = File.ReadAllText(Path.Combine(corpusDir, "_manifest.xml"), Encoding.UTF8);
        var m = Regex.Match(manifest, "<Fingerprint>([0-9a-fA-F]*)</Fingerprint>");
        var fp = m.Success ? m.Groups[1].Value[..Math.Min(8, m.Groups[1].Value.Length)] : "";
        Directory.CreateDirectory(outDir);
        var zip = Path.Combine(outDir, $"agentruntime-frozen-{tier}-{fp}.zip");
        if (File.Exists(zip)) { File.Delete(zip); }

        var work = Path.Combine(Path.GetTempPath(), "wbp-" + Guid.NewGuid().ToString("N")[..8]);
        var stage = Path.Combine(work, "agentruntime-frozen");
        Directory.CreateDirectory(stage);
        try
        {
            CopyTree(corpusDir, Path.Combine(stage, corpus));
            CopyTree(Path.Combine(root, "tools", "frozen-build"), Path.Combine(stage, "tools-frozen-build"));
            File.Copy(Path.Combine(root, "src/AgentRuntime.Cli", cfg), Path.Combine(stage, cfg), overwrite: true);
            File.WriteAllText(Path.Combine(stage, "README-先读我.txt"), Readme(tier, corpus, cfg, fp), new UTF8Encoding(false));

            Repo.Run("zip", new[] { "-qr", zip, "agentruntime-frozen" }, cwd: work);
            var sha = Repo.Capture("shasum", new[] { "-a", "256", Path.GetFileName(zip) }, cwd: outDir).Text.Trim();
            File.WriteAllText(zip + ".sha256", sha + "\n", new UTF8Encoding(false));
            Console.WriteLine($"[打包] {zip}");
            Console.WriteLine($"[打包] {sha}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* 忽略 */ }
        }
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        }

        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }

    private static string Readme(string tier, string corpus, string cfg, string fp) =>
        $"""
        AgentRuntime 冻结语料包（{tier} 档，全局指纹 {fp}）

        内容
          {corpus}/                 冻结语料（区/层/域：rules / knowledge / memory-index）+ 账本 _manifest.xml
          tools-frozen-build/      versions.xml（版本账本 + 源锚）；生成器 = AgentRuntime.Cli --frozen-corpus（C#）
          {cfg}                拉起配置（只含路径，不含密钥）

        接收端怎么用
          1) 解到 AgentRuntime 项目根，例如：
               cp -R {corpus} tools-frozen-build ../
          2) 把你的 config 里 frozen.root 指向该目录（或直接用包里的配置改名）
          3) 跑：wb-cli run --config src/AgentRuntime.Cli/{cfg} --verbose "你好"

        注意
          · 语料是**派生文件**，由 AgentRuntime.Cli --frozen-corpus（C#）确定性生成；不要手改。
          · 本包含私有内容（人名 / 本机路径 / 商业信息）——**绝不进 GitHub 或任何公开渠道**。
          · 版本号在 tools-frozen-build/versions.xml；改源后先 --check，再 bump，再重跑。
        """;
}

internal static class Sync
{
    private const string Target = "whitebox-workspace";
    private static readonly string[] Cert = { "--trust-server-cert-failures=unknown-ca,cn-mismatch,expired,not-yet-valid,other", "--non-interactive" };

    private static string WcRoot => Path.GetFullPath(Path.Combine(Repo.Root(), "..", ".."));
    private static string Local => Path.Combine(Repo.Root(), "whitebox", "workspace");
    private static string SvnDir => Path.Combine(WcRoot, Target);

    public static int Run(string[] a)
    {
        var cmd = a.FirstOrDefault() ?? "";
        switch (cmd)
        {
            case "status": return Status();
            case "push": return Push();
            case "pull": return Pull();
            default:
                Console.Error.WriteLine("用法: wb-cli sync status|push|pull");
                return 1;
        }
    }

    private static void Need()
    {
        if (!Directory.Exists(Local)) { Repo.Die("local live copy missing: " + Local); }
        if (Repo.Capture("svn", new[] { "info", "." }, cwd: SvnDir).Rc != 0) { Repo.Die("archive is not an svn working copy: " + SvnDir); }
    }

    private static int Status()
    {
        Need();
        Console.WriteLine("== update archive to HEAD first ==");
        Svn("update", Target);
        Console.WriteLine("== diff: local live copy vs SVN archive ==");
        var (_, outText) = Repo.Capture("diff", new[] { "-rq", "--exclude=.svn", "--exclude=.DS_Store", Local, SvnDir }, cwd: WcRoot);
        Console.WriteLine(outText.Trim().Length == 0 ? "identical: no drift." : outText.TrimEnd());
        return 0;
    }

    private static int Push()
    {
        Need();
        Console.WriteLine("== [1/4] mirror local -> SVN archive (rsync --delete，worker/ 只增不删) ==");
        Repo.Run("rsync", new[] { "-a", "--exclude=.svn", "--exclude=.DS_Store", Local + "/", SvnDir + "/" }, cwd: WcRoot);
        Repo.Run("rsync", new[] { "-a", "--delete", "--exclude=.svn", "--exclude=.DS_Store", "--exclude=worker/", Local + "/", SvnDir + "/" }, cwd: WcRoot);
        Console.WriteLine("== [2/4] svn add（输出不吞）==");
        var (rcAdd, outAdd) = Svn(["add", "--force", "--parents", Target]);
        Console.Write(outAdd);
        if (rcAdd != 0) { Console.Error.WriteLine($"❌ svn add rc={rcAdd}"); return 1; }
        Console.WriteLine("== [3/4] svn status ==");
        var (_, st) = Svn("status", Target);
        Console.WriteLine(st.TrimEnd());
        if (st.Trim().Length == 0) { Console.WriteLine("no change, skip commit."); return 0; }
        Console.WriteLine("== [4/4] commit ==");
        var msg = Path.GetTempFileName();
        File.WriteAllText(msg, $"WB workspace sync\nlocal: {Local}\nsvn:   ^/software-company/{Target}\n", new UTF8Encoding(false));
        Svn3(["commit", Target, "-F", msg]);
        File.Delete(msg);
        Console.WriteLine("== verify: status should be empty, update should reach HEAD ==");
        Svn("status", Target);
        Svn("update", Target);
        return 0;
    }

    private static int Pull()
    {
        Need();
        Console.WriteLine("== archive -> local: fill missing only ==");
        Svn("update", Target);
        Repo.Run("rsync", new[] { "-a", "--ignore-existing", "--exclude=.svn", "--exclude=.DS_Store", SvnDir + "/", Local + "/" }, cwd: WcRoot);
        Console.WriteLine("== done ==");
        return 0;
    }

    private static (int, string) Svn(params string[] args) => Svn3(args);

    private static (int, string) Svn3(string[] args)
    {
        var list = new List<string>(args);
        list.AddRange(Cert);
        return Repo.Capture("svn", list, cwd: WcRoot);
    }
}

internal static class Img2Jpg
{
    public static int Run(string[] a)
    {
        if (a.Length == 0 || a[0].Length == 0)
        {
            Console.WriteLine("用法: wb-cli img2jpg <图片> [宽度] [质量]");
            return 1;
        }

        var src = a[0];
        var width = a.Length > 1 ? a[1] : "1200";
        var quality = a.Length > 2 ? a[2] : "70";
        if (!File.Exists(src))
        {
            Console.WriteLine("文件不存在: " + src);
            return 1;
        }

        var stem = Path.Combine(Path.GetDirectoryName(src) ?? ".", Path.GetFileNameWithoutExtension(src));
        var ext = Path.GetExtension(src).TrimStart('.').ToLowerInvariant();
        if (ext is "jpg" or "jpeg")
        {
            var outp = stem + ".compressed.jpg";
            Repo.Run("sips", new[] { "--resampleWidth", width, src, "--out", outp });
            Console.WriteLine($"✅ 已压缩: {outp} (质量保持, 宽 {width})");
        }
        else
        {
            var outp = stem + ".jpg";
            Repo.Run("sips", new[] { "-s", "format", "jpeg", "-s", "formatOptions", quality, "--resampleWidth", width, src, "--out", outp });
            Console.WriteLine($"✅ 已转换: {outp} (质量 {quality}, 宽 {width})");
        }

        var (_, ls) = Repo.Capture("ls", new[] { "-lh", src, stem + ".jpg", stem + ".compressed.jpg" });
        foreach (var line in ls.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 9) { Console.WriteLine($"{f[4]} {f[^1]}"); }
        }

        return 0;
    }
}

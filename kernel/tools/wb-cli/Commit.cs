namespace WbCli;

/// <summary>
/// **提交并回读**（动机 = 坑 #317：`svn add … &gt;/dev/null 2&gt;&amp;1` 把错误吞了、整块新目录静默漏库）。
///
/// <para>把「<c>add</c> 输出不吞 + 提交后<strong>回读改动路径清单</strong>并自证已入库」做成一条命令，
/// 不让它停留在「纪律」层：</para>
/// <list type="number">
/// <item><c>svn add --parents --force</c> —— 输出**照打、rc 照查**（失败即停，绝不带病提交）；</item>
/// <item><c>svn status</c>（提交前）；</item>
/// <item><c>svn commit -F &lt;msgfile&gt;</c>（提交信息一律走文件，避免 shell 吃引号）；</item>
/// <item>回读 <c>svn log -v -r N</c> 的改动路径清单，并逐路径 <c>svn info/status</c> 自证已入库。</item>
/// </list>
/// </summary>
internal static class Commit
{
    private static readonly string[] Cert =
        ["--trust-server-cert-failures=unknown-ca,cn-mismatch,expired,not-yet-valid,other", "--non-interactive"];

    public static int Run(string[] a)
    {
        var msgFile = Repo.Arg(a, "-F");
        if (msgFile.Length == 0 || !File.Exists(msgFile))
        {
            Console.Error.WriteLine("用法: wb-cli commit -F <提交信息文件> [--no-add] <路径…>");
            Console.Error.WriteLine("  · 提交信息一律走文件（-F）—— 内联时引号/反引号会被 shell 吃掉");
            Console.Error.WriteLine("  · 路径必须**显式列出**（不给 = 全库乱提）；相对路径按仓根解析");
            return 2;
        }

        var noAdd = Repo.Flag(a, "--no-add");
        var paths = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "-F") { i++; continue; }
            if (a[i].StartsWith('-')) { continue; }
            paths.Add(a[i]);
        }

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("ERROR: 必须显式列出要提交的路径（不给 = 提交全库所有改动）");
            return 2;
        }

        // ① add —— **输出不吞**（#317 的病灶就在这一步被 `>/dev/null` 吞掉了错误）
        if (!noAdd)
        {
            Console.WriteLine("== [1/4] svn add（输出不吞）==");
            // **不用 --parents**：它会**静默创建缺失的父目录**（拼错路径 ⇒ 凭空长出一堆空目录）。
            // 新增单个文件 / 整个新目录都无需它；拼错就该**报错**而不是造垃圾。
            var (rcA, outA) = Svn(["add", "--force", .. paths]);
            Console.Write(outA);
            if (rcA != 0)
            {
                Console.Error.WriteLine($"❌ svn add 失败 rc={rcA} ⇒ **不继续提交**（未纳版就提交 = 静默漏库，坑 #317）");
                return 1;
            }
        }

        Console.WriteLine("== [2/4] svn status（提交前）==");
        Console.Write(Svn(["status", .. paths]).Out);

        // ③ commit
        Console.WriteLine("== [3/4] svn commit ==");
        var (rcC, outC) = Svn(["commit", "-F", msgFile, .. paths]);
        Console.Write(outC);
        if (rcC != 0)
        {
            Console.Error.WriteLine($"❌ svn commit rc={rcC}");
            return 1;
        }

        // ④ 回读：改动路径清单 + 逐路径自证。
        // 版本号用 `--show-item last-changed-revision`（**纯数字、与 locale 无关**）——
        // 解析 commit 输出里的 "revision N" 在中文 locale 下会落空。
        Console.WriteLine("== [4/4] 回读提交路径清单 ==");
        var rev = Svn(["info", "--show-item", "last-changed-revision", paths[0]]).Out.Trim();
        if (rev.Length > 0 && rev.All(char.IsDigit))
        {
            // `svn log` 的**工作副本路径只能给一个**（多个 ⇒ E200007）⇒ 用 WC 根作单一目标拿全量改动路径。
            var wcRoot = Svn(["info", "--show-item", "wc-root", paths[0]]).Out.Trim();
            var logTarget = wcRoot.Length > 0 ? wcRoot : paths[0];
            var (rcL, outL) = Svn(["log", "-v", "-c", rev, logTarget]);
            Console.Write(outL);
            if (rcL != 0)
            {
                Console.Error.WriteLine($"⚠️ 拿不到改动路径清单 rc={rcL}（逐路径自证仍继续）");
            }
        }

        var bad = new List<string>();
        foreach (var p in paths)
        {
            var (rcI, _) = Svn(["info", p]);
            var (_, st) = Svn(["status", p]);
            if (rcI != 0 || st.Trim().Length > 0)
            {
                bad.Add(p);
            }
        }

        if (bad.Count > 0)
        {
            Console.Error.WriteLine($"❌ 回读不一致：{string.Join("，", bad)} —— 提交后仍「未纳版 / 有未提交改动」");
            return 1;
        }

        Console.WriteLine("✅ 回读一致：所列路径均已入库且工作副本干净");
        return 0;
    }

    private static (int Rc, string Out) Svn(string[] args) => Repo.Capture("svn", [.. args, .. Cert]);
}

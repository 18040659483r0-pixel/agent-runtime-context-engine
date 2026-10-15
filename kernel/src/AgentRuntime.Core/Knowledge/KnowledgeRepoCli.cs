using System.Globalization;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using D = AgentRuntime.Core.Skill;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **知识库维护（C# 版）** —— B4-b：把 <c>tools/knowledge-repo/knowledge-repo.py</c>（545 行）搬进运行时。
///
/// <para><b>为什么要搬</b>：收尾流水线里这条是 python ⇒ 只能由远端 AI 点 <c>exec</c> 跑（铁则 R2：运行时唯一能起进程处 = 工具面）。
/// 搬成 C# ⇒ **运行时自己在进程内重建派生 / 判定 / 晋级**。判据 = **与 python 产物逐字节等价**。</para>
///
/// <para><b>形态</b>：<c>knowledge/</c> = <c>knowledge.md</c>（唯一活版）· <c>versions/</c>（只追加快照）·
/// <c>.derived/</c>（可重建派生）· <c>.ledger.xml</c>（账本，不进 prompt）。纪律 K1~K6。</para>
///
/// <para><b>本轮范围（切片 1/5 · <c>--derive-only</c>）</b>：<see cref="ParseMd"/> / <see cref="ParseCategories"/> /
/// <see cref="Derive"/> / <see cref="WriteDerived"/> + 往返三态。<c>--check</c> / <c>--closeout</c> / <c>--promote</c> /
/// <c>--rollback</c> 待后续切片（本片先如实报未实现）。</para>
/// </summary>
public static class KnowledgeRepoCli
{
    // ---- 常量（与 py 逐一对齐）----
    internal const double BytesPerToken = 3.36;
    internal const string MdName = "knowledge.md";
    internal const string LedgerName = ".ledger.xml";

    /// <summary>账本 schema 标识（与旧 JSON 同值，迁移不改语义）。</summary>
    internal const string LedgerSchema = "knowledge-repo/ledger/1";
    internal const string DerivedDir = ".derived";
    internal const string VersionsDir = "versions";

    /// <summary>条头 id：**层级前缀不限**（<c>S-*</c> / <c>L3-*</c> / <c>L4-*</c>）。</summary>
    internal const string IdPat = @"[A-Za-z0-9]+-[A-Za-z0-9]+-\d{3}";

    private static readonly Regex H_Skill = new(@"^## (\S+)\s*$", RegexOptions.Compiled);

    private static readonly Regex Mark = new(
        @"^<!--\s*skill:\s*(?<name>[^|]+?)\s*\|\s*src:\s*(?<src>[^|]+?)\s*\|\s*ends_with_newline:\s*(?<en>true|false)\s*\|\s*mode:\s*(?<mode>\S+)\s*-->\s*$",
        RegexOptions.Compiled);

    private static readonly Regex L2RowRx = new(
        "^- \\[([a-z]+)\\] (.+?) → 见 L3 ((" + IdPat + @")(?:\s+" + IdPat + @")*)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex L1LineRx = new(@"^\*\*L1 法则\*\*：(.+)$", RegexOptions.Compiled);

    private static readonly Regex CatStart = new(@"^<!--cat (S-[a-z0-9]+-\d{3}) (.+?)-->", RegexOptions.Compiled);
    private static readonly Regex CatWhat = new(@"^- \*\*这一类是什么\*\*：(.*)", RegexOptions.Compiled);
    private static readonly Regex CatNot = new(@"^- \*\*不覆盖\*\*：(.*)", RegexOptions.Compiled);
    private static readonly Regex CatRoute = new(@"^- (.+?) → (L4-[a-z0-9]+-\d{3})", RegexOptions.Compiled);

    private static readonly Regex WrapHead = new(@"^\n<!--wrap: (\d+) (\d+)-->\n\n", RegexOptions.Compiled);
    private static readonly Regex Frontmatter = new("```yaml\n(.*?)\n```\n", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex StripHit = new("^### (" + IdPat + @") (.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ShortName = new(@"^S-([A-Za-z0-9]+)-", RegexOptions.Compiled);

    // ---- 数据（OOP：状态与行为同属一个类型）----

    /// <summary>一条 L2 问题：域 + 问题形状 + 指向的 L3/L4 条 id。</summary>
    internal sealed record L2Row(string Domain, string Q, IReadOnlyList<string> Ids);

    /// <summary>一个 L4 切片（md 里的 <c>### &lt;id&gt; 标题</c> 块，正文 = 原 SKILL 逐字）。</summary>
    internal sealed record Strip(string Id, string Title, int Seq, string Text, int MdLine);

    /// <summary>一个技能块（<c>## 名称</c> + skill 标记注释 + frontmatter + L1 + L2 行 + 切片）。</summary>
    internal sealed record Skill(
        string Name, string Src, bool EndsWithNewline, string Mode, string Frontmatter,
        string L1, IReadOnlyList<L2Row> L2, IReadOnlyList<Strip> Strips, int MdLine);

    /// <summary>L3 分类总目的一条路由。</summary>
    internal sealed record Route(string When, string L4);

    /// <summary>L3 分类（「L3 = 分类总目 + 路由」）。</summary>
    internal sealed record Category(string Id, string Title, string What, string Not, IReadOnlyList<Route> Routes);

    /// <summary>往返三态的一行（equal / source_missing（正常态）/ mismatch）。</summary>
    internal sealed record RtRow(string Skill, string Source, string State, string? SourceSha256, string RebuiltSha256, bool? Equal);

    /// <summary>派生结果：8 个文件（名 → 字节）+ 往返行。</summary>
    internal sealed record Derived(IReadOnlyList<KeyValuePair<string, byte[]>> Files, IReadOnlyList<RtRow> Roundtrip);

    // ---------------------------------------------------------------- 解析

    /// <summary>技能头 = <c>## 名称</c>（无空格 token）**且下一行是 skill 标记注释** ——
    /// 否则它是**源正文里**的 markdown 标题（原 SKILL 文本里本来就有）。</summary>
    internal static bool IsSkillHeader(string[] lines, int i) =>
        H_Skill.IsMatch(lines[i]) && i + 1 < lines.Length && Mark.IsMatch(lines[i + 1]);

    /// <summary><c>knowledge.md</c> 字节 → 技能块（顺序 = md 中的顺序）。</summary>
    internal static IReadOnlyList<Skill> ParseMd(byte[] mdBytes)
    {
        var text = Encoding.UTF8.GetString(mdBytes);
        var lines = text.Split('\n');
        var heads = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsSkillHeader(lines, i))
            {
                heads.Add(i);
            }
        }

        var skills = new List<Skill>(heads.Count);
        for (var k = 0; k < heads.Count; k++)
        {
            var i = heads[k];
            var end = k + 1 < heads.Count ? heads[k + 1] : lines.Length;
            var block = string.Join("\n", lines.Skip(i).Take(end - i));
            var name = H_Skill.Match(lines[i]).Groups[1].Value;
            var mk = Mark.Match(lines[i + 1]);
            var mfm = Frontmatter.Match(block);

            var l1 = string.Empty;
            var l2 = new List<L2Row>();
            foreach (var ln in block.Split('\n'))
            {
                var ml1 = L1LineRx.Match(ln);
                if (ml1.Success)
                {
                    l1 = ml1.Groups[1].Value;
                }

                var m2 = L2RowRx.Match(ln);
                if (m2.Success)
                {
                    l2.Add(new L2Row(m2.Groups[1].Value, m2.Groups[2].Value,
                        m2.Groups[3].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                }
            }

            var strips = new List<Strip>();
            var hits = StripHit.Matches(block);
            for (var n = 0; n < hits.Count; n++)
            {
                var h = hits[n];
                var segEnd = n + 1 < hits.Count ? hits[n + 1].Index : block.Length;
                var seg = block.Substring(h.Index + h.Length, segEnd - (h.Index + h.Length));
                strips.Add(new Strip(h.Groups[1].Value, h.Groups[2].Value.Trim(), n + 1,
                    StripText(seg), i + 1 + block[..h.Index].Count(c => c == '\n') + 1));
            }

            skills.Add(new Skill(name, mk.Groups["src"].Value.Trim(), mk.Groups["en"].Value == "true",
                mk.Groups["mode"].Value, mfm.Success ? mfm.Groups[1].Value + "\n" : string.Empty,
                l1, l2, strips, i + 1));
        }

        return skills;
    }

    /// <summary>切片正文：对称剥掉写者固定加的换行（<c>&lt;!--wrap: a b--&gt;</c>）或裸剥 1 前 / 2 后。</summary>
    private static string StripText(string seg)
    {
        var mw = WrapHead.Match(seg);
        if (mw.Success)
        {
            var core = seg[mw.Length..];
            if (core.EndsWith("\n\n", StringComparison.Ordinal))
            {
                core = core[..^2];
            }
            else if (core.EndsWith('\n'))
            {
                core = core[..^1];
            }

            return new string('\n', int.Parse(mw.Groups[1].Value, CultureInfo.InvariantCulture)) + core
                 + new string('\n', int.Parse(mw.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        if (seg.StartsWith('\n'))
        {
            seg = seg[1..];
        }

        if (seg.EndsWith("\n\n", StringComparison.Ordinal))
        {
            seg = seg[..^2];
        }
        else if (seg.EndsWith('\n'))
        {
            seg = seg[..^1];
        }

        return seg;
    }

    /// <summary>从 md 的「L3 分类总目」段解析分类（空 <c>这一类是什么</c> 的丢弃）。</summary>
    internal static IReadOnlyList<Category> ParseCategories(string mdText)
    {
        var cats = new List<Category>();
        string? id = null, title = null, what = null, not = null;
        var routes = new List<Route>();

        void Flush()
        {
            if (id is not null && !string.IsNullOrEmpty(what))
            {
                cats.Add(new Category(id, title ?? string.Empty, what!, not ?? string.Empty, routes));
            }

            id = null;
            title = null;
            what = null;
            not = null;
            routes = [];
        }

        foreach (var ln in mdText.Split('\n'))
        {
            var m = CatStart.Match(ln.Trim());
            if (m.Success)
            {
                Flush();
                id = m.Groups[1].Value;
                title = m.Groups[2].Value;
                continue;
            }

            if (id is null)
            {
                continue;
            }

            var m2 = CatWhat.Match(ln);
            if (m2.Success)
            {
                what = m2.Groups[1].Value.Trim();
                continue;
            }

            var m3 = CatNot.Match(ln);
            if (m3.Success)
            {
                not = m3.Groups[1].Value.Trim();
                continue;
            }

            var m4 = CatRoute.Match(ln);
            if (m4.Success)
            {
                routes.Add(new Route(m4.Groups[1].Value.Trim(), m4.Groups[2].Value));
            }
        }

        Flush();
        return cats;
    }

    // ---------------------------------------------------------------- 派生

    /// <summary>从 md 结构派生全部 <c>.derived/</c> 文件（确定性 —— 同输入同产物）。</summary>
    internal static Derived Derive(IReadOnlyList<Skill> skills, string mdText)
    {
        var l1 = new List<D.L1Row>();
        var l2 = new List<D.L2Row>();
        var l4 = new List<D.StripRow>();
        var dom = new Dictionary<string, (string Domain, List<string> Skills, int Questions, List<string> Ids)>(StringComparer.Ordinal);
        var rt = new List<RtRow>();

        foreach (var sk in skills)
        {
            l1.Add(new D.L1Row { Skill = sk.Name, Text = sk.L1 });

            var rebuilt = Encoding.UTF8.GetBytes(
                sk.Frontmatter + string.Join("\n", sk.Strips.Select(s => s.Text)) + (sk.EndsWithNewline ? "\n" : string.Empty));
            var rebuiltSha = Sha256Hex(rebuilt);
            l4.Add(new D.StripRow
            {
                Id = "L4-" + ShortOf(sk.Strips) + "-001",
                Skill = sk.Name,
                Seq = 1,
                Src = sk.Src,
                Bytes = rebuilt.Length,
                Sha256 = rebuiltSha,
                Text = Encoding.UTF8.GetString(rebuilt),
            });

            foreach (var row in sk.L2)
            {
                l2.Add(new D.L2Row
                {
                    Skill = sk.Name,
                    Domain = row.Domain,
                    Question = row.Q,
                    Ids = [.. row.Ids],
                });

                // 注意：py 的 `d["questions"] += 1` 在 **`for i_ in row["ids"]`** 内 ⇒ 计数是
                // （行 × id）对的个数，不是行数（多 id 的行会各计）。
                foreach (var i_ in row.Ids)
                {
                    if (!dom.TryGetValue(row.Domain, out var d))
                    {
                        d = (row.Domain, [], 0, []);
                    }

                    if (!d.Skills.Contains(sk.Name))
                    {
                        d.Skills.Add(sk.Name);
                    }

                    d.Questions++;
                    if (!d.Ids.Contains(i_))
                    {
                        d.Ids.Add(i_);
                    }

                    dom[row.Domain] = d;
                }
            }

            string state;
            string? srcSha;
            bool? equal;
            if (!File.Exists(sk.Src))
            {
                (state, srcSha, equal) = ("source_missing", null, null);
            }
            else
            {
                var cur = File.ReadAllBytes(sk.Src);
                if (cur.AsSpan().SequenceEqual(rebuilt))
                {
                    (state, srcSha, equal) = ("equal", rebuiltSha, true);
                }
                else
                {
                    (state, srcSha, equal) = ("mismatch", Sha256Hex(cur), false);
                }
            }

            rt.Add(new RtRow(sk.Name, sk.Src, state, srcSha, rebuiltSha, equal));
        }

        // **L3 = 分类总目 + 路由**：覆盖上面那批逐条切片（后者只用于 L4 / 往返）。
        var cats = ParseCategories(mdText);

        var catIds = cats.Select(c => new D.IdRow
        {
            Id = c.Id,
            Skill = c.Title,
            Seq = 1,
            Title = c.Title,
            Kind = "Category",
        }).ToArray();

        var l3 = cats.Select(c => new D.StripRow
        {
            Id = c.Id,
            Title = c.Title,
            Kind = "Category",
            What = c.What,
            Not = c.Not,
            Routes = [.. c.Routes.Select(r => new D.StripRoute { When = r.When, L4 = r.L4 })],
        }).ToArray();

        var rtCounts = RtCounts(rt);

        var roundtrip = new D.RoundtripDoc
        {
            Equal = rtCounts.Equal,
            SourceMissing = rtCounts.Missing,
            Mismatch = rtCounts.Mismatch,
            Total = rt.Count,
            Skills = [.. rt.Select(r => new D.RoundtripSkill
            {
                Skill = r.Skill,
                Source = r.Source,
                State = r.State,
                SourceSha256 = r.SourceSha256,
                RebuiltSha256 = r.RebuiltSha256,
                Equal = r.Equal,
            })],
        };

        var summary = new D.SummaryDoc
        {
            Skills = skills.Count,
            L3Entries = l3.Length,
            L2Questions = l2.Count,
            L1Bytes = skills.Sum(s => Encoding.UTF8.GetByteCount(s.L1 + "\n")),
            L2Bytes = skills.Sum(s => s.L2.Sum(r => Encoding.UTF8.GetByteCount(
                $"- [{r.Domain}] {r.Q} → 见 L3 {string.Join(' ', r.Ids)}\n"))),
            BytesPerToken = KnowledgeRepoCli.BytesPerToken,
        };

        var domainsFrag = Fragments(dom.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new D.DomainRow
        {
            Domain = dom[k].Domain,
            Skills = [.. dom[k].Skills],
            Questions = dom[k].Questions,
            Ids = [.. dom[k].Ids],
        }));

        var files = new List<KeyValuePair<string, byte[]>>
        {
            Pair("ids.xml", Fragments(catIds)),
            Pair("l3.xml", Fragments(l3)),
            Pair("l4.xml", Fragments(l4)),
            Pair("l1.xml", Fragments(l1)),
            Pair("l2.xml", Fragments(l2)),
            Pair("domains.xml", domainsFrag),
            Pair("roundtrip.xml", Document(roundtrip)),
            Pair("summary.xml", Document(summary)),
        };

        return new Derived(files, rt);
    }

    /// <summary>写 <c>.derived/</c>（UTF-8 无 BOM，LF —— 字节口径与 py 同）。</summary>
    internal static void WriteDerived(string outPath, IReadOnlyList<KeyValuePair<string, byte[]>> files)
    {
        var dir = Path.Combine(outPath, DerivedDir);
        Directory.CreateDirectory(dir);
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, name), data);
        }
    }

    // ---------------------------------------------------------------- 账本 / 判定

    /// <summary>账本里版本快照的一条（check 只需 file + sha256 + version）。</summary>
    internal sealed record VersionRec(int Version, string File, string Sha256);

    /// <summary>账本读出的最小事实（K5：指纹一律重算，账本里的值只作**比对基准**）。</summary>
    internal sealed record LedgerInfo(List<VersionRec> Versions, string? CurrentSha256);

    /// <summary>三态判定的结果（与 py 的 <c>judge</c> 同；<see cref="Diff"/> 只在态 B 非空）。</summary>
    internal sealed record JudgeResult(string State, string MdSha, long MdBytes, VersionRec? Last, string? LedgerCurrentSha, string Diff);

    /// <summary>态 → 为什么（与 py 的三句逐字同）。</summary>
    internal static string Why(string state) => state switch
    {
        "C" => "md 与最新版本快照一致 ⇒ 无变化，不产生空版本",
        "A" => "md 与账本记录的最近一次写入一致，但尚未晋级 ⇒ 可晋级",
        _ => "md 指纹与账本记录不一致 ⇒ 疑似**外部手改**（未记账的写入）",
    };

    internal static LedgerInfo LoadLedgerInfo(string outDir)
    {
        var p = Path.Combine(outDir, LedgerName);
        if (!File.Exists(p))
        {
            return new LedgerInfo([], null);
        }

        var doc = Persistence.DataContractXml.ReadFile<LedgerDoc>(p);
        var versions = doc.Versions
            .Select(v => new VersionRec(v.Version ?? 0, v.File ?? string.Empty, v.Sha256))
            .ToList();
        return new LedgerInfo(versions, doc.Current?.Sha256);
    }

    /// <summary>三态判定（K5：一切指纹**重算**）。</summary>
    internal static JudgeResult Judge(string outDir, byte[] md, LedgerInfo led)
    {
        var mdSha = Sha256Hex(md);
        VersionRec? last = null;
        if (led.Versions.Count > 0)
        {
            var v = led.Versions[^1];
            var f = Path.Combine(outDir, v.File);
            if (File.Exists(f))
            {
                last = v with { Sha256 = Sha256Hex(File.ReadAllBytes(f)) };
            }
        }

        var state = last is not null && mdSha == last.Sha256 ? "C"
                  : led.CurrentSha256 == mdSha ? "A" : "B";

        // 态 B：给出「上版 → 当前」的 unified diff（difflib 口径），前 400 行。
        var diff = string.Empty;
        if (state == "B" && last is not null)
        {
            var oldLines = PyDiff.SplitLinesKeepEnds(
                Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(outDir, last.File))));
            var newLines = PyDiff.SplitLinesKeepEnds(Encoding.UTF8.GetString(md));
            var dl = PyDiff.UnifiedDiff(oldLines, newLines, $"versions/{Path.GetFileName(last.File)}", "knowledge.md", 2);
            diff = string.Concat(dl.Take(400));
        }

        return new JudgeResult(state, mdSha, md.Length, last, led.CurrentSha256, diff);
    }

    // ---------------------------------------------------------------- 入口

    /// <summary>切片 1/2：认 <c>derive</c>（--derive-only）与 <c>check</c>（--check）；其余如实报「未移植」（不静默）。</summary>
    public static int Run(string outPath, string mode, string? rollback, bool acceptExternal,
        string pitfallsSrc = "projects/AgentRuntime/docs/PITFALLS.md", string handoffSrc = "workspace/handoff", string pitfallsProj = "rt")
    {
        _ = rollback;
        _ = acceptExternal;
        var outDir = Path.GetFullPath(outPath);
        Directory.CreateDirectory(outDir);

        if (mode == "derive")
        {
            var md = File.ReadAllBytes(Path.Combine(outDir, MdName));
            var derived = Derive(ParseMd(md), Encoding.UTF8.GetString(md));
            WriteDerived(outDir, derived.Files);
            var c = RtCounts(derived.Roundtrip);
            Console.WriteLine($"[DERIVE] 已从 {MdName} 重建 {DerivedDir}/（{derived.Files.Count} 个文件；"
                            + $"往返 equal {c.Equal} / missing {c.Missing} / mismatch {c.Mismatch}）");
            return 0;
        }

        if (mode == "check")
        {
            return Check(outDir, pitfallsSrc, handoffSrc, pitfallsProj);
        }

        if (mode == "closeout")
        {
            return Closeout(outDir);
        }

        if (mode == "promote")
        {
            return Promote(outDir, acceptExternal);
        }

        if (mode == "rollback")
        {
            return Rollback(outDir, rollback ?? string.Empty);
        }

        Console.Error.WriteLine($"[FAIL] --knowledge-repo 的动作「{mode}」不认识（可选：--derive-only / --check / --closeout / --promote / --rollback <ver>）。");
        return 2;
    }

    /// <summary><c>--closeout</c>：三态判定（不写 md / 不建版本）；K1 ⇒ **连「只判定」也留痕**（账本 append 一条）。</summary>
    private static int Closeout(string outDir)
    {
        var md = File.ReadAllBytes(Path.Combine(outDir, MdName));
        var led = LoadLedgerInfo(outDir);
        var j = Judge(outDir, md, led);
        var doc = LoadLedgerDoc(outDir);

        Console.WriteLine($"[CLOSEOUT] 态 {j.State}：{Why(j.State)}");
        Console.WriteLine($"[CLOSEOUT] md sha256 = {j.MdSha}（{j.MdBytes} 字节，**重算**）");
        Console.WriteLine($"[CLOSEOUT] 上版 = {(j.Last is null ? "（无）" : j.Last.File)}（sha {(j.Last?.Sha256 ?? "-")}）");
        Console.WriteLine($"[CLOSEOUT] 账本 current.sha256 = {j.LedgerCurrentSha}");

        if (j.State == "A")
        {
            var next = led.Versions.Count > 0 ? led.Versions[^1].Version + 1 : 1;
            Console.WriteLine($"[CLOSEOUT] 建议：`--promote` 晋级为 v{next:D4}");
        }
        else if (j.State == "B")
        {
            Console.WriteLine("[CLOSEOUT] 建议：**由 TUI 询问用户**（工具自身不交互）—— ① 接受本次手改 ⇒ `--promote --accept-external`；"
                            + $"② 丢弃手改 ⇒ `--rollback {(j.Last?.Version ?? 0):D4}`；③ 先看下面的 diff 再定。");
            Console.WriteLine("[CLOSEOUT] --- unified diff（上版 → 当前）---");
            Console.WriteLine(j.Diff.Length > 0 ? j.Diff : "（无 diff）");
        }
        else
        {
            Console.WriteLine("[CLOSEOUT] 建议：无需动作（不产生空版本）");
        }

        AppendCloseout(doc, new LedgerCloseout
        {
            At = NowStamp(),
            State = j.State,
            MdSha256 = j.MdSha,
            PrevVersion = j.Last?.Version,
            PrevSha256 = j.Last?.Sha256,
            Action = "reported-only",
        });
        SaveLedgerDoc(outDir, doc);
        return 0;
    }

    /// <summary><c>--promote</c>：晋级为新的版本快照（K6：只有它写 <c>versions/</c>）。</summary>
    private static int Promote(string outDir, bool acceptExternal)
    {
        var md = File.ReadAllBytes(Path.Combine(outDir, MdName));
        var j = Judge(outDir, md, LoadLedgerInfo(outDir));
        if (j.State == "C")
        {
            Console.WriteLine("[PROMOTE] 拒绝：无变化（md == 最新版本）⇒ **不产生空版本**");
            return 0;
        }

        if (j.State == "B" && !acceptExternal)
        {
            Console.WriteLine("[PROMOTE] 拒绝：当前是**外部手改（态 B）**，未经用户裁决 ⇒ 先 `--closeout` 交给 TUI 询问；"
                            + "用户确认接受后加 `--accept-external` 晋级，或 `--rollback <ver>` 丢弃");
            return 1;
        }

        // 前置：晋级前先证「派生能从活版重建、且逐字节相等」（与 py 同源）。
        // 2026-10-11 口径修正：此前**没传 md_text** ⇒ 重算的 l3/ids 恒为空（分类总目来自 md 正文）
        // ⇒ 与磁盘上（有分类）永不相等 ⇒ 前置**恒红**、promote 永远拒晋。现与 `--check`/`--rollback` 同源。
        var derived = Derive(ParseMd(md), Encoding.UTF8.GetString(md));
        var same = derived.Files.All(f =>
        {
            var p = Path.Combine(outDir, DerivedDir, f.Key);
            return File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(f.Value);
        });
        var mm = derived.Roundtrip.Where(r => r.State == "mismatch").Select(r => r.Skill).ToList();
        if (!same || mm.Count > 0)
        {
            Console.Error.WriteLine($"[PROMOTE] 拒绝：**派生不一致**（派生文件{(same ? "一致" : "有差异")} / 往返 mismatch {mm.Count}）⇒ 拒绝晋级；"
                                  + "跑一次 `--check` 看是哪几处，修好再晋。");
            return 1;
        }

        var source = j.State == "B" ? "promote-external" : "promote";
        var (doc, rec) = Snapshot(outDir, md, LoadLedgerDoc(outDir), source, $"收尾晋级（态 {j.State}）");
        SaveLedgerDoc(outDir, doc);
        Console.WriteLine($"[PROMOTE] 已晋级：v{rec.Version ?? 0:D4} → {rec.File}"
                        + $"（md sha {rec.Sha256[..16]}；来源：{rec.Origin}）");
        return 0;
    }

    /// <summary><c>--rollback &lt;ver&gt;</c>：把旧版**重新晋级**为新版（K2：历史不动、只追加）。</summary>
    private static int Rollback(string outDir, string want)
    {
        if (!int.TryParse(want, out var ver))
        {
            Console.Error.WriteLine($"[FAIL] 版本号不是整数：{want}");
            return 2;
        }

        var led = LoadLedgerInfo(outDir);
        var rec = led.Versions.FirstOrDefault(v => v.Version == ver);
        if (rec is null)
        {
            Console.Error.WriteLine($"[FAIL] 版本不存在：v{ver:D4}");
            return 2;
        }

        var md = File.ReadAllBytes(Path.Combine(outDir, rec.File));
        File.WriteAllBytes(Path.Combine(outDir, MdName), md);
        WriteDerived(outDir, Derive(ParseMd(md), Encoding.UTF8.GetString(md)).Files);
        var (doc, newRec) = Snapshot(outDir, md, LoadLedgerDoc(outDir), "rollback", $"回滚：旧版 v{ver:D4} 重新晋级");
        AppendCloseout(doc, new LedgerCloseout
        {
            At = NowStamp(),
            State = "rollback",
            Action = $"rollback -> v{ver:D4}，重新晋级为 v{newRec.Version ?? 0:D4}",
        });
        SaveLedgerDoc(outDir, doc);

        var rt = Derive(ParseMd(md), Encoding.UTF8.GetString(md));
        var c = RtCounts(rt.Roundtrip);
        Console.WriteLine($"[ROLLBACK] 已回滚到 v{ver:D4} 的内容，并**追加**为新版本 v{newRec.Version ?? 0:D4}（历史未删）：{newRec.File}");
        Console.WriteLine($"[ROLLBACK] 派生已随之重建；往返 equal {c.Equal} / missing {c.Missing} / mismatch {c.Mismatch}");
        return 0;
    }

    /// <summary><c>--check</c>：派生一致 + 往返 + 版本完整性 + 账本判定 + 常驻汇总 + 下钻指针 + 坑集/交接一致性。</summary>
    internal static int Check(string outDir, string pitfallsSrc, string handoffSrc, string pitfallsProj)
    {
        var mdPath = Path.Combine(outDir, MdName);
        if (!File.Exists(mdPath))
        {
            Console.Error.WriteLine($"[FAIL] {mdPath} 不存在");
            return 2;
        }

        var md = File.ReadAllBytes(mdPath);
        var derived = Derive(ParseMd(md), Encoding.UTF8.GetString(md));
        var led = LoadLedgerInfo(outDir);
        var failed = 0;

        var same = derived.Files.All(f =>
        {
            var p = Path.Combine(outDir, DerivedDir, f.Key);
            return File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(f.Value);
        });
        Console.WriteLine($"  [{(same ? "PASS" : "FAIL")}] 派生一致           {derived.Files.Count} 个派生文件与从 md 重算的结果{(same ? "逐字节相同" : "不一致")}");
        failed += same ? 0 : 1;

        var c = RtCounts(derived.Roundtrip);
        var mismatch = derived.Roundtrip.Where(r => r.State == "mismatch").Select(r => r.Skill).ToList();
        var missing = derived.Roundtrip.Where(r => r.State == "source_missing").Select(r => r.Skill).ToList();
        if (mismatch.Count > 0)
        {
            Console.WriteLine($"  [FAIL] 往返（md→派生→SKILL.md）{c.Equal}/{c.Equal + c.Mismatch} 逐字节相等（字节不等：{PyListRepr(mismatch.Take(5))}）");
            failed++;
        }
        else
        {
            Console.WriteLine($"  [PASS] 往返（md→派生→SKILL.md）{c.Equal}/{c.Equal + c.Mismatch} 逐字节相等（mismatch 0）");
        }

        if (missing.Count > 0)
        {
            Console.WriteLine($"  [WARN] 源已归档/卸载       {missing.Count} 个技能 src 不存在 ⇒ 跳过字节比对：{PyListRepr(missing.Take(8))}");
        }

        var bad = new List<string>();
        foreach (var v in led.Versions)
        {
            var f = Path.Combine(outDir, v.File);
            if (!File.Exists(f))
            {
                bad.Add($"{v.File} 缺失");
            }
            else if (Sha256Hex(File.ReadAllBytes(f)) != v.Sha256)
            {
                bad.Add($"{v.File} 指纹不符（重算）");
            }
        }

        Console.WriteLine($"  [{(bad.Count == 0 ? "PASS" : "FAIL")}] 版本完整性         {led.Versions.Count} 个版本快照{(bad.Count == 0 ? "全部指纹重算通过" : $"异常：{PyListRepr(bad)}")}（只增不删：是）");
        failed += bad.Count == 0 ? 0 : 1;

        var j = Judge(outDir, md, led);
        var ledgerCur = j.LedgerCurrentSha is null ? "-" : j.LedgerCurrentSha[..16];
        Console.WriteLine($"  [{(j.State is "A" or "C" ? "PASS" : "WARN")}] 账本 ↔ md          {j.State}（md 重算 sha {j.MdSha[..16]} / 账本 {ledgerCur}）");

        var summary = Persistence.DataContractXml.ReadFile<D.SummaryDoc>(Path.Combine(outDir, DerivedDir, "summary.xml"));
        var l1b = (long)summary.L1Bytes;
        var l2b = (long)summary.L2Bytes;
        var sum = l1b + l2b;
        Console.WriteLine($"  [PASS] 常驻汇总         L1 {l1b} B + L2 {l2b} B = {sum} B = {(sum / BytesPerToken).ToString("F0", CultureInfo.InvariantCulture)} token（口径 {BytesPerToken.ToString("F2", CultureInfo.InvariantCulture)}）");

        // **下钻指针闸门**：每一跳都要能解析（悬空 = 红）。
        var dangling = new List<string>();
        var l3Ids = Frags<D.StripRow>(derived.Files, "l3.xml").Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var l4Ids = Frags<D.StripRow>(derived.Files, "l4.xml").Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var r in Frags<D.L2Row>(derived.Files, "l2.xml"))
        {
            foreach (var i_ in r.Ids)
            {
                if (!l3Ids.Contains(i_))
                {
                    dangling.Add($"L2「{RunePrefix(r.Question, 18)}」→ {i_}");
                }
            }
        }

        foreach (var cat in Frags<D.StripRow>(derived.Files, "l3.xml"))
        {
            foreach (var route in cat.Routes ?? [])
            {
                if (!l4Ids.Contains(route.L4))
                {
                    dangling.Add($"L3 {cat.Id} → {route.L4}");
                }
            }
        }

        Console.WriteLine($"  [{(dangling.Count == 0 ? "PASS" : "FAIL")}] 下钻指针           L2→L3→L4 悬空 {dangling.Count} 处{(dangling.Count == 0 ? string.Empty : "：" + string.Join("；", dangling.Take(4)))}");
        failed += dangling.Count == 0 ? 0 : 1;

        failed += CheckUnify(outDir, pitfallsSrc, handoffSrc, pitfallsProj);
        Console.WriteLine($"\n[CHECK] {(failed == 0 ? "全部通过" : "存在失败项")}（失败 {failed} 项）");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>坑集/交接派生一致性（就地调 <see cref="L3UnifyCli"/>，不再起 python —— 铁则 R2）。</summary>
    private static int CheckUnify(string outDir, string pitfallsSrc, string handoffSrc, string pitfallsProj)
    {
        var failed = 0;
        var specs = new List<(string Kind, string Src, string? Proj)>();
        if (!string.IsNullOrEmpty(pitfallsSrc) && File.Exists(pitfallsSrc))
        {
            specs.Add(("pitfalls", pitfallsSrc, pitfallsProj));
        }

        if (!string.IsNullOrEmpty(handoffSrc) && (Directory.Exists(handoffSrc) || File.Exists(handoffSrc)))
        {
            specs.Add(("handoff", handoffSrc, null));
        }

        foreach (var (kind, src, proj) in specs)
        {
            var outDerived = Path.Combine(outDir, DerivedDir, kind == "pitfalls" ? "pitfalls.xml" : "handoff.xml");
            Console.WriteLine($"  -- {kind} 派生一致性（C# L3Unify --check）--");
            var r = L3UnifyCli.Check(kind, src, proj, outDerived, null, null);
            Console.Write(r.Stdout);
            if (r.Code != 0)
            {
                Console.Write(r.Stderr);
                failed++;
            }
            else
            {
                Console.WriteLine($"  [PASS] {kind} 派生           {Path.GetFileName(outDerived)} 与源一致、id 稳定");
            }
        }

        return failed;
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>往返三态计数（equal / source_missing / mismatch）。</summary>
    internal static (int Equal, int Missing, int Mismatch) RtCounts(IReadOnlyList<RtRow> rt) =>
        (rt.Count(r => r.State == "equal"), rt.Count(r => r.State == "source_missing"), rt.Count(r => r.State == "mismatch"));

    /// <summary>短名：<c>S-ctxidx-001</c> ⇒ <c>ctxidx</c>（无 ⇒ <c>unknown</c>）。</summary>
    internal static string ShortOf(IReadOnlyList<Strip> strips)
    {
        if (strips.Count == 0)
        {
            return "unknown";
        }

        var m = ShortName.Match(strips[0].Id);
        return m.Success ? m.Groups[1].Value : "unknown";
    }

    /// <summary>一个整份文档（带声明 + 缩进）的 UTF-8 字节。</summary>
    private static byte[] Document<T>(T value) where T : class => Utf8(Persistence.DataContractXml.ToDocument(value));

    /// <summary>行式 XML 片段流（一行一条；结构承载，不转义）—— 与事件流同一套。</summary>
    private static byte[] Fragments<T>(IEnumerable<T> rows) where T : class =>
        Utf8(string.Concat(rows.Select(r => Persistence.DataContractXml.ToFragment(r) + "\n")));

    private static byte[] Utf8(string s) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(s);

    private static KeyValuePair<string, byte[]> Pair(string name, byte[] data) => new(name, data);

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>从**内存里的派生文件**读行式 XML 片段流（结构承载，正文里的换行不受影响）。</summary>
    private static IEnumerable<T> Frags<T>(IReadOnlyList<KeyValuePair<string, byte[]>> files, string name) where T : class =>
        Persistence.DataContractXml.ReadFragmentsFromText<T>(
            Encoding.UTF8.GetString(files.First(f => f.Key == name).Value));

    /// <summary>Python <c>s[:n]</c>（**码点**切片；代理对算 1）。</summary>
    internal static string RunePrefix(string s, int n)
    {
        var runes = s.EnumerateRunes().ToArray();
        return runes.Length <= n ? s : string.Concat(runes.Take(n).Select(r => r.ToString()));
    }

    /// <summary>Python 列表 repr（<c>['a', 'b']</c>）—— 判据文本要逐字同。</summary>
    internal static string PyListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(PyRepr)) + "]";

    private static string PyRepr(string s) => s.Contains('\'')
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : "'" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";

    // ---------------------------------------------------------------- 账本（整份读写：K1 留痕 / K6 晋级）

    internal static readonly string[] K_Gates =
    [
        "K1 绝不静默", "K2 版本只增不删", "K3 派生可重建而 md 不可反向生成",
        "K4 一条命令可回滚", "K5 指纹重算不信旧值", "K6 晋级只发生在收尾",
    ];

    private static readonly Dictionary<string, string> Origin = new(StringComparer.Ordinal)
    {
        ["emit-initial"] = "初始基线（首次 emit）",
        ["promote"] = "记账写入 A（收尾晋级）",
        ["promote-external"] = "外部手改后确认 B（用户裁决后晋级）",
        ["rollback"] = "回滚（旧版重新晋级）",
    };

    internal static string OriginOf(string source) => Origin.TryGetValue(source, out var v) ? v : source;

    /// <summary>账本整份文档（A4 尾：<c>.ledger.json</c> → 强类型 XML，<c>.ledger.xml</c>；`sources`/`repo` 为历史遗留字段，原样保留）。</summary>
    internal static LedgerDoc LoadLedgerDoc(string outDir)
    {
        var p = Path.Combine(outDir, LedgerName);
        return File.Exists(p) ? Persistence.DataContractXml.ReadFile<LedgerDoc>(p) : NewLedgerDoc();
    }

    internal static void SaveLedgerDoc(string outDir, LedgerDoc doc) =>
        Persistence.DataContractXml.WriteFile(Path.Combine(outDir, LedgerName), doc);

    private static LedgerDoc NewLedgerDoc() => new() { Schema = LedgerSchema, Gates = [.. K_Gates] };

    private static void AppendCloseout(LedgerDoc doc, LedgerCloseout rec) => doc.Closeouts.Add(rec);

    /// <summary>K6：只追加一个版本快照（命名只用版本号，时间戳只进账本 ⇒ 同输入同产物）。</summary>
    private static (LedgerDoc Doc, LedgerVersion Rec) Snapshot(string outDir, byte[] md, LedgerDoc doc, string source, string note)
    {
        var ver = doc.Versions.Count > 0 ? (doc.Versions[^1].Version ?? 0) + 1 : 1;
        var fn = $"knowledge-v{ver:D4}.md";
        Directory.CreateDirectory(Path.Combine(outDir, VersionsDir));
        File.WriteAllBytes(Path.Combine(outDir, VersionsDir, fn), md);
        var rec = new LedgerVersion
        {
            Version = ver,
            File = $"{VersionsDir}/{fn}",
            Sha256 = Sha256Hex(md),
            Bytes = md.Length,
            At = NowStamp(),
            Source = source,
            Origin = OriginOf(source),
            Note = note,
        };
        doc.Versions.Add(rec);
        doc.Current = rec;
        return (doc, rec);
    }

    internal static string NowStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>知识库账本（A4 尾：<c>.ledger.json</c> → 强类型 XML，<c>.ledger.xml</c>；K1~K6 的真相源）。</summary>
[DataContract(Name = "Ledger", Namespace = "")]
public sealed class LedgerDoc
{
    [DataMember(Order = 0)] public string Schema { get; set; } = "knowledge-repo/ledger/1";

    [DataMember(Order = 1)] public List<string> Gates { get; set; } = [];

    [DataMember(Order = 2)] public LedgerVersion? Current { get; set; }

    [DataMember(Order = 3)] public List<LedgerVersion> Versions { get; set; } = [];

    [DataMember(Order = 4)] public List<LedgerCloseout> Closeouts { get; set; } = [];

    /// <summary>历史遗留：技能源清单（由已退役的 py <c>emit</c> 写；C# 侧不读不写，只原样保留，不丢数据）。</summary>
    [DataMember(Order = 5)] public Dictionary<string, LedgerSource> Sources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>历史遗留：技能仓库路径（同 <see cref="Sources"/>）。</summary>
    [DataMember(Order = 6)] public string? Repo { get; set; }
}

/// <summary>账本里的一条版本快照（<c>versions[]</c> / <c>current</c>）。</summary>
[DataContract(Name = "Version", Namespace = "")]
public sealed class LedgerVersion
{
    [DataMember(Order = 0)] public int? Version { get; set; }

    [DataMember(Order = 1)] public string? File { get; set; }

    [DataMember(Order = 2)] public string Sha256 { get; set; } = string.Empty;

    [DataMember(Order = 3)] public long Bytes { get; set; }

    [DataMember(Order = 4)] public string At { get; set; } = string.Empty;

    [DataMember(Order = 5)] public string Source { get; set; } = string.Empty;

    [DataMember(Order = 6)] public string? Origin { get; set; }

    [DataMember(Order = 7)] public string? Note { get; set; }
}

/// <summary>K1 留痕的一条（<c>closeouts[]</c>）。</summary>
[DataContract(Name = "Closeout", Namespace = "")]
public sealed class LedgerCloseout
{
    [DataMember(Order = 0)] public string At { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string State { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string? MdSha256 { get; set; }

    [DataMember(Order = 3)] public int? PrevVersion { get; set; }

    [DataMember(Order = 4)] public string? PrevSha256 { get; set; }

    [DataMember(Order = 5)] public string Action { get; set; } = string.Empty;
}

/// <summary>历史遗留的技能源（<c>sources</c> 的一条；键 = 技能名）。</summary>
[DataContract(Name = "Source", Namespace = "")]
public sealed class LedgerSource
{
    [DataMember(Order = 0)] public string Src { get; set; } = string.Empty;

    [DataMember(Order = 1)] public bool EndsWithNewline { get; set; }

    [DataMember(Order = 2)] public string Frontmatter { get; set; } = string.Empty;

    [DataMember(Order = 3)] public string L1 { get; set; } = string.Empty;

    [DataMember(Order = 4)] public string Mode { get; set; } = string.Empty;

    [DataMember(Order = 5)] public double RatioIndexed { get; set; }

    [DataMember(Order = 6)] public string? Benefit { get; set; }

    [DataMember(Order = 7)] public List<LedgerStrip> Strips { get; set; } = [];
}

/// <summary>历史遗留的技能切片（<c>strips</c> 的一条）。</summary>
[DataContract(Name = "Strip", Namespace = "")]
public sealed class LedgerStrip
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;

    [DataMember(Order = 1)] public int Seq { get; set; }

    [DataMember(Order = 2)] public string Title { get; set; } = string.Empty;

    [DataMember(Order = 3)] public string Kind { get; set; } = string.Empty;

    [DataMember(Order = 4)] public string Domain { get; set; } = string.Empty;
}

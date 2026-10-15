namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **difflib 忠实移植**（B4-b 依赖件）—— <c>difflib.unified_diff(a, b, fromfile, tofile, n)</c> 的**逐字符等价**。
///
/// <para>为什么要真搬而不是「随便算个 diff」：<c>knowledge-repo --closeout</c> 在**态 B**（外部手改）时把
/// <c>unified_diff</c> 的结果直接呈现给人看 —— 那是判据的一部分，必须与 python 同形。
/// 因此这里连 <c>SequenceMatcher</c> 的 <b>autojunk</b>（<c>len(b) &gt;= 200</c> 时把出现次数 &gt; <c>len(b)//100+1</c>
/// 的「热门行」当垃圾剔除）也一并照搬：少了它，长文档的 hunk 边界就会漂。</para>
/// </summary>
internal static class PyDiff
{
    /// <summary>Python <c>str.splitlines(keepends=True)</c>（含 <c>\v \f \x1c-\x1e \x85 \u2028 \u2029</c> 这些边界）。</summary>
    public static List<string> SplitLinesKeepEnds(string s)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var isBoundary = c is '\n' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';
            var len = 1;
            if (c == '\r')
            {
                isBoundary = true;
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    len = 2;
                }
            }

            if (isBoundary)
            {
                lines.Add(s.Substring(start, i - start + len));
                i += len;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < s.Length)
        {
            lines.Add(s[start..]);
        }

        return lines;
    }

    /// <summary><c>difflib.unified_diff(a, b, fromfile, tofile, n=2)</c>，返回**行序列**（每行含 <c>\n</c>）。</summary>
    public static List<string> UnifiedDiff(IReadOnlyList<string> a, IReadOnlyList<string> b, string fromfile, string tofile, int n)
    {
        var outLines = new List<string>();
        var started = false;
        foreach (var group in new SequenceMatcher(a, b).GetGroupedOpcodes(n))
        {
            if (!started)
            {
                started = true;
                outLines.Add($"--- {fromfile}\n");
                outLines.Add($"+++ {tofile}\n");
            }

            var first = group[0];
            var last = group[^1];
            outLines.Add($"@@ -{FormatRange(first.I1, last.I2)} +{FormatRange(first.J1, last.J2)} @@\n");
            foreach (var (tag, i1, i2, j1, j2) in group)
            {
                if (tag == "equal")
                {
                    for (var i = i1; i < i2; i++)
                    {
                        outLines.Add(" " + a[i]);
                    }

                    continue;
                }

                if (tag is "replace" or "delete")
                {
                    for (var i = i1; i < i2; i++)
                    {
                        outLines.Add("-" + a[i]);
                    }
                }

                if (tag is "replace" or "insert")
                {
                    for (var j = j1; j < j2; j++)
                    {
                        outLines.Add("+" + b[j]);
                    }
                }
            }
        }

        return outLines;
    }

    private static string FormatRange(int start, int stop)
    {
        var beginning = start + 1;
        var length = stop - start;
        if (length == 1)
        {
            return beginning.ToString();
        }

        if (length == 0)
        {
            beginning -= 1;
        }

        return $"{beginning},{length}";
    }

    private sealed record Match(int A, int B, int Size);

    private sealed record Op(string Tag, int I1, int I2, int J1, int J2);

    /// <summary><c>difflib.SequenceMatcher(None, a, b)</c>（<c>autojunk=True</c>）。</summary>
    private sealed class SequenceMatcher
    {
        private readonly IReadOnlyList<string> _a;
        private readonly IReadOnlyList<string> _b;
        private readonly Dictionary<string, List<int>> _b2j = new(StringComparer.Ordinal);
        private readonly HashSet<string> _bjunk = new(StringComparer.Ordinal);
        private List<Match>? _matchingBlocks;

        public SequenceMatcher(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            _a = a;
            _b = b;
            for (var i = 0; i < b.Count; i++)
            {
                if (!_b2j.TryGetValue(b[i], out var list))
                {
                    _b2j[b[i]] = list = [];
                }

                list.Add(i);
            }

            // autojunk：n >= 200 时，出现次数 > n//100+1 的「热门行」不进 b2j。
            var n2 = b.Count;
            if (n2 >= 200)
            {
                var ntest = (n2 / 100) + 1;
                foreach (var elt in _b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList())
                {
                    _b2j.Remove(elt);
                }
            }
        }

        private Match FindLongestMatch(int alo, int ahi, int blo, int bhi)
        {
            var besti = alo;
            var bestj = blo;
            var bestsize = 0;
            var j2len = new Dictionary<int, int>();
            for (var i = alo; i < ahi; i++)
            {
                var newj2len = new Dictionary<int, int>();
                if (_b2j.TryGetValue(_a[i], out var js))
                {
                    foreach (var j in js)
                    {
                        if (j < blo)
                        {
                            continue;
                        }

                        if (j >= bhi)
                        {
                            break;
                        }

                        var k = j2len.TryGetValue(j - 1, out var prev) ? prev + 1 : 1;
                        newj2len[j] = k;
                        if (k > bestsize)
                        {
                            besti = i - k + 1;
                            bestj = j - k + 1;
                            bestsize = k;
                        }
                    }
                }

                j2len = newj2len;
            }

            while (besti > alo && bestj > blo && !_bjunk.Contains(_b[bestj - 1]) && _a[besti - 1] == _b[bestj - 1])
            {
                besti--; bestj--; bestsize++;
            }

            while (besti + bestsize < ahi && bestj + bestsize < bhi
                   && !_bjunk.Contains(_b[bestj + bestsize]) && _a[besti + bestsize] == _b[bestj + bestsize])
            {
                bestsize++;
            }

            while (besti > alo && bestj > blo && _bjunk.Contains(_b[bestj - 1]) && _a[besti - 1] == _b[bestj - 1])
            {
                besti--; bestj--; bestsize++;
            }

            while (besti + bestsize < ahi && bestj + bestsize < bhi
                   && _bjunk.Contains(_b[bestj + bestsize]) && _a[besti + bestsize] == _b[bestj + bestsize])
            {
                bestsize++;
            }

            return new Match(besti, bestj, bestsize);
        }

        private List<Match> GetMatchingBlocks()
        {
            if (_matchingBlocks is not null)
            {
                return _matchingBlocks;
            }

            int la = _a.Count, lb = _b.Count;
            var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
            queue.Push((0, la, 0, lb));
            var blocks = new List<Match>();
            while (queue.Count > 0)
            {
                var (alo, ahi, blo, bhi) = queue.Pop();
                var m = FindLongestMatch(alo, ahi, blo, bhi);
                if (m.Size > 0)
                {
                    blocks.Add(m);
                    if (alo < m.A && blo < m.B)
                    {
                        queue.Push((alo, m.A, blo, m.B));
                    }

                    if (m.A + m.Size < ahi && m.B + m.Size < bhi)
                    {
                        queue.Push((m.A + m.Size, ahi, m.B + m.Size, bhi));
                    }
                }
            }

            blocks.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B != y.B ? x.B.CompareTo(y.B) : x.Size.CompareTo(y.Size));

            var nonAdjacent = new List<Match>();
            int i1 = 0, j1 = 0, k1 = 0;
            foreach (var (i2, j2, k2) in blocks)
            {
                if (i1 + k1 == i2 && j1 + k1 == j2)
                {
                    k1 += k2;
                }
                else
                {
                    if (k1 > 0)
                    {
                        nonAdjacent.Add(new Match(i1, j1, k1));
                    }

                    (i1, j1, k1) = (i2, j2, k2);
                }
            }

            if (k1 > 0)
            {
                nonAdjacent.Add(new Match(i1, j1, k1));
            }

            nonAdjacent.Add(new Match(la, lb, 0));
            _matchingBlocks = nonAdjacent;
            return _matchingBlocks;
        }

        private List<Op> GetOpcodes()
        {
            var answer = new List<Op>();
            int i = 0, j = 0;
            foreach (var (ai, bj, size) in GetMatchingBlocks())
            {
                var tag = string.Empty;
                if (i < ai && j < bj)
                {
                    tag = "replace";
                }
                else if (i < ai)
                {
                    tag = "delete";
                }
                else if (j < bj)
                {
                    tag = "insert";
                }

                if (tag.Length > 0)
                {
                    answer.Add(new Op(tag, i, ai, j, bj));
                }

                (i, j) = (ai + size, bj + size);
                if (size > 0)
                {
                    answer.Add(new Op("equal", ai, i, bj, j));
                }
            }

            return answer;
        }

        public List<List<Op>> GetGroupedOpcodes(int n)
        {
            var codes = GetOpcodes();
            if (codes.Count == 0)
            {
                codes = [new Op("equal", 0, 1, 0, 1)];
            }

            if (codes[0].Tag == "equal")
            {
                var c = codes[0];
                codes[0] = c with { I1 = Math.Max(c.I1, c.I2 - n), J1 = Math.Max(c.J1, c.J2 - n) };
            }

            if (codes[^1].Tag == "equal")
            {
                var c = codes[^1];
                codes[^1] = c with { I2 = Math.Min(c.I2, c.I1 + n), J2 = Math.Min(c.J2, c.J1 + n) };
            }

            var nn = n + n;
            var groups = new List<List<Op>>();
            var group = new List<Op>();
            foreach (var op in codes)
            {
                var (tag, i1, i2, j1, j2) = op;
                if (tag == "equal" && i2 - i1 > nn)
                {
                    group.Add(new Op(tag, i1, Math.Min(i2, i1 + n), j1, Math.Min(j2, j1 + n)));
                    groups.Add(group);
                    group = [];
                    (i1, j1) = (Math.Max(i1, i2 - n), Math.Max(j1, j2 - n));
                }

                group.Add(new Op(tag, i1, i2, j1, j2));
            }

            if (group.Count > 0 && !(group.Count == 1 && group[0].Tag == "equal"))
            {
                groups.Add(group);
            }

            return groups;
        }
    }
}

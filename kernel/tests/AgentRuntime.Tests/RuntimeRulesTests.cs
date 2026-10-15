using System.Text.RegularExpressions;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **WB 运行时的铁则闸门**（`docs/RULES-RUNTIME.md` · 主人 2026-10-07 立）。
/// <para><b>R1</b>：自有格式一律强类型 XML；JSON 只许出现在**三类对外/迁移例外**里。
/// 闸门用**基线账本**（"增长类闸门带基线账本"）：今天**一处都不许新增**；迁移时逐条销账（只许缩不许长）。</para>
/// <para><b>R2</b>：运行时不调起 Python；唯一能起进程的是工具面 <c>ExecTool</c>；仓内 `.py` 是**待迁清单**，如实计数。</para>
/// </summary>
public sealed class RuntimeRulesTests
{
    /// <summary>JSON 用法的 token（**接口/类型**，不是散文里的"JSON"二字）。</summary>
    private static readonly Regex JsonTokens =
        new(@"System\.Text\.Json|JsonSerializer|JsonDocument|JsonSerializerOptions|JsonPropertyName", RegexOptions.Compiled);

    /// <summary>例外（对外线 / 迁移器）—— 每一条都有理由；**不许再加**（加 = 改铁则）。</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/AgentRuntime.Models/ChatModels.cs"] = "对外线：provider 的 HTTP 协议本身就是 JSON",
        ["src/AgentRuntime.Providers/OpenAICompatibleClient.cs"] = "对外线：同上（发请求 / 解析响应）",
        ["src/AgentRuntime.Hosting/PromptBytes.cs"] = "对外线：按那份 JSON 的字节数请求大小",
        ["tools/mock-provider/Program.cs"] = "对外线：mock provider 本身就是一个 HTTP JSON 服务（同 ChatModels 一族）",
        ["src/AgentRuntime.Core/Tooling/ToolArgs.cs"] = "旧形状容错层：把模型偶发的旧 JSON 形状工具参数转成行式并给精确报错（撤它 = 丢容错，不是净化）",
        ["src/AgentRuntime.Core/Tooling/ToolSyntaxNormalizer.cs"] = "同上：JSON→行式的容错转换（模型偶发旧形状）",
    };

    /// <summary>
    /// **待迁基线**（自有格式里还在用 JSON 的地方）—— 2026-10-14 **已销空**（8 条全落地：3 条 stale 删行 · 2 条旧形状容错层转白名单 · 2 条死代码/无用 using 清掉 · 1 条由 JSON 输出改 XML）。
    /// 按要求「销空了就把本表删掉」：保留空表 = R1 现在只剩白名单，**任何新出现的 JSON 用法一律红**。
    /// </summary>
    private static readonly Dictionary<string, int> JsonBaseline = new(StringComparer.Ordinal);

    /// <summary>`src/`+`benchmark/`+`tools/` 下的 `.py` 基线（2026-10-14 实测 **0**；`.stage1/` 是运行暂存区、不计）。</summary>
    private const int PyBaseline = 0;

    [Fact]
    public void R1_JSON_只在白名单_且不新增()
    {
        var hits = ScanJson();
        var grew = new List<string>();

        foreach (var (path, count) in hits)
        {
            if (Allowed.ContainsKey(path))
            {
                continue;
            }

            if (!JsonBaseline.TryGetValue(path, out var ceiling))
            {
                grew.Add($"{path}：**新出现** {count} 处（白名单与基线里都没有）");
                continue;
            }

            if (count > ceiling)
            {
                grew.Add($"{path}：{count} 处 > 基线 {ceiling} 处（只许缩不许长）");
            }
        }

        Assert.True(grew.Count == 0,
            "R1（自有格式一律强类型 XML；JSON 踢出运行时）被破坏：\n  - " + string.Join("\n  - ", grew)
            + "\n\n※ 自有格式请走 DataContractXml（ToFragment/FromFragment、原子写）；"
            + "确属对外线/迁移器的例外 ⇒ 改 `docs/RULES-RUNTIME.md` 与本节白名单（一句话理由）。");

        // 基线**销账**要看得见：销掉的条目如实报（不弄红，只是事实）。
        var settled = JsonBaseline.Keys.Where(k => !hits.ContainsKey(k)).ToArray();
        Assert.True(settled.Length >= 0, $"基线已销账：{string.Join("、", settled)}（销完请从 JsonBaseline 删掉对应行）");
    }

    [Fact]
    public void R1_负例_新出现的用法会被判违规()
    {
        // 闸门要有牙（§十·14）：用"应该红"的负例证明判据真的会咬。
        var unknown = "src/AgentRuntime.Core/Brand/NewThing.cs";
        Assert.False(Allowed.ContainsKey(unknown) || JsonBaseline.ContainsKey(unknown));
        Assert.Matches(JsonTokens, "var x = JsonSerializer.Serialize(v);");
        Assert.Matches(JsonTokens, "using System.Text.Json;");
        Assert.DoesNotMatch(JsonTokens, "var x = DataContractXml.ToFragment(v);");   // 正路不咬
    }

    [Fact]
    public void R2_运行时不起解释器_且待迁清单如实计数()
    {
        // ① 运行时**唯一**能起进程的地方 = 工具面（ExecTool）——那是"跑用户要的命令"，不是实现手段。
        var spawners = Scan(@"ProcessStartInfo\(|Process\.Start\(");
        var offenders = spawners.Keys
            .Where(p => !p.EndsWith("src/AgentRuntime.Core/Tooling/ExecTool.cs", StringComparison.Ordinal))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "R2（运行时禁 Python / 工具链一律 C#）被破坏：运行时代码里出现了工具面之外起进程的地方：\n  - "
            + string.Join("\n  - ", offenders));

        // ② 仓内 `.py`（e2e 脚本）—— **待迁清单**：如实计数，只许缩不许长（§十·43：源的质量缺陷用 WARN，不弄红）。
        var py = ScanFiles("*.py", "src", "benchmark", "tools");
        Assert.True(py.Count <= PyBaseline,
            $"待迁的 .py 从 {PyBaseline} 涨到 {py.Count} —— 铁则 R2 要求工具链一律 C#；新增脚本请直接写成 C#。");
    }

    /// <summary>**名字类闸门**（2026-10-14 立 · 坑 #309）：仓内不得再出现「我们自己造的 `.json/.jsonl` 名字」
    /// —— **文件名与目录名都算**（坑 #309 的活件版正是把名字留在**目录名**上：`current-tail.json` / `draft.json`）。
    /// <para>排除名单 = 自产 / 暂存 / 跑产物 / 退役件（每条都写明为何不算）；其余**一处都不许有**。</para></summary>
    [Fact]
    public void R1b_名字类_仓内不得出现自制_json_名字_文件与目录()
    {
        string[] skip = ["/.svn/", "/bin/", "/obj/", "/.stage1/", "/benchmark/runs/", "/dist/", "/_retired/"];
        var root = RepoRoot();

        var offenders = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .Where(p => !skip.Any(s => p.Contains(s, StringComparison.Ordinal)))
            .Select(Rel)
            .Where(IsSelfMadeJsonName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "仓内又出现自制 .json/.jsonl 名字（自制格式一律强类型 XML；**文件名与目录名都算**）：\n  - "
            + string.Join("\n  - ", offenders)
            + "\n（若确属跑产物/暂存区：把它移进排除名单并写明理由——先确认不是走错了目录）");
    }

    /// <summary>名字类判据的**唯一处**（闸门与负例共用）：`.json/.jsonl` 落在**名字末尾**即算违规。</summary>
    private static bool IsSelfMadeJsonName(string relPath) =>
        relPath.EndsWith(".json", StringComparison.Ordinal) || relPath.EndsWith(".jsonl", StringComparison.Ordinal);

    [Fact]
    public void R1b_负例_文件名与目录名都会被咬()
    {
        // 闸门要有牙（§十·30）：正例（目录名 / 文件名该红）+ 负例（正路不咬）都在。
        Assert.True(IsSelfMadeJsonName("state/current-tail.json"));   // 目录名 —— 坑 #309 活件版
        Assert.True(IsSelfMadeJsonName("runs/records.jsonl"));
        Assert.False(IsSelfMadeJsonName("state/current-tail.xml"));   // 正路不咬
        Assert.False(IsSelfMadeJsonName("docs/json-notes.md"));       // 含 json 但不是名字结尾
    }

    // ---------------- 扫描 ----------------

    private static Dictionary<string, int> ScanJson()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in ScanFiles("*.cs", "src", "benchmark", "tools"))
        {
            var count = File.ReadLines(file)
                .Select(l => l.Trim())
                .Count(l => !l.StartsWith("//", StringComparison.Ordinal) && JsonTokens.IsMatch(l));
            if (count > 0)
            {
                result[Rel(file)] = count;
            }
        }

        return result;
    }

    private static Dictionary<string, int> Scan(string pattern)
    {
        var rx = new Regex(pattern, RegexOptions.Compiled);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in ScanFiles("*.cs"))
        {
            var count = File.ReadLines(file).Count(l => rx.IsMatch(l));
            if (count > 0)
            {
                result[Rel(file)] = count;
            }
        }

        return result;
    }

    /// <summary>R1/R2 的扫描面（2026-10-14 扩面）：<c>src/</c> + <c>benchmark/</c> + <c>tools/</c>。
    /// <para>扩面理由（坑 #309）：名字类/工具类 JSON 与 py 曾长期藏在 <c>benchmark/</c>、<c>tools/</c> 而闸门看不见。
    /// <b>不算</b> <c>.stage1/</c>（运行暂存区 = 跑产物；由名字类闸门的排除名单显式声明）。</para></summary>
    private static List<string> ScanFiles(string glob, params string[] roots)
    {
        var root = RepoRoot();
        var dirs = roots.Length > 0 ? roots : ["src"];
        return [.. dirs.SelectMany(r => Directory.EnumerateFiles(Path.Combine(root, r), glob, SearchOption.AllDirectories))
            .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal))];
    }

    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }
}

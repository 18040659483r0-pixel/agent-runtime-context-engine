using System.Globalization;

namespace AgentRuntime.NetworkTestSuite;

/// <summary>
/// **I 系列用料**（只给任务目的；工具名在题面里点名，保证测的是「这套工具能不能干成」而不是模型的随机选择）。
/// <para>所有外部地址都是稳定公开页；搜索后端用无密钥的 Wikipedia（I 系列可重复跑）。</para>
/// </summary>
public static class Scenarios
{
    public static IReadOnlyList<Scenario> All(string repo, string outRoot)
    {
        var i4Downloads = Path.Combine(outRoot, "I4", "downloads");

        return
        [
            new Scenario(
                "I1",
                "web_fetch 正文",
                "用 web_fetch 读取 https://example.com ，回报 status、title 和正文第一句。只做这一件事。",
                (facts, _) => CheckTool(facts, "web_fetch", ("status=200", "状态 200"), ("title=Example Domain", "标题"))),

            new Scenario(
                "I2",
                "http_request GET",
                "用 http_request 以 GET 读取 https://example.com ，回报 status 与响应体里是否含 Example Domain。只做这一件事。",
                (facts, _) => CheckTool(facts, "http_request", ("status=200", "状态 200"), ("Example Domain", "正文"))),

            new Scenario(
                "I3",
                "http_request POST",
                "用 http_request 向 https://httpbin.org/post 发 POST，body 写 {\"suite\":\"I3\"}，回报 status 与响应体里 json.suite 的值。只做这一件事。",
                (facts, _) => CheckTool(facts, "http_request", ("status=200", "状态 200"), ("I3", "回显内容"))),

            new Scenario(
                "I4",
                "web_download 落盘",
                "用 web_download 下载 https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf 到 i4.pdf，回报 path、bytes、sha256。只做这一件事。",
                (facts, downloads) =>
                {
                    var expected = Path.Combine(i4Downloads, "i4.pdf");
                    var generic = CheckTool(facts, "web_download", ("path=", "路径"), ("sha256=", "sha256"));
                    if (!generic.Ok)
                    {
                        return generic;
                    }

                    if (!File.Exists(expected))
                    {
                        return new CheckResult(false, $"工具报成功但文件不在：{expected}");
                    }

                    var bytes = new FileInfo(expected).Length;
                    return bytes > 1000
                        ? new CheckResult(true, $"文件 {bytes} B；{generic.Detail}")
                        : new CheckResult(false, $"文件过小（{bytes} B）");
                }),

            new Scenario(
                "I5",
                "web_search 搜索",
                "用 web_search 搜索 OpenClaw，给我前 3 条结果的标题与链接。只做这一件事。",
                (facts, _) => CheckTool(facts, "web_search", ("backend=wikipedia", "后端"), ("1.", "第一条结果"))),

            new Scenario(
                "I6",
                "web_search_extract 批量",
                "用 web_search_extract 批量读取这两个网页：https://example.com 和 https://en.wikipedia.org/wiki/Open-source_software ，每页给标题。只做这一件事。",
                (facts, _) => CheckTool(facts, "web_search_extract", ("pages=2", "两页"), ("title=Example Domain", "第一页标题"))),

            new Scenario(
                "I7",
                "SSRF 负例",
                "用 web_fetch 读取 http://127.0.0.1/ ，如实回报结果（成功或失败都要说）。只做这一件事。",
                (facts, _) =>
                {
                    var generic = CheckTool(facts, "web_fetch");
                    if (!generic.Ok)
                    {
                        return generic;
                    }

                    var hit = facts.AllResults.Contains("禁止", StringComparison.Ordinal)
                              || facts.AllResults.Contains("私网", StringComparison.Ordinal)
                              || facts.AllResults.Contains("禁止的地址段", StringComparison.Ordinal);
                    return hit
                        ? new CheckResult(true, "私网目标被拒")
                        : new CheckResult(false, "没有看到 SSRF 拒绝理由");
                }),

            new Scenario(
                "I8",
                "authRef 负例",
                "用 http_request 调用 https://example.com ，authRef 写 missing，如实回报结果。只做这一件事。",
                (facts, _) =>
                {
                    var generic = CheckTool(facts, "http_request");
                    if (!generic.Ok)
                    {
                        return generic;
                    }

                    var hit = facts.AllResults.Contains("authRef", StringComparison.Ordinal)
                              && facts.AllResults.Contains("未声明", StringComparison.Ordinal);
                    return hit
                        ? new CheckResult(true, "未声明的 authRef 被拒绝")
                        : new CheckResult(false, "没有看到 authRef 拒绝理由");
                }),
        ];
    }

    private static CheckResult CheckTool(RunFacts facts, string tool, params (string Needle, string Label)[] needles)
    {
        // 形状错一次可自查（与 M/W 系列同口径）；同一形状连犯 ≥2 次才记实错。
        if (facts.Denials > 1)
        {
            return new CheckResult(false, $"被拒绝 {facts.Denials} 次（>1，未自查收敛）：{string.Join(" / ", facts.ToolDenied.Take(2))}");
        }

        if (!facts.ToolNames.Any(name => name.Equals(tool, StringComparison.OrdinalIgnoreCase)))
        {
            return new CheckResult(false, $"没有调用 {tool}（实际：{string.Join(" / ", facts.ToolNames.Distinct())}）");
        }

        if (facts.ToolResults.Count == 0)
        {
            return new CheckResult(false, "没有工具结果");
        }

        var missing = needles
            .Where(n => !facts.AllResults.Contains(n.Needle, StringComparison.Ordinal))
            .Select(n => n.Label)
            .ToArray();
        return missing.Length == 0
            ? new CheckResult(true, "判据全中")
            : new CheckResult(false, $"缺：{string.Join(" / ", missing)}");
    }
}

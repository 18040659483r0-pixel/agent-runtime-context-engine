using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Tests;

/// <summary>
/// **JSON 串里的非法转义**（2026-09-24 · 坑 #157）—— 工具块被拒的**同族第三个病因**。
/// <para>
/// 来历（真机）：<c>.stage1/stream-20260924-230628.jsonl</c> 的 <b>E095 / E280</b> —— 模型把 shell 正则
/// 原样搬进 JSON 字符串（<c>grep -n 'a\|b'</c>），而 JSON 只认
/// <c>\" \\ \/ \b \f \n \r \t \uXXXX</c> ⇒
/// <c>JsonException: '|' is an invalid escapable character within a JSON string</c> ⇒
/// **整篇工具块被拒**（fail-closed：一个块坏就是全不跑）。
/// </para>
/// <para>
/// 为什么病不断换、话术却不动：已记的错因（键没引号 / 串里有真换行）**与复发的错因不是同一个**
/// —— 拒绝话术只说一个病因时，换一个病因就等于没话术（<c>KNOWLEDGE.md</c> §十·51）。
/// 所以本闸门钉的是**话术本身**：非法转义必须被**点名**，且给出一条可执行的修法。
/// </para>
/// <para>
/// 位置说明：本条**不改** <c>ToolReportTests.cs</c> —— 那本文件本轮另有未提交的改动
/// （别人的在飞改动，同一文件两个写者会互相夹带）；同一主题的闸门单独立一个文件，互相不夹带。
/// </para>
/// </summary>
public sealed class ToolArgsJsonHintTests
{
    // ---------------- 决定性负例：非法转义必须被点名 ----------------

    [Fact]
    public void 解析_JSON串里的非法转义_必须指名那一条并给修法()
    {
        var q = '"';

        // 真机原样：`grep -n '生成\|勿手改\|BEGIN' …` —— 反斜杠 + `|`（E280 的现场形状）。
        var pipeBlock = "[TOOL] exec {" + q + "command" + q + ":" + q + "grep -n '生成\\|勿手改' docs/x.md" + q + "} risk: none";

        var parsed = ToolReport.Parse(pipeBlock);
        Assert.Equal(ToolParseStatus.Ok, parsed.Status);                       // 解析层放过（大括号配平没问题）
        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(parsed.Call!.ArgumentsJson));

        Assert.Contains("非法的转义", ex.Message, StringComparison.Ordinal);
        Assert.Contains("反斜杠 + |", ex.Message, StringComparison.Ordinal);     // **点名那一个字符**，不是泛泛说「JSON 不合法」
        Assert.Contains("正则里的元字符", ex.Message, StringComparison.Ordinal);
        Assert.Contains("grep -F", ex.Message, StringComparison.Ordinal);       // 修法要可执行

        // 同族的另一半：反斜杠 + `(`（E095 的现场形状）。
        var parenBlock = "[TOOL] exec {" + q + "command" + q + ":" + q + "grep -v 'v\\(1[0-9]\\)' f" + q + "} risk: none";
        var ex2 = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(ToolReport.Parse(parenBlock).Call!.ArgumentsJson));
        Assert.Contains("非法的转义", ex2.Message, StringComparison.Ordinal);
        Assert.Contains("反斜杠 + (", ex2.Message, StringComparison.Ordinal);
    }

    // ---------------- 正例：修法真的可行（照话术改完必须过） ----------------

    [Fact]
    public void 解析_照修法把反斜杠翻倍_必须放行()
    {
        // 话术给的修法之一：JSON 里写 `\\|`（解码后 = 正则要的 `\|`）。
        var fixedJson = "{\"command\":\"grep -n 'a\\\\|b' f\"}";
        Assert.Equal("grep -n 'a\\|b' f", ToolArgs.Parse(fixedJson).RequireString("command"));

        // 其余合法转义一律照旧放行（改话术不能误伤正当写法）。
        Assert.Equal("a/b", ToolArgs.Parse("{\"path\":\"a\\/b\"}").RequireString("path"));
        Assert.Equal("a\tb", ToolArgs.Parse("{\"path\":\"a\\tb\"}").RequireString("path"));
        Assert.Equal("a\\b", ToolArgs.Parse("{\"path\":\"a\\\\b\"}").RequireString("path"));
        Assert.Equal("中", ToolArgs.Parse("{\"path\":\"\\u4e2d\"}").RequireString("path"));

        // 病因一（坑 #130）仍要点名换行 —— 新分支不许把旧话术挤掉。
        var newline = "{\"content\":\"第一行\n第二行\"}";
        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(newline));
        Assert.Contains("没转义的换行", ex.Message, StringComparison.Ordinal);
    }
}

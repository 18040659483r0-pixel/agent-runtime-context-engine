using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B1（2026-09-29 用户定案 · handoff/40 §7.1）**：模型面工具参数 = **行式键值（零转义）**的成对闸门。
///
/// <para><b>应绿</b>：正文里含 换行 / 双引号 / 反斜杠 / 花括号 / 尖括号 &amp; / 中日文 ⇒ **必须成功**
/// —— 这些在 JSON 时代**全部**要求转义，也就是今晚两类拒绝（worker 紧凑写法、master 多行 exec）的根源。</para>
///
/// <para><b>应红</b>：**旧 JSON 形状**且串里带字面换行 ⇒ **明白地拒**（点明病因 + 指出行式出路），
/// 而不是笼统的一句「语法错」。</para>
///
/// <para><b>反向自检</b>：旧 JSON 的**单行**对象仍被容错（归一优先）—— 这条也钉住，防回归把它改没。</para>
/// </summary>
public sealed class ToolLineFormTests
{
    // ---------------- 应绿：JSON 时代会炸的那些正文 ----------------

    [Fact]
    public void 应绿_多行正文_含换行引号反斜杠花括号尖括号与中日文_必须成功()
    {
        const string content =
            "第一行\n第二行 \"引号\" 与 \\反斜杠\\ 与 {花括号} 与 <尖括号> 与 & 与中日文：测试。\n第三行";

        var text = "[TOOL] write\npath: /tmp/b1.md\ncontent<<\n" + content + "\n>>\nrisk: none\n";

        var parsed = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        Assert.NotNull(parsed.Call);
        Assert.Equal("write", parsed.Call!.Name);
        Assert.True(parsed.Call.ClaimsNoRisk);

        var args = ToolArgs.Parse(parsed.Call.ArgumentsJson);
        args.EnsureOnly("path", "content");
        Assert.Equal("/tmp/b1.md", args.RequireString("path"));
        Assert.Equal(content, args.RequireString("content"));       // **逐字**相等（零转义）
    }

    [Fact]
    public void 应绿_单行值_含空格中文与冒号都原样()
    {
        var parsed = ToolReport.Parse("[TOOL] read\npath: /tmp/中文 目录/文件.md\nmaxLines: 80\n");

        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        var args = ToolArgs.Parse(parsed.Call!.ArgumentsJson);
        args.EnsureOnly("path", "maxLines");
        Assert.Equal("/tmp/中文 目录/文件.md", args.RequireString("path"));
        Assert.Equal(80, args.OptionalInt("maxLines"));
    }

    [Fact]
    public void 应绿_块内那行risk不是声明_声明只在块外认()
    {
        const string content = "risk: none\n这行是**正文**，不是声明。";
        var text = "[TOOL] write\npath: /tmp/b1b.md\ncontent<<\n" + content + "\n>>\nrisk: none\n";

        var parsed = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        Assert.True(parsed.Call!.ClaimsNoRisk);                       // 块外那一行才是声明
        var args = ToolArgs.Parse(parsed.Call.ArgumentsJson);
        Assert.Equal(content, args.RequireString("content"));         // 块内那行 risk: 逐字进正文
    }

    [Fact]
    public void 应绿_块后紧跟正文_正文不被当成参数吞进来()
    {
        var text = "[TOOL] read\npath: /tmp/a.md\n\n这一段是回复正文，不是参数。\n还有第二行。";

        var parsed = ToolReport.Parse(text);

        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        var args = ToolArgs.Parse(parsed.Call!.ArgumentsJson);
        args.EnsureOnly("path");
        Assert.Equal("/tmp/a.md", args.RequireString("path"));
        Assert.Null(args.OptionalString("这一段是回复正文，不是参数。"));   // 正文没被吞成键
    }

    // ---------------- 应红：旧 JSON 形状带字面换行 ----------------

    [Fact]
    public void 应红_旧JSON形状带字面换行_被明白地拒并指出元素形状出路()
    {
        // 这正是今晚 master 自己撞的那一类：多行脚本塞进单行 JSON。
        const string oldShape = "{\"path\":\"a\",\"content\":\"第一行\n第二行\"}";

        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse(oldShape));

        Assert.Contains("没转义的换行", ex.Message, StringComparison.Ordinal);   // **点明病因**
        Assert.Contains("元素形状", ex.Message, StringComparison.Ordinal);          // **指出元素形状出路**（旧「行式键值」已退役）
        Assert.Contains("写到文件", ex.Message, StringComparison.Ordinal);         // 长正文的出路
    }

    [Fact]
    public void 应红_旧JSON形状非法转义_被明白地拒()
    {
        // 今晚另一类：模型把 shell 正则原样搬进 JSON（`\|"` 非法转义）。
        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("{\"command\":\"grep -n 'a\\|b' f\"}"));

        Assert.Contains("元素形状", ex.Message, StringComparison.Ordinal);          // 旧「行式键值」话术已退役（形状只许一份）
    }

    [Fact]
    public void 应红_多行块没写收尾_被明白地拒()
    {
        var ex = Assert.Throws<ToolUsageException>(() => ToolArgs.Parse("path: a\ncontent<<\n没写收尾"));

        Assert.Contains(">>", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- 反向自检：旧单行形状仍容错（归一优先） ----------------

    [Fact]
    public void 容错_旧JSON单行形状仍能归一跑通()
    {
        var args = ToolArgs.Parse("{\"path\":\"a.md\",\"maxLines\":20}");

        Assert.Equal("a.md", args.RequireString("path"));
        Assert.Equal(20, args.OptionalInt("maxLines"));
    }

    [Fact]
    public void 容错_DSML原生写法归一到行式后能跑通()
    {
        // 标记由**同一份字面量**拼出（手打全角竖线易错 ⇒ 与 Normalizer 的常量同源）。
        var mark = "\uFF5C\uFF5CDSML\uFF5C\uFF5C";
        var raw = "<" + mark + "invoke name=\"read\">\n"
                + "<" + mark + "parameter name=\"path\" string=\"true\">/tmp/a.md</" + mark + "parameter>\n"
                + "</" + mark + "invoke>";

        var normalized = ToolSyntaxNormalizer.Normalize(raw);
        var parsed = ToolReport.Parse(normalized);

        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        var args = ToolArgs.Parse(parsed.Call!.ArgumentsJson);
        Assert.Equal("/tmp/a.md", args.RequireString("path"));
    }

    [Fact]
    public void 容错_归一器把多行值写成块形式()
    {
        var lineForm = ToolSyntaxNormalizer.ToLineForm("write", "{\"path\":\"a\",\"content\":\"x\\ny\"}");

        Assert.NotNull(lineForm);
        Assert.Contains("content<<", lineForm, StringComparison.Ordinal);
        Assert.Contains(">>", lineForm, StringComparison.Ordinal);

        var parsed = ToolReport.Parse(lineForm!);                      // lineForm 是**整块**（含块头）
        Assert.Equal(ToolParseStatus.Ok, parsed.Status);
        var args = ToolArgs.Parse(parsed.Call!.ArgumentsJson);
        Assert.Equal("x\ny", args.RequireString("content"));         // 转义解掉 ⇒ 真换行
    }

    // ---------------- 确定性：同对象重复渲染字节相等 ----------------

    [Fact]
    public void 确定性_同一参数重复解析_规范化字节相等()
    {
        var a = ToolArgs.Parse("path: a\nmaxLines: 80");
        var b = ToolArgs.Parse("maxLines: 80\npath: a");       // 键序不同

        Assert.Equal(a.Canonical, b.Canonical);                  // 键按序 ⇒ 字节相等
        Assert.Equal(a.Digest("read"), b.Digest("read"));
        Assert.NotEqual(a.Digest("read"), a.Digest("list"));     // 同名不同工具 ⇒ 不同键
    }
}

using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **表达归一（A6，2026-09-28）**：远端 AI 的自然写法先归一成协议形状 —— **归一优先，拒绝是最后手段**。
/// <para>两条硬纪律各有夹具：① 只动**行首块头**那一行（**不凭空造调用**）；② 归一出不来 ⇒ **原样返回**（不吞、不造）。</para>
/// </summary>
public sealed class ToolSyntaxNormalizerTests
{
    /// <summary>块头（取解析器常量，不在测试里另抄一份）。</summary>
    private static string H => ToolReport.Prefix;

    [Fact]
    public void 归一_紧凑写法_名字无空格与裸键_都归一成协议形状()
    {
        // 现场（2026-09-28）：Worker 按自然写法发 `name{key:"v"}`，旧解析器要标准 JSON ⇒ 第一步必被拒。
        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(H + " read{path:\"a.cs\",maxLines:80}"));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("read", result.Call!.Name);
        var args = ToolArgs.Parse(result.Call.ArgumentsJson);       // B1：形状 = 行式键值（零转义）
        Assert.Equal("a.cs", args.RequireString("path"));
        Assert.Equal(80, args.OptionalInt("maxLines"));
    }

    [Fact]
    public void 归一_标准形状_逐字节不变_且幂等()
    {
        // B1（2026-09-29 主人定案）：**标准形状 = 行式键值（零转义）** —— 归一器对它必须逐字节不动。
        var std = H + " read\npath: a.cs\n";

        Assert.Equal(std, ToolSyntaxNormalizer.Normalize(std));                       // 逐字节不变
        Assert.Equal(std, ToolSyntaxNormalizer.Normalize(ToolSyntaxNormalizer.Normalize(std)));   // 幂等
    }

    [Fact]
    public void 归一_不凭空造调用_正文里的花括号不动()
    {
        // 反向自检：**没有行首块头**的行一律原样 —— 否则正文里任何 `Foo{bar:"x"}` 都会变成一次调用。
        var prose = "结构体是 Foo{bar:\"x\"} 这样的。";

        Assert.Equal(prose, ToolSyntaxNormalizer.Normalize(prose));
        Assert.Equal(ToolParseStatus.None, ToolReport.Parse(ToolSyntaxNormalizer.Normalize(prose)).Status);
    }

    [Fact]
    public void 归一_字符串里的冒号与括号不动_值保持原样()
    {
        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(H + " write{path:\"a\",content:\"x: {y}\"}"));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        var args = ToolArgs.Parse(result.Call!.ArgumentsJson);          // B1：值里的冒号与花括号不必转义
        Assert.Equal("a", args.RequireString("path"));
        Assert.Equal("x: {y}", args.RequireString("content"));
    }

    [Fact]
    public void 归一_归不出就原样返回_交给解析器响亮地拒()
    {
        var broken = H + " read {path:\"a\"";     // 没配平

        Assert.Equal(broken, ToolSyntaxNormalizer.Normalize(broken));                  // 不吞、不猜
        Assert.Equal(ToolParseStatus.Malformed, ToolReport.Parse(ToolSyntaxNormalizer.Normalize(broken)).Status);
    }

    [Fact]
    public void 归一_原生DSML的收尾写成_call也要认得()
    {
        // 2026-10-11 真机 M1：模型交的 DSML 收尾写成 </call>（不是 </invoke>）
        //   ==> 只认 </invoke> 时整块归不出 ==> 又回到静默丢（首条回复白花一轮）。
        const string dsml =
            "<\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>\n"
            + "<\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke name=\"read\">\n"
            + "<\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter name=\"path\" string=\"true\">/abs/a.txt</\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter>\n"
            + "</\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke>\n"
            + "</\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(dsml));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("read", result.Call!.Name);
        Assert.Equal("/abs/a.txt", ToolArgs.Parse(result.Call.ArgumentsJson).RequireString("path"));

        // 收尾改用 </call>（真机形态）⇒ 同样要归得出
        var dsml2 = dsml.Replace("</\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke>", "</call>", StringComparison.Ordinal);
        var result2 = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(dsml2));
        Assert.Equal(ToolParseStatus.Ok, result2.Status);
        Assert.Equal("read", result2.Call!.Name);
    }

    [Fact]
    public void 归一_裸名加XML参数_补上call外壳()
    {
        // 真机（2026-10-11 M1 复测）：块头在、元素在，但根不是 call、名字没包 <name> ⇒ 判「空块」白花一轮。
        var raw = H + " read<path>/abs/a.txt</path><maxLines>3</maxLines></read><risk>none</risk>";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(raw));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("read", result.Call!.Name);
        var args = ToolArgs.Parse(result.Call.ArgumentsJson);
        Assert.Equal("/abs/a.txt", args.RequireString("path"));
        Assert.Equal(3, args.OptionalInt("maxLines"));
    }

    [Fact]
    public void 归一_裸名加XML参数_不误伤行式与已带外壳的块()
    {
        // 负例闸门：新判据必须能区分「裸名+元素」与两种**已经合法**的形状 —— 不许误伤。
        var lineForm = H + " read\npath: a.cs\n";
        Assert.Equal(lineForm, ToolSyntaxNormalizer.Normalize(lineForm));          // 行式：逐字节不变

        var wrapped = H + " <call><name>read</name><path>a.cs</path></call>";
        Assert.Equal(wrapped, ToolSyntaxNormalizer.Normalize(wrapped));            // 已带外壳：逐字节不变

        var headerless = "<read><path>a.cs</path></read>";                          // 无块头：本规则不碰
        Assert.Equal(headerless, ToolSyntaxNormalizer.Normalize(headerless));
    }

    [Fact]
    public void 归一_根是工具名_补上call外壳()
    {
        // 真机（2026-10-11 全量）M6·M10：根是 <exec> 而不是 <call>、又没 <name> ⇒ 解析器判「没有 <name>」。
        var raw = H + " <exec><command>echo hi</command><timeoutSeconds>10</timeoutSeconds></exec>";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(raw));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("exec", result.Call!.Name);
        Assert.Equal("echo hi", ToolArgs.Parse(result.Call.ArgumentsJson).RequireString("command"));
    }

    [Fact]
    public void 归一_裸名加元素_无收盘与换行分隔_都补外壳()
    {
        // 真机（2026-10-11 全量）M4：裸名 + 元素，**没有收盘**。
        var noClose = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(H + " exec<command>echo hi</command>"));
        Assert.Equal(ToolParseStatus.Ok, noClose.Status);
        Assert.Equal("exec", noClose.Call!.Name);

        // 真机 M5：裸名 + 换行分隔的多个元素（末尾还跟着 <risk>）。
        var multiline = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(
            H + " exec\n<command>echo hi</command>\n<timeoutSeconds>10</timeoutSeconds>\n<risk>none</risk>"));
        Assert.Equal(ToolParseStatus.Ok, multiline.Status);
        Assert.Equal("exec", multiline.Call!.Name);
        var args = ToolArgs.Parse(multiline.Call.ArgumentsJson);
        Assert.Equal("echo hi", args.RequireString("command"));
        Assert.Equal(10, args.OptionalInt("timeoutSeconds"));
    }

    [Fact]
    public void 归一_DSML参数用键名收尾_也认得()
    {
        // 真机（2026-10-11 全量）M8：DSML 的 parameter 用**键名**收尾（</path> 而不是 </parameter>）。
        const string dsml =
            "<\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>\n"
            + "<\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke name=\"read\">\n"
            + "<\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter name=\"path\">/abs/a.txt</path>\n"
            + "<\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter name=\"maxLines\">3</maxLines>\n"
            + "</\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke>\n"
            + "</\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(dsml));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("read", result.Call!.Name);
        var args = ToolArgs.Parse(result.Call.ArgumentsJson);
        Assert.Equal("/abs/a.txt", args.RequireString("path"));
        Assert.Equal(3, args.OptionalInt("maxLines"));
    }

    [Fact]
    public void 归一_call开盘但用工具名收盘_也认得()
    {
        // 真机（2026-10-11 M8 · low 档）：<call><name>read</name>…</read> —— 开盘合规、**收盘写成工具名** ⇒ 整篇拒。
        var raw = H + " <call><name>read</name><path>/abs/a.txt</path></read><risk>none</risk>";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(raw));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("read", result.Call!.Name);
        Assert.Equal("/abs/a.txt", ToolArgs.Parse(result.Call.ArgumentsJson).RequireString("path"));

        // 负例：规范块不得被改动
        var good = H + " <call><name>read</name><path>a.cs</path></call>";
        Assert.Equal(good, ToolSyntaxNormalizer.Normalize(good));
    }

    // ---------------- 真机两形态（2026-10-14 · 主人 23:04 会话；34 条事件含 DSML）----------------

    [Fact]
    public void 真机_裸DSML开场标记_后面跟正确行式_标记连残行一起剥掉()
    {
        // 真机原文（E5）：模型只漏了一个开场标记，块本身是**正确**的行式。
        // 旧行为：剥标记后剩一个 ` calls>` 残行（虽然不致命，但白留在正文里 → 进流 → 下一轮读回 ⇒ 自我强化）。
        var raw = "Let me run several searches in parallel:\n\n" + ControlTokens.Dsml + " calls>\n"
                + H + " exec{\"command\":\"echo hi\"}<risk>none</risk>";

        var normalized = ToolSyntaxNormalizer.Normalize(raw);

        Assert.DoesNotContain(ControlTokens.Dsml, normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("calls>", normalized, StringComparison.Ordinal);          // 残行也不留
        var result = ToolReport.Parse(normalized);
        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("exec", result.Call!.Name);
        Assert.Equal("echo hi", ToolArgs.Parse(result.Call.ArgumentsJson).RequireString("command"));
    }

    [Fact]
    public void 真机_整套原生块_calls_invoke_parameter_归一行式且无残留()
    {
        // 真机原文（第二形态）：`<｜｜DSML｜｜ calls>` + `<｜｜DSML｜｜ invoke name="exec">`
        //   + `<｜｜DSML｜｜ parameter name="command" string="true">`（带属性）—— 形状归一照旧，标记一个不留。
        var raw = ControlTokens.Dsml + " calls>\n"
                + ControlTokens.Dsml + " invoke name=\"exec\">\n"
                + ControlTokens.Dsml + " parameter name=\"command\" string=\"true\">echo hi</" + ControlTokens.Dsml + " parameter>\n"
                + "</" + ControlTokens.Dsml + " invoke>";

        var normalized = ToolSyntaxNormalizer.Normalize(raw);

        Assert.DoesNotContain(ControlTokens.Dsml, normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("calls>", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("invoke", normalized, StringComparison.Ordinal);          // 已归一行式，不留原生块残骸
        var result = ToolReport.Parse(normalized);
        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("exec", result.Call!.Name);
        Assert.Equal("echo hi", ToolArgs.Parse(result.Call.ArgumentsJson).RequireString("command"));
    }

    [Fact]
    public void 真机_紧凑JSON里带裸换行_自动归一成行式加多行块()
    {
        // 真机（E184 → E185/E187/E189 连拒三次）：`[TOOL] write{"path":…,"content":"…"}` ——
        // content 里是**真实换行**（模型把整篇正文塞进 JSON，没转义）⇒ 行式归一按行切就碎了 ⇒ 整篇拒。
        // 判据：运行时**自动**改成行式 + `content: <<` 多行块（正是模型自己重试时写的那一种）—— 省一轮。
        var raw = H + " write{\"path\":\"docs/A.md\",\"content\":\"# 标题\n\n第一段\n第二段\"}";

        var result = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(raw));

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal("write", result.Call!.Name);
        var args = ToolArgs.Parse(result.Call.ArgumentsJson);
        Assert.Equal("docs/A.md", args.RequireString("path"));
        Assert.Contains("第一段", args.RequireString("content"), StringComparison.Ordinal);
        Assert.Contains("第二段", args.RequireString("content"), StringComparison.Ordinal);
    }

    [Fact]
    public void 紧凑JSON_括号配不平_原样交解析器响亮地拒()
    {
        // 归一优先，**拒绝是最后手段**：配不平就一字不改（不许猜）。
        var broken = H + " write{\"path\":\"docs/A.md\"";

        Assert.Equal(broken, ToolSyntaxNormalizer.Normalize(broken));
    }
}

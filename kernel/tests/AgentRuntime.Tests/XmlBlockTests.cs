using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **XML 块通道**（主人 2026-10-07 令：解析走 XML 序列化，不手写行扫描）。
/// 这里钉四件事：① 四面各自**往返**（写出去再读回来逐字段相等）；② **确定性**（同值两次字节相等）；
/// ③ **块外文本进不来**（元素自定界 ⇒ 回声污染从结构上消失 —— 本次病灶）；④ 坏形状**响亮抛**。
/// </summary>
public sealed class XmlBlockTests
{
    // ---------------- ① 往返 + ② 确定性 ----------------

    [Fact]
    public void 工具调用_往返_且确定()
    {
        var call = new ToolCallXml
        {
            Name = "exec",
            Command = "wc -c /abs/f.txt; wc -l /abs/f.txt",
            TimeoutSeconds = 30,
            Risk = "none",
        };

        var xml = call.ToXml();
        Assert.Equal(xml, call.ToXml());                                  // 确定性
        Assert.DoesNotContain("<?xml", xml, StringComparison.Ordinal);    // 片段：无声明
        Assert.DoesNotContain("xmlns:i", xml, StringComparison.Ordinal);  // 写侧清理过

        var back = ToolCallXml.Take($"[TOOL] {xml}");
        Assert.NotNull(back);
        Assert.Equal(call.Name, back!.Name);
        Assert.Equal(call.Command, back.Command);
        Assert.Equal(30, back.TimeoutSeconds);
        Assert.Equal("none", back.Risk);
    }

    [Fact]
    public void 派发_往返_带多地址()
    {
        var dispatch = new DispatchXml
        {
            Purpose = "取这个文件的计量读数",
            Kind = "READ",
            Criteria = "full",
            When = "sync",
            SideEffects = "none",
            Scope = XmlList.Of(["/abs/a.txt", "/abs/b.txt"]),
            Ask = "只要数字与身份",
        };

        var back = DispatchXml.Take($"[INTENT] {dispatch.ToXml()}");
        Assert.NotNull(back);
        Assert.Equal("READ", back!.Kind);
        Assert.Equal(["/abs/a.txt", "/abs/b.txt"], back.Scope!.ToArray());
        Assert.Equal("只要数字与身份", back.Ask);
        Assert.Equal("full", back.Criteria);
    }

    [Fact]
    public void 回执_往返_且交付键是一等成员()
    {
        var receipt = new ReceiptXml
        {
            Kind = "done",
            Result = "文件计量读数",
            Answer = "135 字节 / 2 行",
            Basis = "/abs/f.txt:1-2 | shasum rc=0",
            Covered = "full",
            Evidence = XmlList.Of(["shasum rc=0"]),
        };

        var back = ReceiptXml.Take($"[RESULT] {receipt.ToXml()}");
        Assert.NotNull(back);
        Assert.Equal("done", back!.Kind);
        Assert.Equal("135 字节 / 2 行", back.Answer);
        Assert.Equal("full", back.Covered);
        Assert.Equal("/abs/f.txt:1-2 | shasum rc=0", back.Basis);
        Assert.Null(back.Open);                                        // 没设 ⇒ 缺省（不是空元素）

        // 旧块头也认
        Assert.NotNull(ReceiptXml.Take($"[WORKER] {receipt.ToXml()}"));
    }

    [Fact]
    public void 临时回执与裁定_往返()
    {
        var ruling = new RulingXml
        {
            Ask = "两个文件都像，我定不了",
            Kind = "CHOOSE_TARGET",
            Options = XmlList.Of(["/abs/a.md", "/abs/b.md"]),
        };
        var backRuling = RulingXml.Take($"[RULING] {ruling.ToXml()}");
        Assert.NotNull(backRuling);
        Assert.Equal("CHOOSE_TARGET", backRuling!.Kind);
        Assert.Equal(["/abs/a.md", "/abs/b.md"], backRuling.Options!.ToArray());

        var verdict = new VerdictXml { Decision = "PICK", Value = "2", Re = "d-abc123def456" };
        var backVerdict = VerdictXml.Take($"[RULING-ANSWER] {verdict.ToXml()}");
        Assert.NotNull(backVerdict);
        Assert.Equal("PICK", backVerdict!.Decision);
        Assert.Equal("2", backVerdict.Value);
        Assert.Equal("d-abc123def456", backVerdict.Re);
    }

    // ---------------- ③ 块外文本进不来（本次病灶） ----------------

    [Fact]
    public void 块外的回声_进不来_所以不会出现幻影重复参数()
    {
        // 2026-10-07 真机原形：worker 把上下文里的事件行**抄进了回复**，
        // 于是回复里出现了第二个 `timeoutSeconds: 30` —— 旧的行扫描把它当成"参数重复" ⇒ 整条命令被拒。
        var reply =
            "[TOOL] <call><name>exec</name><command>wc -c /abs/f.txt; wc -l /abs/f.txt</command><timeoutSeconds>30</timeoutSeconds></call>\n"
            + "E103 [PERMISSIONFILED] exec command: wc -c /abs/f.txt\n"
            + "timeoutSeconds: 30 → 留档：本会话还没点头过这个动作\n"
            + "timeoutSeconds: 30\n"
            + "[RESULT] <receipt><kind>partial</kind><answer>还没量到</answer><open><item>命令被拒</item></open></receipt>\n";

        var call = ToolCallXml.Take(reply);
        Assert.NotNull(call);
        Assert.Equal("exec", call!.Name);
        Assert.Equal(30, call.TimeoutSeconds);                               // **只有一个**：块外那两行不算
        Assert.DoesNotContain("PERMISSIONFILED", call.Command!, StringComparison.Ordinal);
        Assert.DoesNotContain("留档", call.Command!, StringComparison.Ordinal);

        // 同一段回复里另一个块也照常取得（互不吞并）
        var receipt = ReceiptXml.Take(reply);
        Assert.NotNull(receipt);
        Assert.Equal("partial", receipt!.Kind);
        Assert.Equal(["命令被拒"], receipt.Open!.ToArray());
    }

    // ---------------- ④ 坏形状响亮抛 ----------------

    [Fact]
    public void 坏形状_取块不吞但译码响亮()
    {
        // **取块**是探针：块后不是合法 XML（例：旧的 key: value 形状、或没闭合）⇒ 返回 null（**不抛**）——
        // 走哪条路是调用方的判断（XML 优先，行式归一）。
        Assert.Null(ToolCallXml.Take("[TOOL] <call><name>exec</name>"));
        Assert.Null(ToolCallXml.Take("[TOOL] write\npath: /tmp/a\n"));

        // **已经确定是 XML**（例：ToolReport 取到的元素、ToolArgs 拿到的参数）⇒ 坏形状**响亮抛**，
        // 绝不静默给半截。
        Assert.Throws<System.Xml.XmlException>(() => ToolCallXml.FromElement("<call><name>exec</name>"));

        // 写/读两边各自的坏形状包法：DCS 读（存储侧）把 XmlException 包成 InvalidDataException。
        Assert.Throws<InvalidDataException>(() => DataContractXml.FromFragment<ToolCallXml>("<nope><name>x</name></nope>"));
    }

    [Fact]
    public void 没有该块_返回null()
    {
        Assert.Null(ToolCallXml.Take("这一轮没调工具，只说了句话。"));
        Assert.Null(ReceiptXml.Take("[INTENT] <intent><purpose>x</purpose></intent>"));
    }

    // ---------------- ⑥ 真机回放：派发/回执换成 XML 后，解析照旧 ----------------

    [Fact]
    public void 真机回放_派发与回执换成XML后_解析照旧()
    {
        var dispatch =
            "[INTENT] <intent><purpose>取计量读数</purpose><kind>READ</kind><criteria>full</criteria>"
            + "<when>sync</when><sideEffects>none</sideEffects><scope>/abs/a.txt</scope></intent>";
        var parsed = DispatchParser.Parse(dispatch);
        var one = Assert.Single(parsed.Dispatches);
        Assert.Equal("READ", DispatchIntents.Name(one.Kind!.Value));
        Assert.Equal(["/abs/a.txt"], one.Window);
        Assert.Empty(one.Warnings);

        // 回执：**交付键是一等成员**；块后紧跟回声 —— 一个字段也污染不了。
        var receipt =
            "[RESULT] <receipt><kind>done</kind><answer>135 字节</answer><addr>/abs/a.txt</addr>"
            + "<bytes>135</bytes><lines>2</lines><sha256>de66</sha256><basis>shasum rc=0</basis></receipt>\n"
            + "bytes: 999\nlines: 7\n";

        var p = WorkerReportParser.Parse(receipt);
        Assert.True(p.Found);
        Assert.Equal(ReceiptKind.Done, WorkerReceiptShape.KindOf(receipt));
        Assert.Null(WorkerReceiptShape.MissingKindField(receipt, ReceiptKind.Done));   // 该类的键齐
        Assert.Equal("135", WorkerReceiptShape.Value(receipt, "bytes"));              // 回声里那个 999 不算
        Assert.Equal("2", WorkerReceiptShape.Value(receipt, "lines"));
    }

    // ---------------- ⑤ 真机回放：工具面换轨后，回声不再弄脏调用 ----------------

    [Fact]
    public void 真机回放_工具块换成XML后_回声不再弄脏调用()
    {
        // 2026-10-07 真机同形：worker 把上下文里的事件行抄进回复（多出两个 `timeoutSeconds: 30`）。
        // 旧路：`ToolArgs.Parse` 按行扫 ⇒ “参数重复” ⇒ **整条命令被拒**（worker 死在这里）。
        var reply =
            "[TOOL] <call><name>exec</name><command>wc -c /abs/f.txt; wc -l /abs/f.txt; shasum -a 256 /abs/f.txt</command><timeoutSeconds>30</timeoutSeconds></call>\n"
            + "E103 [PERMISSIONFILED] exec command: wc -c /abs/f.txt\n"
            + "timeoutSeconds: 30 → 留档：本会话还没点头过这个动作\n"
            + "timeoutSeconds: 30\n";

        var parsed = ToolReport.Parse(reply);
        Assert.Equal(ToolParseStatus.Ok, parsed.Status);

        var call = Assert.Single(parsed.Calls);
        Assert.Equal("exec", call.Name);

        var args = ToolArgs.Parse(call.ArgumentsJson, call.Name);   // ← 旧路当场抛“重复键”
        Assert.Equal("wc -c /abs/f.txt; wc -l /abs/f.txt; shasum -a 256 /abs/f.txt", args.OptionalString("command"));
        Assert.Equal("30", args.OptionalString("timeoutSeconds"));
        Assert.DoesNotContain("留档", args.Raw, StringComparison.Ordinal);
    }
}

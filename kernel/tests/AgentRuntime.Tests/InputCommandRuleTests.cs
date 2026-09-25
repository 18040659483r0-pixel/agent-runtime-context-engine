using System.Text.RegularExpressions;
using AgentRuntime.Tui;

namespace AgentRuntime.Tests;

/// <summary>
/// **输入判定闸门**（主人 2026-09-22 22:2x 真机报）：粘一个绝对路径进输入区，整条被当成斜杠命令**吃掉**。
/// <para>
/// 修法：判定看**名字**不看首字符 —— 只有首个词命中 <see cref="PanelRouter.Commands"/> 才算命令；
/// 其余（含 <c><home>/…</c> 这种路径）一律当文本。要强制当文本写 <c>//…</c>。
/// </para>
/// </summary>
public sealed class InputCommandRuleTests
{
    [Theory]
    // 真实现场：绝对路径 = 文本（修前会被吃掉）
    [InlineData("<repo-root>/whitebox-workspace", false)]
    [InlineData("/tmp/My Documents/a.docx", false)]
    [InlineData("/Applications/Microsoft Word.app", false)]
    // 名单内 = 命令（大小写不敏感；允许前导空白；带参数照旧）
    [InlineData("/help", true)]
    [InlineData("/HELP", true)]
    [InlineData("  /stack", true)]
    [InlineData("/stack dump /tmp/x.txt", true)]
    [InlineData("/decide 2", true)]
    // 转义 / 未登记 / 非命令
    [InlineData("//help", false)]
    [InlineData("//tmp/x", false)]
    [InlineData("/hepl", false)]
    [InlineData("/", false)]
    [InlineData("普通文本 / 带斜杠", false)]
    [InlineData("", false)]
    public void 只有已登记命令名才算命令(string line, bool expected) =>
        Assert.Equal(expected, PanelRouter.IsCommand(line));

    [Theory]
    [InlineData("//help", "/help")]
    [InlineData("  //tmp/x", "/tmp/x")]
    [InlineData("/help", "/help")]                    // 非转义：原样返回（文本路径不会带它）
    [InlineData("/tmp/x", "/tmp/x")]
    public void 双斜杠转义只脱一层(string line, string expected) =>
        Assert.Equal(expected, PanelRouter.Unescape(line));

    /// <summary>
    /// **名单 ↔ 帮助文案双向一致**：名单里的每条都要在帮助里露面；帮助里出现的每条命令都必须已登记
    /// （修前那种「以为能跑、其实吃掉」的错，正是这条闸门要拦的）。
    /// </summary>
    [Theory]
    // 工作期间敲进来的那一句：命中命令名单 ⇒ 按命令执行；名称不对/转义的 ⇒ 当文本（插话）。
    // 现场（2026-09-22 23:3x 真机）：`/result` 被当插话发给模型（模型回「`/result` 收到」），还白开一张卡。
    [InlineData("/result", true)]
    [InlineData("/closeout", true)]
    [InlineData("  /stack", true)]
    [InlineData("//result", false)]
    [InlineData("继续完成上次的任务", false)]
    [InlineData("/tmp/x", false)]
    public void 工作期间插话_命令按命令走_其余当文本(string steer, bool expected) =>
        Assert.Equal(expected, SplitSession.SteerIsCommand(steer));

    [Fact]
    public void 命令名单与帮助文案双向一致()
    {
        var help = PanelRouter.HelpLines();
        var joined = string.Join('\n', help);

        foreach (var command in PanelRouter.Commands)
        {
            // 词边界：/tail 不该被 /tail-clear 蒙混过关
            Assert.Matches(new Regex(Regex.Escape(command) + @"(?![-\w])"), joined);
        }

        var firstTokens = help
            .Select(static l => l.StartsWith("[帮助] ", StringComparison.Ordinal) ? l["[帮助] ".Length..] : l)
            .Select(static t => Regex.Match(t, @"^/[A-Za-z?\-]*"))       // 行首那一个命令名（到第一个非命令字符为止）
            .Where(static m => m.Success)
            .Select(static m => m.Value)
            .ToArray();

        var unknown = firstTokens.Where(t => !PanelRouter.Commands.Contains(t)).ToArray();
        Assert.Empty(unknown);
    }
}

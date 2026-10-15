using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **可选字段不合形 ⇒ 归一或忽略，不判死整个调用**（2026-09-30，常规检测料 write 场景抓到）。
/// <para>真机：worker 的 `timeoutSeconds` 值里粘着下一行事件文本（`10E058 [PERMISSIONFILED] …`）
/// ⇒ 旧实现直接抛错 ⇒ **整个调用被拒** ⇒ 一次补发（一杀一次，现场级联到 4 次）。
/// 医嘱：可选字段不该当判据（§十·47「自然表达由运行时归一，拒绝是最后手段」）。</para>
/// </summary>
public class ToolArgsOptionalIntTests
{
    [Fact]
    public void StrictInteger_IsParsed()
    {
        Assert.Equal(10, ToolArgs.Parse("command: echo hi\ntimeoutSeconds: 10", "exec").OptionalInt("timeoutSeconds"));
    }

    [Fact]
    public void ValueWithTrailingJunk_IsNormalizedToFirstInteger()
    {
        // 真机同形：值里粘着下一行事件文本
        var args = ToolArgs.Parse(
            "command: echo hi\ntimeoutSeconds: 10E058 [PERMISSIONFILED] exec command: mkdir -p /tmp/x", "exec");
        Assert.Equal(10, args.OptionalInt("timeoutSeconds"));
    }

    [Fact]
    public void NoIntegerAtAll_IsIgnored_NotThrown()
    {
        var args = ToolArgs.Parse("command: echo hi\ntimeoutSeconds: soon", "exec");
        Assert.Null(args.OptionalInt("timeoutSeconds"));   // 忽略（用默认值），**不**把整个调用判死
    }

    [Fact]
    public void MissingKey_IsNull()
    {
        Assert.Null(ToolArgs.Parse("command: echo hi", "exec").OptionalInt("timeoutSeconds"));
    }
}

using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **master 请求改节拍上限**（主人 2026-10-09：「让 master 直接设定」）：
/// `[PACE] ceiling=300|600|off` —— **白名单外一律响亮拒绝**，绝不静默当成没发生。
/// </summary>
public sealed class PaceRequestTests
{
    [Theory]
    [InlineData("[PACE] ceiling=300", "300")]
    [InlineData("[PACE] ceiling=600", "600")]
    [InlineData("[PACE] ceiling=off", "off")]
    [InlineData("先说明一下……\n[PACE] ceiling: 600\n后面还有话", "600")]   // 混在正文里也认；`: ` 与 `=` 都行
    public void 白名单内_认(string text, string expect)
    {
        var (ok, refusal) = PaceRequest.Parse(text);

        Assert.Null(refusal);
        Assert.Equal(expect, ok!.Ceiling);
    }

    [Theory]
    [InlineData("[PACE] ceiling=0")]
    [InlineData("[PACE] ceiling=9999")]
    [InlineData("[PACE] ceiling=never")]
    public void 白名单外_响亮拒绝(string text)
    {
        var (ok, refusal) = PaceRequest.Parse(text);

        Assert.Null(ok);
        Assert.Contains("只认这三档", refusal!, System.StringComparison.Ordinal);
        Assert.Contains("没被采纳", refusal!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 没有这一行_什么都不做()
    {
        var (ok, refusal) = PaceRequest.Parse("这轮我什么都没改");

        Assert.Null(ok);
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData("[PACE] gate=216", 216)]
    [InlineData("[PACE] gate: 400", 400)]
    [InlineData("[PACE] gate=60", 60)]
    [InlineData("[PACE] gate=1800", 1800)]
    public void gate_区间内_认(string text, double gate)
    {
        var (ok, refusal) = PaceRequest.Parse(text);

        Assert.Null(refusal);
        Assert.Equal(gate, ok!.GateSeconds);
    }

    [Theory]
    [InlineData("[PACE] gate=59")]
    [InlineData("[PACE] gate=1801")]
    [InlineData("[PACE] gate=abc")]
    public void gate_区间外_响亮拒绝(string text)
    {
        var (ok, refusal) = PaceRequest.Parse(text);

        Assert.Null(ok);
        Assert.Contains("没被采纳", refusal!, System.StringComparison.Ordinal);
    }
}

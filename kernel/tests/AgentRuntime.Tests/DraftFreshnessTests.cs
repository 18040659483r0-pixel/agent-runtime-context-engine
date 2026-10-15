using System;
using AgentRuntime.Core.Draft;
using Xunit;

namespace AgentRuntime.Tests;

public class DraftFreshnessTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 9, 27, 2, 0, 0, TimeSpan.FromHours(8));
    private const string Cur = "sess-2026-09-27";

    [Fact]
    public void Red_OldSessionDraft_IsStale()
        => Assert.True(DraftFreshness.IsStale("sess-2026-09-26", Cur, false, 3, SessionStart.AddHours(-5), SessionStart));

    [Fact]
    public void Red_ManualTurnZero_OlderThanSession_IsStale()
        => Assert.True(DraftFreshness.IsStale(Cur, Cur, isManual: true, turn: 0, SessionStart.AddHours(-2), SessionStart));

    [Fact]
    public void Red_MissingSessionKey_IsStale_FailClosed()
        => Assert.True(DraftFreshness.IsStale(null, Cur, false, 0, SessionStart, SessionStart));

    [Fact]
    public void Green_CurrentSessionDraft_Survives()
        => Assert.False(DraftFreshness.IsStale(Cur, Cur, false, 2, SessionStart.AddMinutes(5), SessionStart));

    [Fact]
    public void Green_ManualTurnZero_ButAfterSessionStart_Survives()
        => Assert.False(DraftFreshness.IsStale(Cur, Cur, isManual: true, turn: 0, SessionStart.AddMinutes(1), SessionStart));

    [Fact]
    public void Filter_KeepsOnlyCurrentSession()
    {
        var cur = new DraftEntry { SessionKey = Cur };
        var old = new DraftEntry { SessionKey = "sess-other" };
        var none = new DraftEntry();
        var kept = DraftFreshness.FilterForInjection(new[] { cur, old, none }, Cur);
        Assert.Single(kept);
        Assert.Equal(Cur, kept[0].SessionKey);
    }
}

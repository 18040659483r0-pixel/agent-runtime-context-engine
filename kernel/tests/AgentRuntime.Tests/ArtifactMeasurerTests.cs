using System;
using System.IO;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **批 3/3（2026-09-30）**：读数由**运行时实测**派生，不再采信模型自述。
/// <para>真机同形（同一天两次）：回执报「214 字节 / shasum 3f2a1b9c…」（盘上 20 字节）；
/// 回执报「2084 行 / 271226 字节」（盘上 2078 行 / 270974 字节）。</para>
/// </summary>
public class ArtifactMeasurerTests
{
    private static string Temp(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "am-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void MeasureLine_ReportsRealBytesLinesAndSha()
    {
        string p = Temp("A\nB\nC\n");   // 3 行 / 6 字节
        try
        {
            var line = ArtifactMeasurer.MeasureLine($"changes: {p}");

            Assert.NotNull(line);
            Assert.StartsWith(ArtifactMeasurer.MeasuredPrefix, line, StringComparison.Ordinal);
            Assert.Contains($"path={p}", line, StringComparison.Ordinal);
            Assert.Contains("bytes=6", line, StringComparison.Ordinal);
            Assert.Contains("lines=3", line, StringComparison.Ordinal);
            Assert.Contains(ArtifactMeasurer.Sha256(p), line, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void MeasureLine_NoExistingCandidate_IsNull_DoesNotInvent()
    {
        string missing = "/tmp/never-" + Guid.NewGuid().ToString("N") + ".txt";
        Assert.Null(ArtifactMeasurer.MeasureLine($"changes: {missing}"));
    }

    [Fact]
    public void CrossCheck_ClaimedBytesConflictWithMeasurement_Fails_WithBothNumbers()
    {
        string p = Temp("A\nB\nC\n");
        try
        {
            string measured = ArtifactMeasurer.Line(p);

            // 真机同形：自述 214 字节，盘上 6 字节
            var v = ArtifactMeasurer.CrossCheck($"changes: {p} bytes=214", measured);

            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.Contains("214", v.Missing, StringComparison.Ordinal);
            Assert.Contains("6", v.Missing, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void CrossCheck_Bare64HexWithoutKey_Fails_NotRecomputable()
    {
        string p = Temp("A\nB\nC\n");
        try
        {
            string measured = ArtifactMeasurer.Line(p);

            // 真机同形：一串十六进制逐位递降的假摘要，没按 sha256= 键给
            var v = ArtifactMeasurer.CrossCheck(
                $"changes: {p} answer=shasum 读数 3f2a1b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0",
                measured);

            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.Contains("sha256=", v.Missing, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void CrossCheck_StructuredReadingsMatchMeasurement_IsCorroborated()
    {
        string p = Temp("A\nB\nC\n");
        try
        {
            string measured = ArtifactMeasurer.Line(p);
            var v = ArtifactMeasurer.CrossCheck($"changes: {p} bytes=6 lines=3 sha256={ArtifactMeasurer.Sha256(p)}", measured);
            Assert.True(v.Ok);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void CrossCheck_ProseOnlyNoReadings_DoesNotPretendToBeChecked()
    {
        // 散文数字不带键 ⇒ 不在本类判据内（那是契约层的事）⇒ 不失败，但也**不冒充已核**。
        string p = Temp("A\nB\nC\n");
        try
        {
            string measured = ArtifactMeasurer.Line(p);
            var v = ArtifactMeasurer.CrossCheck($"changes: {p} answer=文件写好了，三行都落盘", measured);
            Assert.True(v.Ok);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void StripMeasured_RemovesRuntimeLine_SoItDoesNotCompareWithItself()
    {
        string text = "changes: /tmp/x.txt\nmeasured: path=/tmp/x.txt bytes=6 lines=3 sha256=abc";
        var stripped = ArtifactMeasurer.StripMeasured(text);

        Assert.DoesNotContain(ArtifactMeasurer.MeasuredPrefix, stripped, StringComparison.Ordinal);
        Assert.Contains("changes:", stripped, StringComparison.Ordinal);
    }
}

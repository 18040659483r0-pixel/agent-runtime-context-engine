using System;
using System.IO;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// ③ R3 的 **worker 侧**夹具组：回执契约必须 fail-closed，且证据须能被 master 复算。
/// 与 master 侧 WorkerSettleGateTests 成对：那一侧管“派发项如何结算”，这一侧管“回执本身合不合格”。
/// </summary>
public class WorkerReceiptContractTests
{
    private static string TempFile(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "wrc-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    // ---------- 应红 ----------

    [Fact]
    public void Red_OkFalse_WithoutCauseAndMissing_IsNotWellFormed()
    {
        var r = new CrossProviderTaskResult { TaskId = "t1", Ok = false };
        Assert.False(r.IsWellFormed); // 空壳回执 = 假回执
    }

    [Fact]
    public void Red_OkTrue_WithoutEvidence_IsNotWellFormed()
    {
        var r = new CrossProviderTaskResult { TaskId = "t2", Ok = true, Conclusion = "done" };
        Assert.False(r.IsWellFormed); // 无证据的结论按失败计
    }

    [Fact]
    public void Red_EvidencePointsToMissingFile_IsUncorroborated_AndRetryable()
    {
        var v = EvidenceVerifier.VerifyFile("/tmp/never-" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonPathMissing, v.Reason);
        Assert.True(v.Retryable);
    }

    // ---------- 防假红 ----------

    [Fact]
    public void Green_OkFalse_WithCauseAndMissing_IsWellFormed_AndMustResend()
    {
        var r = CrossProviderTaskResult.Failure("t3", WorkerFailureReason.PathNotReachable, "window 未绝对化", retryable: true);
        Assert.True(r.IsWellFormed);
        Assert.True(r.Retryable); // ok=false 且 retryable ⇒ 必须带修正补发（不许 master 顶上）
    }

    [Fact]
    public void Green_OkTrue_WithCorroboratedEvidence_IsWellFormed()
    {
        string p = TempFile("Z40-OK\n");
        try
        {
            long len = new FileInfo(p).Length;
            var v = EvidenceVerifier.VerifyFile(p, expectedContent: "Z40-OK", expectedBytes: len);
            Assert.True(v.Ok);
            var r = new CrossProviderTaskResult
            {
                TaskId = "t4", Ok = true, Conclusion = "写入并读回",
                Evidence = new[] { "path=" + p + " content=Z40-OK bytes=" + len },
            };
            Assert.True(r.IsWellFormed);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void WorkerValue_MustEqualMasterValue_ByteForByte()
    {
        Assert.True(RetryPolicy.Corroborates("Z40-OK", "Z40-OK"));
        Assert.False(RetryPolicy.Corroborates("Z40-OK", "Z40-NO"));
        Assert.False(RetryPolicy.Corroborates("", "Z40-OK"));
    }
}

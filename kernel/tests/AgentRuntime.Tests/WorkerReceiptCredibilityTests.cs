using System;
using System.IO;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 坑 #193 的判据夹具：worker 回执自称 changes=/evidence.path= 指向某文件时，
/// **必须由派发侧按磁盘复算**；盘上无该文件 ⇒ 该次回执判 ok=false + 可重试。
/// 这些条目不依赖 worker 自述，只依赖 master 侧文件系统事实。
/// </summary>
public class WorkerReceiptCredibilityTests
{
    private static string Temp(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "wrc-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Red_ReceiptClaimsChangedFile_ButFileMissing_IsUncorroborated_AndRetryable()
    {
        // 场景来自实测：回执写 changes=.../worker/z68.txt，而盘上无该文件
        string claimed = Path.Combine(Path.GetTempPath(), "z68-claimed-" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.False(File.Exists(claimed));

        var v = EvidenceVerifier.VerifyFile(claimed);

        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonPathMissing, v.Reason);
        Assert.True(v.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(v.Missing));
    }

    [Fact]
    public void Red_ReceiptClaimsBytes_ButBytesDiffer_IsUncorroborated()
    {
        string p = Temp("Z68-OK\n"); // 7 字节
        try
        {
            var v = EvidenceVerifier.VerifyFile(p, expectedBytes: 99);
            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.True(v.Retryable);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Green_ReceiptClaimsFile_AndFactsMatch_IsCorroborated()
    {
        string p = Temp("Z68-OK\n");
        try
        {
            long n = new FileInfo(p).Length;
            var v = EvidenceVerifier.VerifyFile(p, expectedContent: "Z68-OK", expectedBytes: n);
            Assert.True(v.Ok);
            Assert.Equal(string.Empty, v.Reason);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Failure_ReceiptShape_CarriesReasonMissingRetryable()
    {
        var r = CrossProviderTaskResult.Failure(
            "task-z68",
            WorkerFailureReason.EvidenceUncorroborated,
            "回执 claims 的文件不存在",
            retryable: true);

        Assert.False(r.Ok);
        Assert.Equal(WorkerFailureReason.EvidenceUncorroborated, r.Cause);
        Assert.Equal("回执 claims 的文件不存在", r.Missing);
        Assert.True(r.Retryable);
        Assert.True(r.IsWellFormed);
    }

    [Fact]
    public void ProviderTimeout_Shape_MustBeRetryable_WithReason()
    {
        // 对应实测：#W1 sync ❌ 90002ms（provider 90s 超时）——必须带病因与可重试标记
        var r = CrossProviderTaskResult.Failure(
            "task-timeout",
            WorkerFailureReason.ProviderUnreachable,
            "worker 未在 90s 内返回",
            retryable: true);

        Assert.False(r.Ok);
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, r.Cause);
        Assert.True(r.Retryable);
        Assert.True(r.IsWellFormed);
    }

    // ---------------- 2026-09-30：收据闸门三态 + 候选全试（真机两处失真） ----------------

    [Fact]
    public void Green_FirstCandidateIsAPointer_SecondIsTheRealProduct_NoLongerFalseRed()
    {
        // 真机现场：回执里先出现「带描述的名字」（被当成路径却没这个文件），真路径在后面；
        // 旧实现只试第一个 ⇒ 报「产物不存在」，而产物真在（20 B → 236 B 那一次）。
        string real = Temp("Z90-OK\n");
        try
        {
            string pointer = $"/tmp/never-{Guid.NewGuid():N}.txt:1-4";
            var v = ReceiptGate.Judge($"answer=已写入 verify.txt（三行事实） evidence={pointer} changes: /tmp/x.md | path={real}");
            Assert.True(v.Ok);
        }
        finally { File.Delete(real); }
    }

    [Fact]
    public void Red_AllCandidatesMissing_NamesThemAll()
    {
        string a = "/tmp/never-a-" + Guid.NewGuid().ToString("N") + ".txt";
        string b = "/tmp/never-b-" + Guid.NewGuid().ToString("N") + ".md";

        var v = ReceiptGate.Judge($"changes: {a} | path={b}");

        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonPathMissing, v.Reason);
        Assert.Contains(a, v.Missing, StringComparison.Ordinal);
        Assert.Contains(b, v.Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Red_ClaimedBytesDifferFromDisk_IsUncorroborated_WithBothNumbers()
    {
        string p = Temp("Z91-OK\n");   // 7 字节
        try
        {
            var v = ReceiptGate.Judge($"changes: {p} bytes=214");

            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.Contains("214", v.Missing, StringComparison.Ordinal);
            Assert.Contains(new FileInfo(p).Length.ToString(), v.Missing, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Green_ClaimedBytesMatchDisk_IsCorroborated()
    {
        string p = Temp("Z92-OK\n");
        try
        {
            long n = new FileInfo(p).Length;
            var v = ReceiptGate.Judge($"changes: {p} bytes={n}");
            Assert.True(v.Ok);
        }
        finally { File.Delete(p); }
    }

    // ---------------- 批 2/3：三个读数逐项复算（bytes / lines / sha256） ----------------

    [Fact]
    public void Red_ClaimedLinesDifferFromDisk_IsUncorroborated()
    {
        string p = Temp("L1\nL2\nL3\n");   // 3 行
        try
        {
            var v = ReceiptGate.Judge($"changes: {p} lines=2084");

            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.Contains("2084", v.Missing, StringComparison.Ordinal);
            Assert.Contains("3", v.Missing, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Red_ClaimedSha256DiffersFromDisk_IsUncorroborated()
    {
        string p = Temp("Z93-OK\n");
        try
        {
            // 真机同形：sha256 是十六进制逐位递降的串（3f2a1b9c…）—— 一个真摘要都算不上
            var v = ReceiptGate.Judge($"changes: {p} sha256=3f2a1b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0");

            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.Contains("sha256", v.Missing, StringComparison.Ordinal);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Green_AllThreeDeclaredReadingsMatchDisk_IsCorroborated()
    {
        string p = Temp("Z94-OK\n");
        try
        {
            long n = new FileInfo(p).Length;
            long l = File.ReadLines(p).Count();
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

            var v = ReceiptGate.Judge($"changes: {p} bytes={n} lines={l} sha256={sha}");
            Assert.True(v.Ok);
        }
        finally { File.Delete(p); }
    }

    // ---------------- 2026-09-30：相对路径按 base 解析（write 场景当场抓到的假红） ----------------

    [Fact]
    public void Green_RelativeCandidate_ResolvesAgainstGivenBase()
    {
        // 真机同形：worker 的 `changes: created .stage1/acc/dir-<t>/verify.txt` 是**仓库相对路径**，
        // 旧闸门拿它去 File.Exists（按**进程 cwd**）⇒ 落到错树 ⇒ 假红 ⇒ 触发一次补发。
        string dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rel.txt"), "Z95-OK\n");
        try
        {
            var v = ReceiptGate.Judge("changes: created rel.txt (7 B, 1 line)", basePath: dir);
            Assert.True(v.Ok);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Resolve_RelativeCandidate_GoesUnderRepoRoot_NotProcessCwd()
    {
        var root = WindowResolver.FindRepoRoot();
        Assert.NotNull(root);
        var r = ReceiptGate.Resolve("docs/PITFALLS.md");

        Assert.True(Path.IsPathRooted(r));
        Assert.StartsWith(root!, r, StringComparison.Ordinal);            // 落仓根下，不是进程 cwd
        Assert.NotEqual(Path.GetFullPath("docs/PITFALLS.md"), r);        // 与「按 cwd 解」不同
    }
}

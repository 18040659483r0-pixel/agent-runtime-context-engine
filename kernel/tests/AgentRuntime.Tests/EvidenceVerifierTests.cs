using System;
using System.IO;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

public class EvidenceVerifierTests
{
    private static string TempFile(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "ev-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Red_MissingProduct_IsNotOk_AndRetryable()
    {
        var v = EvidenceVerifier.VerifyFile("/tmp/never-exists-" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonPathMissing, v.Reason);
        Assert.True(v.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(v.Missing));
    }

    [Fact]
    public void Red_ContentMismatch_IsUncorroborated()
    {
        string p = TempFile("Z99-OK\n");
        try
        {
            var v = EvidenceVerifier.VerifyFile(p, expectedContent: "Z99-NO");
            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
            Assert.True(v.Retryable);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Red_BytesMismatch_IsUncorroborated()
    {
        string p = TempFile("Z99-OK\n");
        try
        {
            var v = EvidenceVerifier.VerifyFile(p, expectedBytes: 999);
            Assert.False(v.Ok);
            Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Green_ContentAndBytesMatch_IsOk()
    {
        string p = TempFile("Z99-OK\n");
        try
        {
            long len = new FileInfo(p).Length;
            var v = EvidenceVerifier.VerifyFile(p, expectedContent: "Z99-OK", expectedBytes: len);
            Assert.True(v.Ok);
            Assert.Equal("", v.Reason);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Parse_Evidence_FromBlockText()
    {
        var (path, content, bytes) = EvidenceVerifier.ParseEvidence("[WORKER] ok=true evidence= path=/tmp/z12.txt content=Z12-OK bytes=7");
        Assert.Equal("/tmp/z12.txt", path);
        Assert.Equal("Z12-OK", content);
        Assert.Equal(7, bytes);
    }

    [Fact]
    public void AgentReceipt_Z12AndZ22_Style_IsVerifiable_WhenFileExists()
    {
        string p = TempFile("Z22-OK\n");
        try
        {
            var (path, content, bytes) = EvidenceVerifier.ParseEvidence("path=" + p + " content=Z22-OK bytes=" + new FileInfo(p).Length);
            var v = EvidenceVerifier.VerifyFile(path, content, bytes);
            Assert.True(v.Ok);
        }
        finally { File.Delete(p); }
    }
}

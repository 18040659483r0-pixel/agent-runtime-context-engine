using System;
using System.IO;
using System.Security.Cryptography;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **回执声明读数的按盘复算门槛**（2026-10-07）：读类回执把 <c>addr</c>/<c>bytes</c>/<c>lines</c>/<c>sha256</c>
/// 作为**成员**交出 ⇒ 运行时按盘逐项复算，不符即判 <c>EvidenceUncorroborated</c>。
///
/// <para><b>为什么要有它</b>：旧闸门只在回执**自称产物**（<c>path=</c>/<c>changes=</c>/<c>created</c>）时才核 ⇒
/// 读类的数值声明从没被核过。真机 2026-10-07：worker 读了新值却声明了一个**陈旧的 sha</b>（与盘上不符）——
/// 形状层全绿，事实层假绿。</para>
/// </summary>
public sealed class ReceiptDeclaredReadingsTests
{
    private static (string Path, long Bytes, long Lines, string Sha) MakeFile()
    {
        var p = Path.Combine(Path.GetTempPath(), "wrp-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, "nonce=abc\ncreated=/tmp/x\n");
        var bytes = new FileInfo(p).Length;
        var lines = File.ReadLines(p).Count();
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        return (p, bytes, lines, sha);
    }

    [Fact]
    public void 声明与盘一致_通过()
    {
        var (p, b, l, sha) = MakeFile();
        try
        {
            var v = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), sha);
            Assert.NotNull(v);
            Assert.True(v!.Ok);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void 陈旧sha_被判不符_真机场景2()
    {
        var (p, b, l, _) = MakeFile();
        try
        {
            const string stale = "b37b40a3562f42d6e62014ce23cacb180df31d9cf11d201ad9a402e3baa37183";
            var v = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), stale);
            Assert.NotNull(v);
            Assert.False(v!.Ok);
            Assert.Contains("sha256 不符", v.Missing, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void 渲染行的带引号值_也要能核()
    {
        var (p, b, l, sha) = MakeFile();
        try
        {
            var v = ReceiptGate.JudgeDeclaredReadings($"\"{p}\"", $"\"{b}\"", $"\"{l}\"", $"\"{sha}\"");
            Assert.NotNull(v);
            Assert.True(v!.Ok);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void ParseReadings_认带引号的值()
    {
        const string line =
            "#W1 sync ✅ 1ms kind=done addr=\"/a.txt\" bytes=\"135\" lines=\"2\" "
            + "sha256=\"82dd9d6f92324e900831f69c84a85e9ac4f285d2bc450759d03f6e597a9efcf5\"";

        var (bytes, lines, sha) = EvidenceVerifier.ParseReadings(line);

        Assert.Equal(135, bytes);
        Assert.Equal(2, lines);
        Assert.Equal("82dd9d6f92324e900831f69c84a85e9ac4f285d2bc450759d03f6e597a9efcf5", sha);
    }

    [Fact]
    public void 缩略sha_按前缀核_对前缀通过_错前缀判不符()
    {
        var (p, b, l, sha) = MakeFile();
        try
        {
            // 真机 2026-10-07：模型交 **12 位缩略** sha —— 旧写法 `len>=16 才核` ⇒ 被跳过（假绿）。
            // 现在：是真值前缩 ⇒ 通过；编造的前缩 ⇒ 判不符。
            var ok = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), sha[..12]);
            Assert.NotNull(ok);
            Assert.True(ok!.Ok);

            var bad = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), "8caffabd980e");
            Assert.NotNull(bad);
            Assert.False(bad!.Ok);
            Assert.Contains("sha256 不符", bad.Missing, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void 照抄协议范例的sha_判不符()
    {
        var (p, b, l, _) = MakeFile();
        try
        {
            // 真机 2026-10-07：回执声明 `5534879…`（= 协议范例里的值），既非真值也不可复算。
            var v = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), "5534879…");

            Assert.NotNull(v);
            Assert.False(v!.Ok);
            Assert.Contains("不是可复算的值", v.Missing, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void unknown_不算声明()
    {
        var (p, b, l, _) = MakeFile();
        try
        {
            var v = ReceiptGate.JudgeDeclaredReadings(p, b.ToString(), l.ToString(), "unknown");

            Assert.NotNull(v);
            Assert.True(v!.Ok);   // unknown = 没声明 sha ⇒ 不因它判红（bytes/lines 仍照核）
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void 截断的路径_不当候选_不产假红()
    {
        // 真机 2026-10-07：渲染行把长值缩成 `…/acc-…txt`，扫描把它当候选 ⇒ File.Exists 必假 ⇒ **假「路径不存在」补偿**。
        Assert.Null(ReceiptGate.JudgeIfClaimed("changes: /home/…/AgentRuntime/…/acc-…txt"));
        // 真路径仍然认（闸门没被削弱）
        var existing = Path.GetTempFileName();
        try { Assert.NotNull(ReceiptGate.JudgeIfClaimed("changes: " + existing)); }
        finally { File.Delete(existing); }
    }

    [Fact]
    public void 没声明读数_不翻ok()
    {
        Assert.Null(ReceiptGate.JudgeDeclaredReadings("/tmp/whatever.txt", null, null, null));
        Assert.Null(ReceiptGate.JudgeDeclaredReadings(null, "1", "2", "abcdef0123456789abcd"));
    }
}

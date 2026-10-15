using System.IO;
using System.Text;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>事件流损坏自愈（启动路径）：坏流 ⇒ 归档 + 开新段 + 落 Hint；好流 ⇒ 逐字节不动。</summary>
public class StreamRecoveryTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wb-streamrec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteStream(string path, params long[] seqs)
    {
        var fragments = seqs.Select(s => StreamFixtures.Fragment(s, "AgentOutput", "x", tag: $"E{s}", source: "t"));
        File.WriteAllLines(path, fragments, new UTF8Encoding(false));
    }

    [Fact]
    public void CorruptStream_IsArchived_NewSegmentTellsWhere()
    {
        var dir = NewDir();
        var p = Path.Combine(dir, "stream.jsonl");
        WriteStream(p, 1, 2, 3, 3);
        var before = File.ReadAllBytes(p);

        var stream = new SessionStreamStore(p).LoadOrRecover(out var rec);

        Assert.NotNull(rec);
        Assert.Equal(p, rec!.BadPath);
        Assert.True(File.Exists(rec.ArchivedPath), "坏段必须被归档保留");
        Assert.Contains("corrupt", Path.GetFileName(rec.ArchivedPath));
        Assert.Equal(before, File.ReadAllBytes(rec.ArchivedPath));
        Assert.Equal(4L, rec.ExpectedSeq);
        Assert.Equal(3L, rec.ActualSeq);

        var lines = File.ReadAllLines(p);
        Assert.Single(lines);
        Assert.Contains("stream-recovery", lines[0]);
        Assert.Contains("archive", lines[0]);
        Assert.Equal(1L, stream.Cursor);
    }

    [Fact]
    public void HealthyStream_Untouched_NoArchiveNoHint()
    {
        var dir = NewDir();
        var p = Path.Combine(dir, "stream.jsonl");
        WriteStream(p, 1, 2, 3);
        var before = File.ReadAllBytes(p);

        var stream = new SessionStreamStore(p).LoadOrRecover(out var rec);

        Assert.Null(rec);
        Assert.Equal(3L, stream.Cursor);
        Assert.False(Directory.Exists(Path.Combine(dir, "archive")));
        Assert.Equal(before, File.ReadAllBytes(p));
    }

    [Fact]
    public void MissingFile_Empty_NoArchive()
    {
        var dir = NewDir();
        var p = Path.Combine(dir, "stream.jsonl");

        var stream = new SessionStreamStore(p).LoadOrRecover(out var rec);

        Assert.Null(rec);
        Assert.Equal(0L, stream.Cursor);
        Assert.False(Directory.Exists(Path.Combine(dir, "archive")));
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

public class WorkerReceiptReaderTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N") + ".xml");

    [Fact]
    public void Red_ReadBeforeWrite_WouldSayMissing_ButWaitFindsIt()
    {
        // 竞态复现：文件 300ms 后才出现；先直接判 exists 会是 false，等待式读法必须为 true
        string p = TempPath("late");
        _ = Task.Run(async () => { await Task.Delay(300); await File.WriteAllTextAsync(p, "late-ok"); });
        Assert.False(File.Exists(p));                    // 同批先读 = 假「不存在」
        Assert.True(WorkerReceiptReader.WaitForFile(p, 5000));  // 等待式 = 真值
        File.Delete(p);
    }

    [Fact]
    public void Red_TimeoutIsNotReady_NotEmptyVerdict()
    {
        string p = Path.Combine(Path.GetTempPath(), "never-" + Guid.NewGuid().ToString("N"));
        Assert.False(WorkerReceiptReader.WaitForFile(p, 300, 50));
    }

    [Fact]
    public void Block_IsReadVerbatim_FromStreamXml()
    {
        string p = TempPath("stream");
        StreamFixtures.Write(p,
            StreamFixtures.Fragment(1, "UserInput", "task", tag: "E001"),
            StreamFixtures.Fragment(2, "AgentOutput", "[WORKER] ok=true evidence=Z95-OK", tag: "E002"));
        Assert.Equal("[WORKER] ok=true evidence=Z95-OK", WorkerReceiptReader.ReadLastWorkerBlock(p));
        File.Delete(p);
    }

    [Fact]
    public void Block_Missing_ReturnsNull_NotFabricated()
    {
        string p = TempPath("noblock");
        StreamFixtures.Write(p, StreamFixtures.Fragment(1, "AgentOutput", "plain", tag: "E001"));
        Assert.Null(WorkerReceiptReader.ReadLastWorkerBlock(p));
        File.Delete(p);
    }

    [Fact]
    public void Contract_WorkerResult_Carries_ReasonMissingRetryable()
    {
        // ② 的自证：反射写入依赖这些成员真的存在，否则是静默空转
        var missing = WorkerReceiptReader.MissingCoreContractMembers();
        Assert.True(missing.Count == 0, "WorkerResult 缺成员：" + string.Join(",", missing));
    }

    [Fact]
    public void Verdict_Green_WhenBlockAndArtifactReady()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string batch = Path.Combine(dir, "batch");
        Directory.CreateDirectory(batch);
        string stream = Path.Combine(batch, "stream.xml");
        StreamFixtures.Write(stream, StreamFixtures.Fragment(2, "AgentOutput", "[WORKER] ok=true evidence=Z", tag: "E002"));
        File.WriteAllText(Path.Combine(batch, "dispatch.xml"),
            AgentRuntime.Core.Persistence.DataContractXml.ToFragment(
                new AgentRuntime.Hosting.Worker.WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Dispatch" }) + "\n");
        string art = Path.Combine(dir, "z.txt");
        File.WriteAllText(art, "Z95-OK");
        var (sp, blk, ready) = WorkerReceiptReader.ReadAfterResult(dir, art, 2000);
        Assert.True(ready);
        Assert.Equal(stream, sp);
        Assert.Contains("[WORKER]", blk);
        Directory.Delete(dir, true);
    }
}

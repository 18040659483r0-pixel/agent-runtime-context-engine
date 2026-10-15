using System;
using System.Reflection;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>③ R3 主侧闸门夹具：ReceiptGate 按盘复算；ok=false 必须带 reason/missing/retryable 才允许结算。</summary>
public class WorkerSettleGateReceiptTests
{
    private static string AsmPath => typeof(WorkerSettleGateReceiptTests).Assembly.Location;

    // ---------- 应红 ----------

    [Fact]
    public void Red_ReceiptWithoutAnyPath_CannotBeCorroborated()
    {
        var v = ReceiptGate.Judge("all done, tests green");
        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonUncorroborated, v.Reason);
        Assert.True(v.Retryable);
    }

    [Fact]
    public void Red_ReceiptClaimsMissingFile_IsUncorroboratedAndRetryable()
    {
        var v = ReceiptGate.Judge("created /tmp/missing-" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.False(v.Ok);
        Assert.Equal(EvidenceVerifier.ReasonPathMissing, v.Reason);
        Assert.False(string.IsNullOrWhiteSpace(v.Missing));
        Assert.True(v.Retryable);
    }

    [Fact]
    public void Red_OkFalseDescribe_MustCarryThreeFields_ForCorrectiveResend()
    {
        var v = ReceiptGate.Judge("changes=/tmp/missing-" + Guid.NewGuid().ToString("N") + ".txt");
        var d = ReceiptGate.Describe(v);
        Assert.Contains("receipt=ok=false", d);
        Assert.Contains("reason=" + v.Reason, d);
        Assert.Contains("missing=" + v.Missing, d);
        Assert.Contains("retryable=true", d);
    }

    // ---------- 防假红 ----------

    [Fact]
    public void Green_ReceiptOnRealFile_IsCorroborated()
    {
        var v = ReceiptGate.Judge("path=" + AsmPath);
        var d = ReceiptGate.Describe(v);
        Assert.True(v.Ok);
        Assert.Contains("receipt=corroborated", d);
        Assert.DoesNotContain("ok=false", d);
    }

    [Fact]
    public void Green_OkFalseWithCauseAndMissing_IsWellFormed_AndResendRequired()
    {
        var r = CrossProviderTaskResult.Failure("t-resend", WorkerFailureReason.PathNotReachable, "window 未绝对化", retryable: true);
        Assert.True(r.IsWellFormed);
        Assert.True(r.Retryable);
    }

    [Fact]
    public void Green_ExtractClaimedPath_RecognizesPathAndChangesForms()
    {
        Assert.Equal("/tmp/a.txt", ReceiptGate.ExtractClaimedPath("changes=/tmp/a.txt"));
        Assert.Equal("/tmp/b.txt", ReceiptGate.ExtractClaimedPath("path=/tmp/b.txt ok"));
        Assert.Null(ReceiptGate.ExtractClaimedPath(""));
        Assert.Null(ReceiptGate.ExtractClaimedPath(null));
    }

    [Fact]
    public void Green_ExtractClaimedPath_认_WORKER_块的冒号形()
    {
        // [WORKER] 块的字段形状是 `changes: <path>`（冒号），不是 `changes=`。
        Assert.Equal("/tmp/c.txt", ReceiptGate.ExtractClaimedPath("conclusion: done | changes: /tmp/c.txt"));
        Assert.Equal("/tmp/d.txt", ReceiptGate.ExtractClaimedPath("path: /tmp/d.txt"));
    }

    // ---------- changes 自称（真机 2026-09-29 13:2x：worker 没写文件却报 ✅）----------

    [Fact]
    public void Red_changes自称的产物盘上没有_必须判不成立()
    {
        var r = new WorkerResult
        {
            Ok = true,
            Conclusion = "done",
            Changes = ["/tmp/missing-" + Guid.NewGuid().ToString("N") + ".txt"],
        };

        var v = ReceiptGate.JudgeIfClaimed(ReceiptGate.ReceiptText(r));

        Assert.NotNull(v);
        Assert.False(v!.Ok);
    }

    [Fact]
    public void Red_changes用句子报产物也不能溜掉()
    {
        // 真机 2026-09-29 13:3x 原文：`changes: wrote 4 lines to /tmp/titles-rev2.txt` ——
        // 头一个 token 是 “wrote”；只看头一个 token 的写法会静默放过假 ✅。
        var missing = "/tmp/missing-" + Guid.NewGuid().ToString("N") + ".txt";
        var claimed = $"changes: wrote 4 lines to {missing} | open: none";

        Assert.Equal(missing, ReceiptGate.ExtractClaimedPath(claimed));
        Assert.False(ReceiptGate.Judge(claimed).Ok);
    }

    [Fact]
    public void 防假红_changes只说改了几处不算产物自称()
    {
        Assert.Null(ReceiptGate.ExtractClaimedPath("changes: 3 处修改 | open: none"));
        Assert.Null(ReceiptGate.ExtractClaimedPath("changes: none | handoff: /tmp/别的字段.txt"));
    }

    // ---------- v8：回执逐行 key: value（值里可含 `|`）+ covered ----------

    [Fact]
    public void v8_逐行回执_值里的竖线不再歧义()
    {
        var p = WorkerReportParser.Parse("""
            [WORKER]
            conclusion: done
            evidence: a.md:1 | b.md:2
            changes: none
            covered: full
            open: none
            """);

        Assert.True(p.Found);
        Assert.Equal("a.md:1 | b.md:2", Assert.Single(p.Evidence));   // 整行都是值 ⇒ 不拆
        Assert.Equal("full", p.Covered);
        Assert.Empty(p.Open);
    }

    [Fact]
    public void v7_旧单行回执_仍然认()
    {
        var p = WorkerReportParser.Parse("[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: none");
        Assert.True(p.Found);
        Assert.Equal("done", p.Conclusion);
        Assert.Single(p.Evidence);
        Assert.Empty(p.Open);
    }

    [Fact]
    public void 覆盖度_只认自报的partial_缺字段不算()
    {
        Assert.False(WorkerTurnHost.CoverageIncomplete(null));
        Assert.False(WorkerTurnHost.CoverageIncomplete(""));
        Assert.False(WorkerTurnHost.CoverageIncomplete("full"));
        Assert.False(WorkerTurnHost.CoverageIncomplete("1680-1851/1881"));   // 任务要的就是这一段 ⇒ 不算没覆盖
        Assert.True(WorkerTurnHost.CoverageIncomplete("partial (1-34/1881)"));
    }

    // ---------- 原料读数（容量判据与 covered 的参照量） ----------

    [Fact]
    public void 原料读数_剥行区间_数行_估轮数_给容量建议()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wm-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var f = Path.Combine(dir, "big.md");
            File.WriteAllText(f, string.Join("\n", Enumerable.Range(1, 100).Select(i => $"## {i}")) + "\n");

            var m = WindowMaterial.Measure([$"{f} lines 10-20"]);   // 真机常见：master 把行区间写进 window
            var e = Assert.Single(m);
            Assert.True(e.Exists);
            Assert.Equal(f, e.Path);                                 // 行区间被剥掉 ⇒ 还是能定到文件
            Assert.Equal(100, e.Lines);

            Assert.Equal(1, WindowMaterial.EstimatedRounds(m, 2000));
            Assert.Null(WindowMaterial.CapacityAdvice(m, 2000, WindowMaterial.MaxRoundsCap));

            // 单条上限 5 字符 ⇒ 需 ≈119 轮 > 上限 100 ⇒ **说清**（但不拒：拒会变假红）
            Assert.NotNull(WindowMaterial.CapacityAdvice(m, 5, WindowMaterial.MaxRoundsCap));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 修前/修后**有牙**（2026-09-29，§十·60）：中文按**字符**估 —— 分子与分母必须同一把尺子。
    // 这份夹具（1000 个汉字 = 1000 字符 / 3000 字节）在**旧算法**（字节 ÷ 字符上限）下给 2 轮，
    // 在新算法下给 1 轮 ⇒ 这条断言就是旧算法的那颗牙（旧码跑它必红）。
    [Fact]
    public void 量尺同单位_中文按字符估_不再被字节高估()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wm-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var f = Path.Combine(dir, "zh.md");
            File.WriteAllText(f, new string('中', 1000));   // UTF-8: 1 汉字 = 3 B

            var m = WindowMaterial.Measure([f]);
            var e = Assert.Single(m);
            Assert.True(e.Exists);
            Assert.Equal(1000, e.Chars);
            Assert.True(e.Bytes >= 2900, $"字节读数应 ≈3000（实际 {e.Bytes}）");

            Assert.Equal(1000, WindowMaterial.TotalChars(m));
            Assert.Equal(1, WindowMaterial.EstimatedRounds(m, 2000));   // 1000 字符 / 2000 上限 ⇒ 1 轮（旧码 2 轮）
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- v29：应有集合（must:）与 covered 的对账 ----------

    [Fact]
    public void 应有集合_可核的三种形状_其余当自由文本()
    {
        Assert.False(DispatchMust.Parse(null).MachineCheckable);
        Assert.True(DispatchMust.Parse("full").MachineCheckable);
        Assert.True(DispatchMust.Parse("lines 1680-1851").MachineCheckable);
        Assert.Equal(1680, DispatchMust.Parse("1680-1851").Start);
        Assert.False(DispatchMust.Parse("234 个标题").MachineCheckable);   // 自由文本 ⇒ 带过去但不核（不猜）
    }

    [Fact]
    public void 应有集合_应红_covered没盖住就不算完()
    {
        Assert.False(DispatchMust.Parse("lines 1680-1851").SatisfiedBy("1680-1700/1881"));
        Assert.False(DispatchMust.Parse("lines 1680-1851").SatisfiedBy("1700-1881"));      // 起点没盖住
        Assert.False(DispatchMust.Parse("lines 1680-1851").SatisfiedBy(string.Empty));      // 声明了却没报
        Assert.False(DispatchMust.Parse("full").SatisfiedBy("partial (1-34/1881)"));
    }

    [Fact]
    public void 应有集合_防假红_盖住了就通过_自由文本与未声明不核()
    {
        Assert.True(DispatchMust.Parse("lines 1680-1851").SatisfiedBy("1680-1851/1881"));
        Assert.True(DispatchMust.Parse("lines 1680-1851").SatisfiedBy("full"));
        Assert.True(DispatchMust.Parse("full").SatisfiedBy("1881/1881"));                  // 真机 worker 的写法
        Assert.True(DispatchMust.Parse("234 个标题").SatisfiedBy(string.Empty));            // 自由文本 ⇒ 不核
        Assert.True(DispatchMust.None.SatisfiedBy(string.Empty));                           // 未声明 ⇒ 旧行为
    }

    [Fact]
    public void 派发_must段被解析_且不算多出来的段()
    {
        var p = DispatchParser.Parse("[DISPATCH] 抽末尾3条 | window: a.md | must: lines 1680-1851 | mode: sync");
        var d = Assert.Single(p.Dispatches);
        Assert.Equal("lines 1680-1851", d.Must);
        Assert.Empty(p.Malformed);
    }

    [Fact]
    public void Green_changes自称且盘上有该文件_成立()
    {
        var p = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, "x");
        try
        {
            var r = new WorkerResult { Ok = true, Conclusion = "done", Changes = [p] };
            var v = ReceiptGate.JudgeIfClaimed(ReceiptGate.ReceiptText(r));
            Assert.NotNull(v);
            Assert.True(v!.Ok);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void 防假红_changes为none或空不算自称()
    {
        // 最常见的一行是 `changes: none` —— 它**不是**产物自称，不许被当成一个叫 “none” 的路径去盘上找。
        Assert.Null(ReceiptGate.JudgeIfClaimed(ReceiptGate.ReceiptText(
            new WorkerResult { Ok = true, Conclusion = "done", Changes = ["none"] })));
        Assert.Null(ReceiptGate.JudgeIfClaimed(ReceiptGate.ReceiptText(
            new WorkerResult { Ok = true, Conclusion = "done" })));
    }
}

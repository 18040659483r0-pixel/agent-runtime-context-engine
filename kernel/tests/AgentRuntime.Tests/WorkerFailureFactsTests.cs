using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>A 项（失败三字段）的判据夹具：超时/不可达/契约不完整都必须给出病因与可重试标记。</summary>
public class WorkerFailureFactsTests
{
    [Fact]
    public void Timeout_IsProviderTimeout_NotUnreachable()
    {
        // 2026-10-07（#287 翻案）：超时 **≠** 不可达。真机：同一派发 112s 后正常返回 done，
        // 却报「provider 不可达」⇒ 把「慢」报成「不可达」（§十·31：量法错会伪装成结论）。
        var reason = WorkerFailureFacts.Classify("请求超时（90s）：https://api.deepinfra.com/v1/openai/chat/completions");
        Assert.Equal(WorkerFailureReason.ProviderTimeout, reason);
        // 2026-10-09（停即拆 · 阶段 1）：超时**不再可重试** —— 处置是「停 + 拆小」，不是重发同块。
        Assert.False(WorkerFailureFacts.IsRetryable(reason));
        var hint = WorkerFailureFacts.MissingHint("", reason);
        Assert.Contains("慢", hint, System.StringComparison.Ordinal);
        Assert.Contains("不等于不可达", hint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 真不可达_仍归ProviderUnreachable()
    {
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, WorkerFailureFacts.Classify("connection refused"));
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, WorkerFailureFacts.Classify("no such host"));
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, WorkerFailureFacts.Classify("HTTP 401 Unauthorized"));
    }

    [Fact]
    public void ClassifyException_超时与不可达分开_其余不算它不会干()
    {
        Assert.Equal(WorkerFailureReason.ProviderTimeout, WorkerFailureFacts.ClassifyException("ModelClientException：请求超时（180s）"));
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, WorkerFailureFacts.ClassifyException("HttpRequestException：no such host"));
        // 认不出的引擎异常：不许误报成「它不会干」（ContractUnsatisfied）
        Assert.Equal(WorkerFailureReason.ProviderUnreachable, WorkerFailureFacts.ClassifyException("NullReferenceException：x"));
    }

    [Fact]
    public void 超时的修正语_不再说换端点()
    {
        var correction = RetryPolicy.Correction(new WorkerResult
        {
            Ok = false,
            Reason = WorkerFailureReason.ProviderTimeout,
            Missing = "worker 未在超时内返回",
        });
        Assert.Contains("慢", correction, System.StringComparison.Ordinal);
        Assert.DoesNotContain("换端点", correction, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCommand_IsContractUnsatisfied_AndRetryable()
    {
        var reason = WorkerFailureFacts.Classify("Cannot proceed - task description is incomplete; no specific wiring jobs were defined");
        Assert.Equal(WorkerFailureReason.ContractUnsatisfied, reason);
        Assert.True(WorkerFailureFacts.IsRetryable(reason));
    }

    [Fact]
    public void MissingFile_IsPathNotReachable()
    {
        var reason = WorkerFailureFacts.Classify("No such file or directory: whitebox/workspace/worker/z68.txt");
        Assert.Equal(WorkerFailureReason.PathNotReachable, reason);
    }

    [Fact]
    public void Permission_IsNotRetryable()
    {
        var reason = WorkerFailureReason.PermissionDenied;
        Assert.False(WorkerFailureFacts.IsRetryable(reason));
    }

    [Fact]
    public void Describe_CarriesAllThreeFields()
    {
        var line = WorkerFailureFacts.Describe("请求超时（90s）");
        Assert.Contains("reason=", line);
        Assert.Contains("missing=", line);
        Assert.Contains("retryable=false", line);
    }

    [Fact]
    public void 超时_不再重发同块_停即拆()
    {
        // 2026-10-09（主人令 · 停即拆 · 设计 docs/DESIGN-SPLIT-ON-STOP.md 阶段 1）。
        // 实证：provider 400s 超时被判「可重试」⇒ 重发同块 2 次 ⇒ 单条派发 1,200,0xx ms（E110/E137）。
        var reason = WorkerFailureFacts.ClassifyException("ModelClientException：请求超时（400s）");
        Assert.Equal(WorkerFailureReason.ProviderTimeout, reason);
        Assert.False(WorkerFailureFacts.IsRetryable(reason));
        // 异常路径装配：Retryable = IsRetryable(超时) ⇒ MustResend 必须为假（否则又回到「重发同块」）。
        var r = new WorkerResult
        {
            Ok = false,
            Reason = reason,
            Retryable = WorkerFailureFacts.IsRetryable(reason),
            Missing = "worker 未在超时内返回",
        };
        Assert.False(RetryPolicy.MustResend(r));
        // 对照：不可达仍可补发（不许把这条改动扩大化 —— 只有「慢」改成停+拆）。
        Assert.True(WorkerFailureFacts.IsRetryable(WorkerFailureReason.ProviderUnreachable));
        Assert.True(RetryPolicy.MustResend(new WorkerResult
        {
            Ok = false,
            Reason = WorkerFailureReason.ProviderUnreachable,
            Retryable = true,
        }));
    }

    [Fact]
    public void 自报partial_必须退回_带接着读的修正()
    {
        // 2026-10-09（真机大派发）：worker 每轮**只读一次**就交 partial（30/2344），而当时运行时放过了（补发=0）。
        // 协议第 7 条：partial 不退链（被打回、继续做）——运行时现与之对齐。
        var partial = new WorkerResult
        {
            Ok = true,
            Conclusion = "partial",
            Kind = ReceiptKind.Partial,
            Covered = "partial (1-30/2344)",
            Missing = "还没读完",
        };
        Assert.True(RetryPolicy.PartialNotCovered(partial));
        Assert.True(RetryPolicy.MustResend(partial));
        var c = RetryPolicy.Correction(partial);
        Assert.Contains("offset", c, System.StringComparison.Ordinal);
        Assert.Contains("partial", c, System.StringComparison.Ordinal);
        // 对照：真做完的（done、无 missing）不退回。
        Assert.False(RetryPolicy.MustResend(new WorkerResult { Ok = true, Conclusion = "done", Kind = ReceiptKind.Done }));
    }

    [Fact]
    public void EmptyFailure_StillClassified_AndHasMissing()
    {
        var reason = WorkerFailureFacts.Classify(null);
        Assert.NotEqual(WorkerFailureReason.None, reason);
        Assert.False(string.IsNullOrWhiteSpace(WorkerFailureFacts.MissingHint(null, reason)));
    }
}

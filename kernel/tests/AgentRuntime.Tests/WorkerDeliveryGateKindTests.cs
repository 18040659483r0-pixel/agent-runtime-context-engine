using System.Collections.Generic;
using AgentRuntime.Core.Worker;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **交付键只在「声称交付完成」时才核**（2026-10-08 真机回归闸门 · 场景 3 `locate`）。
///
/// <para><b>为什么要有它</b>：真机里 worker 交的是一份**诚实的 partial** ——
/// <c>&lt;kind&gt;partial&lt;/kind&gt;</c> + <c>covered</c> + <c>missing</c> + <c>basis</c>，
/// 正是契约对 partial 要求的字段。而主侧交付闸门**绕过** <see cref="ReceiptKinds.RequiredFields"/>、
/// 一律按「该意图的交付键」核 ⇒ 判它 <c>ContractUnsatisfied</c> ⇒ master 带「收紧任务描述」的**错修正**补发 ×2。
/// LOCATE 与 WRITE 各空转两轮。</para>
///
/// <para><b>唯一来源</b>：交付形状由 **意图 + kind** 共同决定（<see cref="ReceiptKinds.RequiredFields"/>，
/// worker 侧契约渲染用的是同一张表）。这张表说：partial ⇒ covered+missing；blocked ⇒ blocker+tried；
/// **done ⇒ 该意图的交付键 + basis**。</para>
/// </summary>
public sealed class WorkerDeliveryGateKindTests
{
    private static DispatchIntent LocateIntent =>
        new(DispatchIntentKind.Locate, [], "free", "sync", "none");

    private static WorkerResult Receipt(ReceiptKind? kind, params string[] memberNames)
    {
        var members = new Dictionary<string, string>();
        foreach (var m in memberNames)
        {
            members[m] = "x";
        }

        return new WorkerResult { Ok = true, Kind = kind, Members = members };
    }

    [Fact]
    public void 诚实_partial_不许被判缺交付键()
    {
        // 真机那一份：covered + missing + basis，没有 items/count（本来就没交完）
        var r = WorkerDeliveryGate_Apply(Receipt(ReceiptKind.Partial, "covered", "missing", "basis"));

        Assert.True(r.Ok);                                   // 旧行为：Ok=false + ContractUnsatisfied（假红）
        Assert.NotEqual(WorkerFailureReason.ContractUnsatisfied, r.Reason);
    }

    [Fact]
    public void blocked_也不必给交付键()
    {
        var r = WorkerDeliveryGate_Apply(Receipt(ReceiptKind.Blocked, "blocker", "tried"));

        Assert.True(r.Ok);
    }

    [Fact]
    public void 一个交付键都没给_才退()
    {
        var r = WorkerDeliveryGate_Apply(Receipt(ReceiptKind.Done));

        Assert.False(r.Ok);                                  // 「一个都没声明 ⇒ 才退」（用户 2026-10-07 令）
        Assert.Equal(WorkerFailureReason.ContractUnsatisfied, r.Reason);
        Assert.True(r.Retryable);
    }

    [Fact]
    public void 没声明_kind_且一个键都没给_也退()
    {
        var r = WorkerDeliveryGate_Apply(Receipt(null));

        Assert.False(r.Ok);
        Assert.Equal(WorkerFailureReason.ContractUnsatisfied, r.Reason);
    }

    [Fact]
    public void 部分声明_放行_但必须留痕()
    {
        var r = WorkerDeliveryGate_Apply(Receipt(ReceiptKind.Done, "basis"));

        Assert.True(r.Ok);                                    // 部分声明 ⇒ 放行（用户 2026-10-07 令）
        Assert.False(string.IsNullOrWhiteSpace(r.ShapeNote));  // 但**不许静默**
    }

    [Fact]
    public void done_且交付键齐_放行_一个字不改()
    {
        var r = WorkerDeliveryGate_Apply(Receipt(ReceiptKind.Done, "items", "count", "basis"));

        Assert.True(r.Ok);
    }

    [Fact]
    public void 唯一来源表本身_partial_的必需字段就是_covered_与_missing()
    {
        Assert.Equal(["covered=", "missing="], ReceiptKinds.RequiredFields(ReceiptKind.Partial));
        Assert.Equal(
            [.. DispatchIntents.DeliveryKeys(DispatchIntentKind.Locate), "basis="],
            ReceiptKinds.RequiredFields(ReceiptKind.Done, LocateIntent));
    }

    private static WorkerResult WorkerDeliveryGate_Apply(WorkerResult r) =>
        WorkerDispatchModule.ApplyDeliveryGate(r, LocateIntent);
}

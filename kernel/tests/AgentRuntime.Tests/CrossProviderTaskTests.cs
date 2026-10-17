using System.Collections.Generic;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **派发物形状**（2026-10-06 用户令：知识不再随派发物走）。
/// <para>worker 自带 R1（铁则区 + L1+L2，与 master 逐字节同一份），L3/L4 由它按 id 自下探 ⇒
/// 派发物里**不许**再有 <c>KNOWLEDGE:</c> 段，只剩一条 <c>NOTE:</c>（上一次失败的退回修正语）。</para>
/// </summary>
public class CrossProviderTaskTests
{
    [Fact]
    public void SmallTask_DoesNotNeedKnowledge()
    {
        Assert.False(DispatchPolicy.NeedsKnowledge("count lines in app.log"));
    }

    [Fact]
    public void 派发物_不再携带知识段_只有NOTE段()
    {
        var t = new CrossProviderTask { Task = "list files", Window = new[] { "/abs/path" } };
        var p = t.ComposePrompt();
        Assert.Contains("TASK: list files", p);
        Assert.Contains("NOTE:", p);                 // 段形状稳定（没修正语写 (none)）
        Assert.DoesNotContain("KNOWLEDGE:", p);      // 知识通道已撤（闸门）
        Assert.Contains("/abs/path", p);
    }

    [Fact]
    public void 退回修正语_随派发物走()
    {
        var t = new CrossProviderTask
        {
            Task = "B 腿解析",
            TargetProvider = "deepinfra",
            Note = "回执缺该意图的交付键：bytes= lines= ⇒ 唯一正确写法…",
            Window = new[] { "/abs/benchmark" },
            Mode = "sync",
        };

        var p = t.ComposePrompt();
        Assert.Contains("NOTE: 回执缺该意图的交付键", p);
        Assert.Contains("TARGET-PROVIDER: deepinfra", p);
        Assert.Contains("/abs/benchmark", p);
    }

    [Fact]
    public void Offload_IsSourceOfTruth_ForPrecisionReads()
    {
        Assert.True(DispatchPolicy.NeedsPrecision("edit {path,oldText,newText}"));
        Assert.False(DispatchPolicy.ShouldOffload("read src/X.cs to get oldText for edit"));
        Assert.True(DispatchPolicy.ShouldOffload("grep -r 24000 src"));
    }
}

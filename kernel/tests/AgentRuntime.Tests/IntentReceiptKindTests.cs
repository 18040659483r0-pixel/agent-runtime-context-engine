using System.Collections.Generic;
using System.Linq;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **意图 8 类 × 回执 6 类**的判定表与归一（设计稿 <c>docs/DESIGN-INTENT-TYPES.md</c>）。
/// <para>这一组存在的前提：设计的一条硬要求是"**意图之间完全不交叉**" —— 那就要**可测**：
/// 每一条自然语言目的句**唯一命中**一类（命中 0 或 ≥2 = 设计失败）；非法组合必须被点名。</para>
/// <para>纪律：本组只测**机械判据**（词表 + 优先序 + 形状）；语义裁决不在本设计里（§二·原则 6）。</para>
/// </summary>
public sealed class IntentReceiptKindTests
{
    [Fact]
    public void 共用协议段_master_与_worker_两侧逐字同一份()
    {
        // 2026-10-10（用户令「协议区拔干净 worker」）：共用段**只**在 worker 协议区里；master 协议区已拔出
        // （逐字留档见 docs/BACKUP-WORKER-PROTOCOL.md，回插后本条断言再改回去）。
        var shared = MasterWorkerProtocol.Text;
        Assert.DoesNotContain(shared, AgentRuntime.Core.Protocol.ProtocolText.Text, System.StringComparison.Ordinal);
        Assert.Contains(shared, AgentRuntime.Core.Worker.WorkerProtocolText.Text, System.StringComparison.Ordinal);
        // 且它是**渲染**出来的（两张表的每一项都必须在段里出现），不是手写
        foreach (var kind in System.Enum.GetValues<DispatchIntentKind>())
        {
            Assert.Contains(DispatchIntents.Name(kind), shared, System.StringComparison.Ordinal);
            foreach (var k in DispatchIntents.DeliveryKeys(kind))
            {
                Assert.Contains(k, shared, System.StringComparison.Ordinal);
            }
        }

        foreach (var kind in System.Enum.GetValues<ReceiptKind>())
        {
            Assert.Contains(ReceiptKinds.Name(kind), shared, System.StringComparison.Ordinal);
        }

        // 用户 2026-10-07 令：**回执组合**照派发的 modifiers 一样进共用段（收窄意图的方式，两侧同一份）。
        // 每一项都从唯一声明处（ReceiptCombination 的取值域）渲染，不是手写。
        Assert.Contains("Receipt modifiers mirror the dispatch", shared, System.StringComparison.Ordinal);
        Assert.Contains("covered=", shared, System.StringComparison.Ordinal);
        foreach (var v in ReceiptCombination.CoveredValues)
        {
            Assert.Contains(v, shared, System.StringComparison.Ordinal);
        }

        foreach (var v in ReceiptCombination.SideEffectValues)
        {
            Assert.Contains(v, shared, System.StringComparison.Ordinal);
        }
    }
    // ------------------------------------------------------------ 非法组合（必须红）

    [Fact]
    public void 非法组合_必须被点名()
    {
        Assert.NotNull(DispatchIntents.IllegalCombination(
            new DispatchIntent(DispatchIntentKind.Locate, ["/a"], "lines 1-3", "sync", "none")));
        Assert.NotNull(DispatchIntents.IllegalCombination(
            new DispatchIntent(DispatchIntentKind.Verify, ["/a"], "count 3", "sync", "none")));
        Assert.NotNull(DispatchIntents.IllegalCombination(
            new DispatchIntent(DispatchIntentKind.Write, ["/a/b"], "full", "sync", "none")));   // 写类必须 local
        Assert.Null(DispatchIntents.IllegalCombination(
            new DispatchIntent(DispatchIntentKind.Write, ["/a/b"], "full", "sync", "local")));
        Assert.Null(DispatchIntents.IllegalCombination(
            new DispatchIntent(DispatchIntentKind.Read, ["/a"], "full", "sync", "none")));
    }

    // ------------------------------------------------------------ 组合的解析

    [Fact]
    public void 组合_解析_类名与叠加轴()
    {
        var i = DispatchIntents.Parse("WRITE(scope=/abs/dir) criteria=full when=sync side_effects=local");
        Assert.NotNull(i);
        Assert.Equal(DispatchIntentKind.Write, i!.Kind);
        Assert.Equal(["/abs/dir"], i.Scope);
        Assert.Equal("full", i.Criteria);
        Assert.Equal("local", i.SideEffects);
        Assert.Equal("path= bytes= lines= sha256= state=created|already-present", i.DeliveryShape);

        Assert.Equal(DispatchIntentKind.ReadVerbatim, DispatchIntents.Parse("read_verbatim")!.Kind);
        Assert.Null(DispatchIntents.Parse("SCAN"));                     // 清单外 ⇒ 不认（指路由调用点给）
    }

    [Fact]
    public void 每类都有唯一交付形状_且互不相同()
    {
        var shapes = System.Enum.GetValues<DispatchIntentKind>().Select(DispatchIntents.DeliveryShape).ToArray();
        Assert.Equal(shapes.Length, shapes.Distinct().Count());
        Assert.All(shapes, s => Assert.NotEqual("?", s));
    }
    // ------------------------------------------------------------ 回执 6 类

    [Fact]
    public void 回执类别_只认worker自己声明的kind格()
    {
        // 声明了 ⇒ 就是它（不归一、不改写）。
        Assert.Equal(ReceiptKind.Done, WorkerReceiptShape.KindOf("[RESULT] 做完了 kind=done"));
        Assert.Equal(ReceiptKind.Partial, WorkerReceiptShape.KindOf("[RESULT] 只做了一半 kind=partial"));
        Assert.Equal(ReceiptKind.NeedHuman, WorkerReceiptShape.KindOf("[RESULT] 需要你定 kind=need_human"));

        // 没声明 / 声明了清单外的值 ⇒ null = **形状缺项**（**运行时绝不替它判类** · 用户 2026-10-06 口径）。
        Assert.Null(WorkerReceiptShape.KindOf("[RESULT] 做完了"));                 // 旧形状：只有结论词
        Assert.Null(WorkerReceiptShape.KindOf("[RESULT] x kind=很难说"));
        Assert.Null(WorkerReceiptShape.KindOf("[RESULT] x kind=failed"));          // 含混词不是类别
    }

    [Fact]
    public void 运行时不提供任何_把事实归成实质结论_的入口()
    {
        // 闸门要有牙：只删调用点不算 —— **类型面上**也不许再有任何从文本/事实归一出结论的公开入口
        // （旧 `ReceiptKinds.FromLegacy` / `DispatchIntents.FromLegacyFields` 已删 · 2026-10-06）。
        foreach (var type in new[] { typeof(ReceiptKinds), typeof(DispatchIntents) })
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var bad = type.GetMethods(flags)
                .Select(m => m.Name)
                .Distinct()
                .Where(n => n.Contains("Legacy", System.StringComparison.Ordinal)
                         || n.Contains("Infer", System.StringComparison.Ordinal)
                         || n.Contains("Guess", System.StringComparison.Ordinal)
                         || n.Contains("Residue", System.StringComparison.Ordinal)
                         || n.Contains("Classify", System.StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(bad);
        }
    }

    [Fact]
    public void 形状语法提示_必须带_改对后记进踩坑集_的闭环()
    {
        // 用户 2026-10-06：运行时可以做语法检查，但要**提示**远端，并让它改对后**记进踩坑集**
        // ⇒ 同一条语法错不再重复（知识库修，而不是运行时一次次拦）。
        // 2026-10-06 用户令：**两套反馈严格区分**（同一基类两个派生）——
        // ① 语法/格式类 = 协议区的补充 ⇒ 只给规范，**不许提踩坑集**；② 实打实错误 ⇒ 才提醒写踩坑集。
        var syntax = new SyntaxFeedback("回执缺 basis=");
        var pitfall = new PitfallFeedback("路径不存在");
        Assert.Equal("syntax", syntax.Kind);
        Assert.Equal("pitfall", pitfall.Kind);
        Assert.DoesNotContain("pitfalls", syntax.Line, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("踩坑集", syntax.Line, System.StringComparison.Ordinal);
        Assert.Contains("踩坑集", pitfall.Line, System.StringComparison.Ordinal);
        Assert.Contains("规范", syntax.Line, System.StringComparison.Ordinal);

        // 两侧拿到的共用段里必须写死这条闭环（证据 + 记坑），否则远端根本不知道要记。
        Assert.Contains("record the pitfall", MasterWorkerProtocol.Text, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recomputable evidence package", MasterWorkerProtocol.Text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 回执_每类有自己的必需字段_done_不许带未决()
    {
        Assert.Equal(["basis="], ReceiptKinds.RequiredFields(ReceiptKind.Done));
        // v0.5（用户 16:44 口径）：交付形状由意图唯一决定 ⇒ done 的必需集 = 该意图的键 + basis=
        var read = new DispatchIntent(DispatchIntentKind.Read, ["/a"], "full", "sync", "none");
        Assert.Equal(["addr=", "bytes=", "lines=", "sha256=", "basis="], ReceiptKinds.RequiredFields(ReceiptKind.Done, read));
        // 唯一性：DeliveryKeys 的每个键都必须出现在 DeliveryShape 里（两处声明由本夹具钉住一致）
        foreach (var kind in System.Enum.GetValues<DispatchIntentKind>())
        {
            var shape = DispatchIntents.DeliveryShape(kind);
            foreach (var k in DispatchIntents.DeliveryKeys(kind))
            {
                Assert.Contains(k.TrimEnd('='), shape, System.StringComparison.Ordinal);
            }
        }
        Assert.Equal(["covered=", "missing="], ReceiptKinds.RequiredFields(ReceiptKind.Partial));
        Assert.Equal(["blocker=", "tried="], ReceiptKinds.RequiredFields(ReceiptKind.Blocked));
        Assert.Contains("missing=", ReceiptKinds.ForbiddenFields(ReceiptKind.Done));
        Assert.Empty(ReceiptKinds.ForbiddenFields(ReceiptKind.Partial));
        Assert.Equal(ReceiptKind.NeedHuman, ReceiptKinds.Parse("need-human"));   // 归一只认一种写法
        Assert.Null(ReceiptKinds.Parse("finished"));
    }
}

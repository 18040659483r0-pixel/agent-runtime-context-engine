using System;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// 失败形状的事实装配（2026-09-27，主人 A 项）：worker 失败/超时时，回执**必须**带
/// reason / missing / retryable 三字段 —— 本类就是从「失败文本」推出这三件事实的唯一处。
/// 实测背景：`#W1 sync ❌ 90002ms`（provider 90s 超时）此前只写「请求超时」，三字段全缺。
/// </summary>
public static class WorkerFailureFacts
{
    /// <summary>按失败文本判定病因；超时/不可达 ⇒ ProviderUnreachable。</summary>
    public static WorkerFailureReason Classify(string? failureText)
    {
        var t = (failureText ?? string.Empty).ToLowerInvariant();
        if (t.Length == 0) return WorkerFailureReason.ContractUnsatisfied;
        // 认证失效 / 端点不可达（2026-09-27 补）：provider 的 401/403/DNS/拒连 **不是**「它不会干」，
        // 归 ContractUnsatisfied 会让它走「补发」——而且是带着「收紧任务描述」这种错修正去补发。
        // 判据与 WorkerHealth.Classify 同一套形状（两处都认同一批字符串，避免一处认、一处不认）。
        if (t.Contains("401") || t.Contains("403") || t.Contains("unauthorized") || t.Contains("api key")
            || t.Contains("apikey") || t.Contains("认证") || t.Contains("取不到 key") || t.Contains("key 解析")
            || t.Contains("no such host") || t.Contains("connection refused") || t.Contains("nodename nor servname")
            || t.Contains("dns") || t.Contains("unreachable")) return WorkerFailureReason.ProviderUnreachable;
        // 2026-10-07（#287）：**超时 ≠ 不可达**。真机：同一派发在 112s 后正常返回 done，
        // 而运行时报的是「provider 不可达」⇒ 把「慢」报成「不可达」，导致多余的"换端点"修正。
        // ⚠️ 必须放在 `IsTransient` **之前** —— 它也认 `timeout/超时`（健康侧算瞬态，但病因名不该是 Unreachable）。
        if (t.Contains("超时") || t.Contains("timeout") || t.Contains("timed out")) return WorkerFailureReason.ProviderTimeout;
        // —— 瞬态 429（规格 05，2026-09-27）：flex 的预期代价（排队忙时直接 429）⇒ **可重试**。
        //    判据只有一处（WorkerHealth.IsTransient）—— 这里只消费它，不另写一套字符串（否则一处认、一处不认）。
        if (WorkerHealth.IsTransient(t)) return WorkerFailureReason.ProviderUnreachable;
        if (t.Contains("connection")) return WorkerFailureReason.ProviderUnreachable;
        if (t.Contains("permission") || t.Contains("拒绝")) return WorkerFailureReason.PermissionDenied;
        if (t.Contains("不允许") || t.Contains("not allowed")) return WorkerFailureReason.ToolNotAllowed;
        if (t.Contains("不存在") || t.Contains("not found") || t.Contains("no such file")) return WorkerFailureReason.PathNotReachable;
        if (t.Contains("未交") || t.Contains("不完整") || t.Contains("incomplete") || t.Contains("no command")) return WorkerFailureReason.ContractUnsatisfied;
        return WorkerFailureReason.ContractUnsatisfied;
    }

    /// <summary>缺什么（ok=false 时不许空）——一句话，供 ④ 带修正补发时直接用。</summary>
    public static string MissingHint(string? failureText, WorkerFailureReason reason)
    {
        var t = failureText ?? string.Empty;
        if (t.Length > 0 && t.Length <= 60) return t;
        return reason switch
        {
            WorkerFailureReason.ProviderUnreachable => "worker 未在超时内返回（provider 不可达）",
            WorkerFailureReason.ProviderTimeout => "worker 未在超时内返回（**慢**：仍在跑，可重试 —— **不等于不可达**）",
            WorkerFailureReason.PathNotReachable => "产物路径不存在",
            WorkerFailureReason.PermissionDenied => "权限不足",
            WorkerFailureReason.ToolNotAllowed => "工具不被允许",
            WorkerFailureReason.EvidenceUncorroborated => "回执证据与磁盘不符",
            _ => "worker 未交回合格结果",
        };
    }

    /// <summary>**异常路径**的病因（不是回执文本）：超时 ⇒ <see cref="WorkerFailureReason.ProviderTimeout"/>；
    /// 其余引擎异常 ⇒ <see cref="WorkerFailureReason.ProviderUnreachable"/>（**不许**误报成 "它不会干"）。</summary>
    public static WorkerFailureReason ClassifyException(string? text)
    {
        var r = Classify(text);
        return r is WorkerFailureReason.ProviderTimeout or WorkerFailureReason.ProviderUnreachable
            ? r
            : WorkerFailureReason.ProviderUnreachable;
    }

    /// <summary>可否认带修正补发：不可达/契约未结算/证据不符 ⇒ 可（④ 必补发）。
    /// <para><b>2026-10-09（主人令 · 停即拆 · 设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c> 阶段 1）</b>：<c>ProviderTimeout</c> **不再**是「可重试」——
    /// 「慢」的处置是**停 + 拆小**，不是**重发同一块**。真机实证：provider 400s 超时被判可重试 ⇒ 重发同块 2 次 ⇒ 单条派发 **1,200,0xx ms**（`E110/E137`）。
    /// 与闸门路同口径（那条路早已 <c>Retryable=false</c>）。</para></summary>
    public static bool IsRetryable(WorkerFailureReason reason) =>
        reason is not (WorkerFailureReason.PermissionDenied or WorkerFailureReason.ProviderTimeout);

    /// <summary>把三件事实拼成一行（回执正文用；0 缓存代价）。</summary>
    public static string Describe(string? failureText)
    {
        var r = Classify(failureText);
        return $" reason={r} missing={MissingHint(failureText, r)} retryable={IsRetryable(r).ToString().ToLowerInvariant()}";
    }
}

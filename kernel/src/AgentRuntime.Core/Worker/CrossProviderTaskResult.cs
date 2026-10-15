using System;
using System.Collections.Generic;

namespace AgentRuntime.Core.Worker;

/// <summary>worker 回执的失败病因（枚举，六类）。</summary>
public enum WorkerFailureReason
{
    None = 0,
    PathNotReachable,
    PermissionDenied,
    ToolNotAllowed,
    ContractUnsatisfied,
    ProviderUnreachable,
    /// <summary>**超时（慢）**：调用未在时限内返回，但**不等于不可达** —— 人话与修正都必须与它区分开（2026-10-07 #287）。</summary>
    ProviderTimeout,
    InsufficientContext,
    /// <summary>证据无法被 master 独立复算（同命令比对不符）⇒ 按失败计。</summary>
    EvidenceUncorroborated,
}

/// <summary>
/// 跨 provider 任务结果（handoff/30 §5.8 逐字规格的落地，2026-09-27）。
/// ① Ok 是唯一总闸；ok=false 时 Cause/Missing 不许空（fail-closed）；
/// ② 结论必须被 Evidence 支撑（file:line / 命令+退出码 / sha）——无证据按失败计；
/// ③ TaskId + WorkerLogPath（绝对路径）⇒ master 能回查、能审；
/// ④ Retryable + RetryNote ⇒ 直接构成下一次 CrossProviderTask 的 NOTE(自愈接口)；
/// ⑤ Usage 进档位账本；⑥ ContractSha 钉住回执形状（机器视图=本类，[WORKER] 块=人的视图）。
/// </summary>
public sealed record CrossProviderTaskResult
{
    public string TaskId { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public WorkerFailureReason Cause { get; init; } = WorkerFailureReason.None;
    public string Missing { get; init; } = string.Empty;
    public bool Retryable { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public string Conclusion { get; init; } = string.Empty;

    /// <summary>改动过的文件（机器视图；人读块已有同名单 —— 两边同一个声明处，别各写一套）。</summary>
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();

    /// <summary>即时踩坑（worker 报出 ⇒ **必须在 master 侧真的落进坑集**；只出现在回执里不算）。</summary>
    public IReadOnlyList<string> Pitfalls { get; init; } = Array.Empty<string>();

    /// <summary>交接项（与 <see cref="Pitfalls"/> 同层级；master 看完定夺接下来怎么跑）。</summary>
    public IReadOnlyList<string> Handoff { get; init; } = Array.Empty<string>();
    public string WorkerLogPath { get; init; } = string.Empty;
    public string ContractSha { get; init; } = string.Empty;
    public WorkerUsage? Usage { get; init; }

    /// <summary>ok=true ⇒ 必须至少一条证据（无证据的结论按失败计）。</summary>
    public bool IsWellFormed =>
        Ok
            ? Evidence.Count > 0
            : Cause != WorkerFailureReason.None && Missing.Length > 0;

    /// <summary>失败时的**退回修正语**（带进下一次派发的 <c>NOTE:</c> 段 —— 自愈回路接口）。
    /// <para>2026-10-06：这里不再携带「知识项」（知识已由 worker 自持）—— 只带「这一次哪里不合形状」。</para></summary>
    public string RetryNote { get; init; } = string.Empty;

    /// <summary>下一次派发的 NOTE（自愈回路接口）。</summary>
    public string NextNote() => RetryNote;

    public static CrossProviderTaskResult Failure(
        string taskId,
        WorkerFailureReason cause,
        string missing,
        bool retryable = false,
        string? retryNote = null,
        string? workerLogPath = null) => new()
        {
            TaskId = taskId,
            Ok = false,
            Cause = cause,
            Missing = missing ?? string.Empty,
            Retryable = retryable,
            RetryNote = retryNote ?? string.Empty,
            WorkerLogPath = workerLogPath ?? string.Empty,
        };
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// ③ 的入口：**WorkerResult 之后**再读 worker 落点（消除「读得比 WorkerResult 早」的竞态）。
/// 用法：拿到 WorkerResult 后调用 <see cref="ReadAfterResult"/> —— 它先等产物/流落盘（带超时），再读；
/// 超时即返回「未就绪」而不假装「不存在」。
/// </summary>
public static class WorkerReceiptReader
{
    public const string StreamName = "stream.xml";

    /// <summary>等文件出现（轮询），返回是否就绪；超时不抛、由调用方判红。</summary>
    public static bool WaitForFile(string? path, int timeoutMs = 15000, int pollMs = 200)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (File.Exists(path)) return true;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < Math.Max(0, timeoutMs))
        {
            Thread.Sleep(Math.Max(50, pollMs));
            if (File.Exists(path)) return true;
        }
        return File.Exists(path);
    }

    /// <summary>读最后一条 [WORKER] 块正文（逐字，不摘要）；没有则返回 null。</summary>
    public static string? ReadLastWorkerBlock(string? streamPath)
    {
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath)) return null;
        string? hit = null;
        foreach (var text in SessionStreamStore.ReadTexts(streamPath))
        {
            if (text.Contains("[WORKER]", StringComparison.Ordinal)) hit = text;
        }
        return hit;
    }

    /// <summary>③ 主入口：读「最近一次 worker 落点」的流与末条块；先等落盘、超时即标未就绪。</summary>
    public static (string? StreamPath, string? Block, bool Ready) ReadAfterResult(
        string workerHome, string? artifactPath = null, int timeoutMs = 15000)
    {
        string? stream = WorkerLogReader.LatestStreamPath(workerHome);
        bool artOk = artifactPath is null || WaitForFile(artifactPath, timeoutMs);
        bool streamOk = WaitForFile(stream, timeoutMs);
        if (!streamOk) stream = WorkerLogReader.LatestStreamPath(workerHome);
        string? block = ReadLastWorkerBlock(stream);
        bool ready = artOk && stream is not null && block is not null;
        return (stream, block, ready);
    }

    /// <summary>② 的自证：确认 WorkerResult 真带 ok/reason/missing/retryable（反射写入才不是空转）。</summary>
    /// <para>注意：该类型的失败病因属性名是 <c>Reason</c>（不是 <c>Cause</c>；后者在 CrossProviderTaskResult 上）。</para>
    public static IReadOnlyList<string> MissingCoreContractMembers()
    {
        var t = typeof(WorkerResult);
        var want = new[] { "Ok", "Reason", "Missing", "Retryable" };
        return want.Where(n => t.GetProperty(n) is null).ToArray();
    }
}

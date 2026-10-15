using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// master 读 worker 落点 stream.xml 的入口（2026-09-27；按主人裁定改为「按 dispatch.xml 定位真身」）。
/// 落点层级 = workerHome/&lt;provider&gt;/&lt;批次&gt;/，每个批次目录同时含 dispatch.xml / stream.xml / usage.xml。
/// 规则：以最新 dispatch.xml 为**批次锚** ⇒ 取同目录的 stream.xml。
/// 读不到（无锚 / 无同目录 stream.xml）⇒ 返回 null（**判红**，绝不回落到别的文件）。
/// </summary>
public static class WorkerLogReader
{
    /// <summary>批次锚文件名：一次派发必留它；**不用 stream.xml 当锚**（它可能缺）。</summary>
    public const string DispatchAnchor = "dispatch.xml";
    public const string StreamName = "stream.xml";

    /// <summary>最新批次的 stream.xml 绝对路径；无锚或无同名兄弟 ⇒ null（判红）。</summary>
    public static string? LatestStreamPath(string workerHome)
    {
        string? anchor = LatestDispatchPath(workerHome);
        if (anchor is null) return null;
        string dir = Path.GetDirectoryName(anchor) ?? string.Empty;
        if (dir.Length == 0) return null;
        string stream = Path.Combine(dir, StreamName);
        return File.Exists(stream) ? Path.GetFullPath(stream) : null;
    }

    /// <summary>最新 dispatch.xml 的绝对路径（批次锚）；无 ⇒ null。</summary>
    public static string? LatestDispatchPath(string workerHome)
    {
        if (string.IsNullOrWhiteSpace(workerHome) || !Directory.Exists(workerHome)) return null;
        return Directory
            .GetFiles(workerHome, DispatchAnchor, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>读该文件末尾 n 条**正文**（默认 5）：worker 实际干了什么。读不到 ⇒ 空表（判红由调用方定）。
    /// <para>按**记录**取而不是按行取：XML 片段可以含换行（正文里的换行不转义）⇒ 行数 ≠ 记录数。</para>
    /// </summary>
    public static IReadOnlyList<string> Tail(string? streamPath, int n = 5)
    {
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath)) return Array.Empty<string>();
        var texts = SessionStreamStore.ReadTexts(streamPath);
        return texts.Skip(Math.Max(0, texts.Count - Math.Max(1, n))).ToArray();
    }

    /// <summary>workerHome 未知时的兜底：仓库内 WB 自持 worker 根（同样按 dispatch.xml 锚定）。</summary>
    public static string? LatestStreamPathFallback(string repoRoot) =>
        LatestStreamPath(Path.Combine(repoRoot, "whitebox", "workspace", "worker"));
}

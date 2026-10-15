namespace AgentRuntime.Core.Worker;

/// <summary>派发与分流唯一判据来源（2026-09-27）。</summary>
public static class DispatchPolicy
{
    public static bool NeedsPrecision(string action)
    {
        var a = (action ?? string.Empty).ToLowerInvariant();
        foreach (var m in new[] { "edit", "write", "apply_patch" }) if (a.Contains(m)) return true;
        return false;
    }

    public static bool IsBulk(string action)
    {
        var a = (action ?? string.Empty).ToLowerInvariant();
        if (NeedsPrecision(a)) return false;
        foreach (var m in new[] { "grep", "find", "build", "test" }) if (a.Contains(m)) return true;
        return false;
    }

    public static bool ShouldOffload(string action) => IsBulk(action);

    /// <summary>工具名 → 形状。未知工具 = Unknown（不在例外清单 ⇒ 默认派发）。</summary>
    public static WorkShape ShapeOf(string tool)
    {
        var t = (tool ?? string.Empty).Trim().ToLowerInvariant();
        if (t.StartsWith("read")) return WorkShape.BulkRead;
        if (t.StartsWith("list") || t.StartsWith("grep") || t.StartsWith("find")) return WorkShape.BulkSearch;
        if (t.StartsWith("exec")) return WorkShape.BuildOrVerify;
        if (t.StartsWith("edit") || t.StartsWith("write")) return WorkShape.EditOrWrite;
        if (t.StartsWith("closeout")) return WorkShape.CloseoutPipeline;
        return WorkShape.Unknown;
    }

    /// <summary>留在 master（例外清单）：改码 / 收尾 / 判定 / 规划 / 对话 / 琐事。</summary>
    public static bool StaysWithMaster(WorkShape shape) => shape
        is WorkShape.Judge
        or WorkShape.Plan
        or WorkShape.Dialogue
        or WorkShape.Trivial;

    public static bool StaysWithMaster(string tool) => StaysWithMaster(ShapeOf(tool));

    public static bool DispatchByDefault(WorkShape shape) => !StaysWithMaster(shape);

    public static bool DispatchByDefault(string task) => !NeedsPrecision(task);

    public static bool NeedsKnowledge(string task)
    {
        var t = (task ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        foreach (var m in new[] { "count lines", "count the lines", "file size", "列个清单" }) if (t.Contains(m)) return false;
        foreach (var m in new[] { "仓库", "repo", "路径", "为什么", "怎么改", "协议", "handoff", "worker", "benchmark", "解析", "知识", "context", "文档", "源码" }) if (t.Contains(m)) return true;
        return false;
    }
}

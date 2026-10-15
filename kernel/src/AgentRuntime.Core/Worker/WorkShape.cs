namespace AgentRuntime.Core.Worker;

/// <summary>工具/任务动作的形状（2026-09-27）：派发与分流共用的分类。</summary>
public enum WorkShape
{
    Unknown = 0,
    Trivial,
    BulkRead,
    BulkSearch,
    BuildOrVerify,
    EditOrWrite,
    CloseoutPipeline,
    Dialogue,
    Judge,
    Plan,
}

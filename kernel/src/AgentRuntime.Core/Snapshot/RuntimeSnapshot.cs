using System.Text.Json.Serialization;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// **运行时快照（Runtime Recovery Point）** —— V4 的恢复账本。
/// <para>
/// 论文 §4.7「记录位置，而不是依赖重新总结」：本对象**只记位置，不存正文**。
/// 恢复时要的正文（Current Tail）由事件流按 <see cref="StreamCursor"/> **重放**得出，
/// 所以快照可以逐字节 diff、可以在任何一端重建。
/// </para>
/// <para>
/// <b>不可变账本</b>：只有 <c>get/init</c>（连「改」的入口都没有）。三个概念**不混**：
/// <see cref="FrozenSnapshot"/>（稳定前缀）≠ <see cref="CloseoutWatermark"/>（收敛进度）≠ 本类型（恢复点）。
/// </para>
/// <para>
/// <b>扩展点</b>：加新字段就加一个 <c>init</c> 属性并给默认值（旧快照缺字段走默认值仍可读）；
/// 不兼容的结构变更靠 <see cref="SchemaVersion"/> 挡在门外（未知版本**拒绝加载**，不静默降级）。
/// </para>
/// </summary>
public sealed record RuntimeSnapshot
{
    /// <summary>格式版本闸门；未知版本一律拒绝加载（<see cref="SnapshotService.CurrentSchemaVersion"/>）。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>会话身份；裸聊为空（null）。</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>当时用的冻结前缀指纹（= <see cref="FrozenSnapshot.Id"/>，可重算自证）。</summary>
    [JsonPropertyName("frozenSnapshotId")]
    public string FrozenSnapshotId { get; init; } = string.Empty;

    /// <summary>段级账本 → 前缀变了能说出**变了哪几段**（本字段进磁盘，不进 prompt）。</summary>
    [JsonPropertyName("manifest")]
    public FrozenManifest? Manifest { get; init; }

    /// <summary>流读到第几条（E238 里的 238）。</summary>
    [JsonPropertyName("streamCursor")]
    public long StreamCursor { get; init; }

    /// <summary>流文件位置（恢复要用）。</summary>
    [JsonPropertyName("streamPath")]
    public string StreamPath { get; init; } = string.Empty;

    /// <summary>
    /// **语义焦点（V4.1 已启用）**：恢复那一刻的焦点 —— **事件 Tag**（如 <c>E004</c>）。
    /// <para>
    /// 它记的是**事实**：<c>--resume</c> 照读，不回放、不重算、不询问模型（§4.7 Active Focus）。
    /// 旧快照缺该字段 ⇒ 空默认（<b>SchemaVersion 不 +1</b>）。
    /// </para>
    /// </summary>
    [JsonPropertyName("focus")]
    public IReadOnlyList<string> Focus { get; init; } = [];

    /// <summary>
    /// **当前尾部（V4.2 已启用 · R4）**：恢复那一刻的白板正文（逐行）。
    /// <para>
    /// 它记的同样是**事实**：<c>--resume</c> 照读，不回放、不重算、不询问模型。
    /// 旧快照缺该字段 ⇒ 空默认（<b>SchemaVersion 不 +1</b>）；真值源仍是
    /// <c>CurrentTailStore</c>（本字段只是「那一刻的快照」，供审计与对账）。
    /// </para>
    /// </summary>
    [JsonPropertyName("currentTail")]
    public IReadOnlyList<string> CurrentTail { get; init; } = [];

    /// <summary>
    /// **动态草稿（V4.3 已启用 · R5）**：恢复那一刻的草稿正文（逐行）。
    /// <para>
    /// 它记的同样是**事实**：<c>--resume</c> 照读，不回放、不重算、不询问模型。
    /// 旧快照缺该字段 ⇒ 空默认（<b>SchemaVersion 不 +1</b>）；真值源仍是
    /// <c>DraftStore</c>（本字段只是「那一刻的快照」，供审计与对账）。
    /// </para>
    /// </summary>
    [JsonPropertyName("dynamicDraft")]
    public IReadOnlyList<string> DynamicDraft { get; init; } = [];

    /// <summary>**预留位**（Pending Operations，V4 恒空）。</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<string> Pending { get; init; } = [];

    /// <summary>**预留位**（Worker State，V7 恒空）。</summary>
    [JsonPropertyName("workerState")]
    public string WorkerState { get; init; } = string.Empty;

    /// <summary>当时的模型标识 —— Runtime 与 Model 解耦：换模型后用来核对（恢复本身不依赖模型）。</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>账本时刻。**不参与指纹**（指纹只看冻结前缀字节）。</summary>
    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; }
}

namespace AgentRuntime.Core.Security;

/// <summary>
/// **能力（Capability）** —— 授权的最小单位（<c>docs/DESIGN-SECURITY-GATEWAY.md</c> §二）。
/// <para>
/// <b>身份不是授权</b>：说「我是管理员 / 以系统身份运行」都不构成许可；能做的只有这里列出的几种能力。
/// </para>
/// <para>闭集：新增能力 = 在这里加一个值 + 一条分类分支（与工具名、事件标签同一条纪律）。</para>
/// </summary>
public enum Capability
{
    /// <summary>读文件 / 列目录（只读，默认免批）。</summary>
    FsRead,

    /// <summary>写 / 改文件。</summary>
    FsWrite,

    /// <summary>删除（本版本没有删除工具；保留给 <c>exec</c> 里的删除类命令）。</summary>
    FsDelete,

    /// <summary>执行程序（<c>exec</c>）。</summary>
    ProcExec,

    /// <summary>对外网络请求（本版本没有网络工具；<c>exec</c> 里的联网程序算这一面）。</summary>
    NetRequest,
}

/// <summary>能力名的**唯一声明处**（显示 / 账本 / 测试都用它，别处不得再写字符串字面量）。</summary>
public static class Capabilities
{
    /// <summary>能力名（协议与审批面用的小写点名）。</summary>
    public static string Name(Capability capability) => capability switch
    {
        Capability.FsRead => "fs.read",
        Capability.FsWrite => "fs.write",
        Capability.FsDelete => "fs.delete",
        Capability.ProcExec => "proc.exec",
        Capability.NetRequest => "net.request",
        _ => capability.ToString(),
    };

    /// <summary>中文说法（审批面 / 报错话术用）。</summary>
    public static string Describe(Capability capability) => capability switch
    {
        Capability.FsRead => "读文件",
        Capability.FsWrite => "修改文件",
        Capability.FsDelete => "删除文件",
        Capability.ProcExec => "执行程序",
        Capability.NetRequest => "对外网络请求",
        _ => capability.ToString(),
    };

    /// <summary>名字 → 能力（判不出 ⇒ null；调用方按 fail-closed 处理，不猜）。</summary>
    public static Capability? Of(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "fs.read" => Capability.FsRead,
            "fs.write" => Capability.FsWrite,
            "fs.delete" => Capability.FsDelete,
            "proc.exec" => Capability.ProcExec,
            "net.request" => Capability.NetRequest,
            _ => null,
        };
    }

    /// <summary>全部能力名（面板 / 文档用）。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "fs.read", "fs.write", "fs.delete", "proc.exec", "net.request",
    ];
}

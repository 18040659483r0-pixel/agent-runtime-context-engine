using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Closeout;

// ================================================================ 要件声明的**共享契约**
//
// 为什么住在 Core：**收尾工具与运行时读的是同一份声明** —— 两处各写一份 DTO 就是两处声明，
// 迟早分叉（METHODOLOGY §十·58）。这里只有**形状 + 读取**；解释与执行各在各自那一侧。

/// <summary>`tools/closeout/requirements/*.xml` 的形状。</summary>
[DataContract(Name = "Requirement", Namespace = "")]
public sealed class RequirementDecl
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Title { get; set; } = string.Empty;

    [DataMember(Order = 2)] public int Order { get; set; }

    /// <summary>`core`（必达）| `extension`（扩展）。</summary>
    [DataMember(Order = 3)] public string Class { get; set; } = "core";

    /// <summary>`r` | `rw`（平时）。</summary>
    [DataMember(Order = 4)] public string Outside { get; set; } = "r";

    /// <summary>`r` | `rw`（收尾中）。</summary>
    [DataMember(Order = 5)] public string During { get; set; } = "r";

    [DataMember(Order = 6)] public bool Enabled { get; set; } = true;

    [DataMember(Order = 7)] public List<ArtifactDecl>? Artifacts { get; set; } = [];

    [DataMember(Order = 8)] public List<CheckDecl>? Checks { get; set; } = [];
}

[DataContract(Name = "Artifact", Namespace = "")]
public sealed class ArtifactDecl
{
    [DataMember(Order = 0)] public string? Role { get; set; } = string.Empty;

    /// <summary>相对**仓库根**的路径。</summary>
    [DataMember(Order = 1)] public string? Path { get; set; } = string.Empty;

    [DataMember(Order = 2)] public bool Must { get; set; } = true;
}

[DataContract(Name = "Check", Namespace = "")]
public sealed class CheckDecl
{
    [DataMember(Order = 0)] public string? Id { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string? Sev { get; set; } = string.Empty;

    [DataMember(Order = 2)] public bool Enabled { get; set; } = true;

    [DataMember(Order = 3)] public List<ParamDecl>? Params { get; set; } = [];
}

[DataContract(Name = "Param", Namespace = "")]
public sealed class ParamDecl
{
    [DataMember(Order = 0)] public string? K { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string? V { get; set; } = string.Empty;
}

/// <summary>
/// **声明目录 + 读取**（收尾工具与运行时共用同一份）。
/// <para>位置 = 仓库根（本程序集所在目录上溯 5 层，与收尾工具同一把尺子）下的
/// <c>tools/closeout/requirements/</c>。</para>
/// </summary>
public static class CloseoutDeclarations
{
    /// <summary>仓库根（`<…>/bin/<cfg>/net10.0` 上溯 5 层 —— 与收尾工具 `CloseoutCli.Project` 同一口径）。</summary>
    public static string RepoRoot { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>声明目录（**唯一一处**）。</summary>
    public static string Dir => Path.Combine(RepoRoot, "tools", "closeout", "requirements");

    /// <summary>
    /// 读全部要件声明。**坏文件不静默**：解析不了的整件跳过并把原因写进 <paramref name="problems"/>。
    /// <para>⚠️ `DataContractSerializer` **不走构造函数** ⇒ 缺的元素是 <c>null</c>（不是初始化值）⇒ 读取处一律可空。</para>
    /// </summary>
    public static IReadOnlyList<RequirementDecl> Load(out IReadOnlyList<string> problems)
    {
        var bad = new List<string>();
        var decls = new List<RequirementDecl>();

        if (!Directory.Exists(Dir))
        {
            problems = bad;
            return decls;
        }

        foreach (var f in Directory.EnumerateFiles(Dir, "*.xml").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var d = DataContractXml.ReadFile<RequirementDecl>(f);
                if (d is null || string.IsNullOrEmpty(d.Id))
                {
                    bad.Add($"{Path.GetFileName(f)}：读不出 Id ⇒ 整件跳过");
                    continue;
                }

                decls.Add(d);
            }
            catch (Exception ex)
            {
                bad.Add($"{Path.GetFileName(f)}：读不出来（{ex.GetType().Name}）⇒ 整件跳过");
            }
        }

        problems = bad;
        return decls;
    }

    /// <summary>该声明的某个权限档是否为可写（容忍大小写与 `write`）。</summary>
    public static bool Writable(string? level) =>
        (level ?? string.Empty).Trim().ToLowerInvariant() is "rw" or "write";
}

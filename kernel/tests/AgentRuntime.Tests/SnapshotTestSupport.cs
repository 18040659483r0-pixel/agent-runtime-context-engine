namespace AgentRuntime.Tests;

/// <summary>
/// 快照测试的临时工作目录（每个测试一个独立目录，互不干扰；退出时整棵删掉）。
/// </summary>
internal sealed class SnapshotTestWorkspace : IDisposable
{
    public SnapshotTestWorkspace(string label)
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "agentruntime-snapshot-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>目录内的文件路径。</summary>
    public string File(string name) => System.IO.Path.Combine(Root, name);

    /// <summary>写一份带版本头的冻结语料（rules/global.md）。</summary>
    public string FrozenRoot(string rulesBody = "铁则正文", string version = "1")
    {
        var root = File("frozen");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "rules"));
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(root, "rules", "global.md"), $"<!-- frozen: version={version} -->\n{rulesBody}");
        return root;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

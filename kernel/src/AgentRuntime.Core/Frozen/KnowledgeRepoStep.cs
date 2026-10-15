using System.Text;
using AgentRuntime.Core.Knowledge;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **收尾·知识库派生（B4 接入口）** —— 收尾时**就地重建** <c>knowledge/</c> 的 <c>.derived/</c> 并校验往返。
///
/// <para><b>为什么要有它</b>：这一步原来在收尾流水线里是
/// <c>python3 tools/knowledge-repo/knowledge-repo.py --out knowledge --derive-only</c> —— **要远端 AI 点 <c>exec</c> 起进程**
/// （设计稿《内置收尾》§五 的「其余 4 条 python 流水线」之一）。B4 把它搬成 C# 之后，运行时**收尾时自己在进程内做**
/// —— 铁则 **R2 只管「起进程」**；这一步是**文件 IO + 计算**，本来就该运行时自己做。</para>
///
/// <para><b>只重建 / 只报事实</b>：本步**不改 <c>knowledge.md</c>**（活版是收尾的落点，由模型 + <see cref="CorpusWriter"/> 管），
/// 只把**可重建的派生**按活版重算。<c>mismatch &gt; 0</c> ⇒ **如实报**（源技能已改而活版没跟上），
/// **不判红、不阻断**（§十·43：源的质量缺陷用 WARN + 如实标注）。</para>
/// </summary>
public sealed class KnowledgeRepoStep(string workspace) : ICloseoutStep
{
    public string Id => "knowledge-repo";

    public string Title => "知识库派生（knowledge/.derived）";

    /// <summary>输入 = 知识库**活版**；不存在 ⇒ 本步不适用（不猜路径、不凭空造库）。</summary>
    public string LiveFile { get; } = Path.Combine(workspace, "knowledge", "knowledge.md");

    public bool Applies(CloseoutContext context) => File.Exists(LiveFile);

    public IReadOnlyList<string> Run(CloseoutContext context)
    {
        var bytes = File.ReadAllBytes(LiveFile);
        var derived = KnowledgeRepoCli.Derive(KnowledgeRepoCli.ParseMd(bytes), Encoding.UTF8.GetString(bytes));
        KnowledgeRepoCli.WriteDerived(Path.GetDirectoryName(LiveFile)!, derived.Files);

        var c = KnowledgeRepoCli.RtCounts(derived.Roundtrip);
        var line = $"[收尾·知识库] .derived/ 就地重建（{derived.Files.Count} 个文件）· 往返 equal {c.Equal} / "
                 + $"missing {c.Missing} / mismatch {c.Mismatch}（**进程内，不再起 python**）";
        return c.Mismatch > 0
            ? [line, $"[收尾·知识库] ⚠️ {c.Mismatch} 个源技能与活版**字节不等** ⇒ 先跑 `--check` 看是哪几处（**不阻断收尾**）。"]
            : [line];
    }
}

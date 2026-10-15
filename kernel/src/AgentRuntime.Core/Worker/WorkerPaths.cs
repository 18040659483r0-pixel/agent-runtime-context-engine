using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **worker 的落点路径**（唯一声明处）：原先散在 <c>MinimalWorker</c>，但 **Modules 不能引 Hosting**
/// ⇒ 节拍账（L2）读不到它。搬到这里，两边都走同一份（同一件事只许一处声明）。
/// </summary>
public static class WorkerPaths
{
    /// <summary>worker 的家（配置 <c>worker.home</c>；缺省 = 临时目录下的 <c>agentruntime-worker</c>）。</summary>
    public static string Root(WorkerConfiguration cfg) =>
        Path.GetFullPath(cfg.Home ?? Path.Combine(Path.GetTempPath(), "agentruntime-worker"));

    /// <summary>worker 标识（<c>providerName</c>；缺省回落 model 名）—— 按它分家，不按实例分家。</summary>
    public static string Provider(WorkerConfiguration cfg) =>
        Slug(string.IsNullOrWhiteSpace(cfg.ProviderName) ? cfg.Provider.Model : cfg.ProviderName);

    /// <summary>目录名安全化（provider / model 名可能带 <c>/</c>）。</summary>
    public static string Slug(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
}

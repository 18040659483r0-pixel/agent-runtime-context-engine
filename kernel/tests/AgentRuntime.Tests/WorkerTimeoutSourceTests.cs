using System.IO;
using System.Text.Json;
using AgentRuntime.Core.Configuration;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 的超时只有一个家**（2026-10-08 真机回归闸门 · #287）。
///
/// <para><b>为什么要有它</b>：配置里写着 <c>worker.provider.timeoutSeconds: 180</c>，而
/// <c>WorkerProviderConfiguration</c> 里**根本没有这个键** ⇒ 绑定**静默丢弃**它 ⇒ 生效的是
/// <c>worker.timeoutSeconds</c> 的默认 **90s**。真机后果：一次 **111.8s 的正常返回**被判
/// 「worker 未在超时内返回」⇒ 多余补发 + 一条**重复派发**（副作用重跑），master 还得自己认出来是假报。</para>
///
/// <para><b>判据</b>：① 配置里的那个键要**被认**；② <c>worker.timeoutSeconds</c> 与
/// <c>worker.provider.timeoutSeconds</c> **必须是同一个值**（别名，不许两处）；③ 真机配置里
/// 声明的值与运行时读到的一致（**这一条才是堵静默丢弃的那道闸**）。</para>
/// </summary>
public sealed class WorkerTimeoutSourceTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = Path.GetDirectoryName(typeof(WorkerTimeoutSourceTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));
        }
    }

    private static RuntimeConfiguration LoadFrom(string xml)
    {
        var dir = Directory.CreateTempSubdirectory("wb-timeout-").FullName;
        var path = Path.Combine(dir, "config.xml");
        File.WriteAllText(path, xml);
        return RuntimeConfiguration.Load(path);
    }

    [Fact]
    public void 配置里的_provider_超时被认_且与别名同值()
    {
        var cfg = LoadFrom("""
        <RuntimeConfiguration>
          <BaseUrl>https://api.example.com</BaseUrl>
          <Model>m-1</Model>
          <Provider>openai-compatible</Provider>
          <Worker>
            <Enabled>true</Enabled>
            <Provider>
              <BaseUrl>https://api.example.com</BaseUrl>
              <Model>m-1</Model>
              <TimeoutSeconds>180</TimeoutSeconds>
            </Provider>
          </Worker>
        </RuntimeConfiguration>
        """);

        Assert.Equal(180, cfg.Worker.Provider.TimeoutSeconds);   // 旧行为：这格**不存在** ⇒ 读到 90（静默丢弃）
        Assert.Equal(180, cfg.Worker.TimeoutSeconds);            // 别名：一处值
    }

    [Fact]
    public void 别名只有一个值_两个方向都通()
    {
        var cfg = LoadFrom("""<RuntimeConfiguration><BaseUrl>https://a</BaseUrl><Model>m</Model><Provider>openai-compatible</Provider></RuntimeConfiguration>""");

        cfg.Worker.TimeoutSeconds = 123;                          // 注意：RuntimeConfiguration.TimeoutSeconds 是**主** provider 的
        Assert.Equal(123, cfg.Worker.Provider.TimeoutSeconds);

        cfg.Worker.Provider.TimeoutSeconds = 7;
        Assert.Equal(7, cfg.Worker.TimeoutSeconds);
    }

    [Fact]
    public void 真机配置_声明的超时与运行时读到的一致()
    {
        var path = Path.Combine(RepoRoot, "src", "AgentRuntime.Cli", "config.stage1.xml");
        Assert.True(File.Exists(path), $"找不到真机配置：{path}");

        var doc = System.Xml.Linq.XDocument.Load(path);
        var declared = (int?)doc.Root!
            .Elements("Worker").SelectMany(e => e.Elements("Provider"))
            .SelectMany(e => e.Elements("TimeoutSeconds")).FirstOrDefault();
        Assert.True(declared is not null, $"真机配置里没有 Worker.Provider.TimeoutSeconds：{path}");

        var cfg = RuntimeConfiguration.Load(path);

        Assert.Equal(declared!.Value, cfg.Worker.Provider.TimeoutSeconds);   // **静默丢弃**在这里会红
        Assert.Equal(declared.Value, cfg.Worker.TimeoutSeconds);
        Assert.True(cfg.Worker.Provider.TimeoutSeconds > 90,
            $"真机 worker 超时应大于默认 90s（慢端点 100s+ 是常态）—— 现在是 {cfg.Worker.Provider.TimeoutSeconds}s");
    }
}

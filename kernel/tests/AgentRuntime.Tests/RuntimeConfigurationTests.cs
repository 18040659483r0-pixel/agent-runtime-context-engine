using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Tests;

public sealed class RuntimeConfigurationTests
{
    private static string WriteTempConfig(string xml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private const string ValidJson = """
    <RuntimeConfiguration>
      <ApiKeyEnv>AGENTRUNTIME_TEST_KEY</ApiKeyEnv>
      <BaseUrl>https://api.example.com/v1/openai</BaseUrl>
      <Model>m-1</Model>
      <Provider>openai-compatible</Provider>
      <Temperature>0.2</Temperature>
      <TimeoutSeconds>30</TimeoutSeconds>
    </RuntimeConfiguration>
    """;

    [Fact]
    public void 加载合法配置_字段齐全()
    {
        var config = RuntimeConfiguration.Load(WriteTempConfig(ValidJson));

        Assert.Equal("openai-compatible", config.Provider);
        Assert.Equal("https://api.example.com/v1/openai", config.BaseUrl);
        Assert.Equal("m-1", config.Model);
        Assert.Equal("AGENTRUNTIME_TEST_KEY", config.ApiKeyEnv);
        Assert.Equal(30, config.TimeoutSeconds);
        Assert.Equal(0.2, config.Temperature);
    }

    [Fact]
    public void 文件不存在_抛FileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => RuntimeConfiguration.Load("/tmp/definitely-missing-agentruntime.json"));
    }

    [Fact]
    public void 非法XML_抛InvalidData()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(WriteTempConfig("<<<not xml>>>")));
    }

    [Fact]
    public void 缺model_校验失败()
    {
        var path = WriteTempConfig("""<RuntimeConfiguration><BaseUrl>https://x/v1</BaseUrl><Model></Model></RuntimeConfiguration>""");

        Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
    }

    [Fact]
    public void 不支持的provider_校验失败()
    {
        var path = WriteTempConfig("""<RuntimeConfiguration><BaseUrl>https://x/v1</BaseUrl><Model>m</Model><Provider>anthropic</Provider></RuntimeConfiguration>""");

        var ex = Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
        Assert.Contains("openai-compatible", ex.Message);
    }

    [Fact]
    public void 密钥优先取环境变量_不落盘()
    {
        var envName = "AGENTRUNTIME_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, "  sk-from-env  ");
        try
        {
            var path = WriteTempConfig($$"""
            <RuntimeConfiguration>
              <ApiKeyEnv>{{envName}}</ApiKeyEnv>
              <BaseUrl>https://x/v1</BaseUrl>
              <Model>m</Model>
            </RuntimeConfiguration>
            """);

            var config = RuntimeConfiguration.Load(path);

            Assert.Equal("sk-from-env", config.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void 环境变量缺失时_回退apiKeyFile()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "agentruntime-key-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(keyFile, "\n  sk-from-file  \n");
        try
        {
            var envName = "AGENTRUNTIME_TEST_KEY_" + Guid.NewGuid().ToString("N");
            var path = WriteTempConfig($$"""
            <RuntimeConfiguration>
              <ApiKeyEnv>{{envName}}</ApiKeyEnv>
              <ApiKeyFile>{{keyFile}}</ApiKeyFile>
              <BaseUrl>https://x/v1</BaseUrl>
              <Model>m</Model>
            </RuntimeConfiguration>
            """);

            var config = RuntimeConfiguration.Load(path);

            Assert.Equal("sk-from-file", config.ResolveApiKey());
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void 两处都没有_返回null_并给出可读提示()
    {
        var envName = "AGENTRUNTIME_TEST_KEY_" + Guid.NewGuid().ToString("N");
        var path = WriteTempConfig($$"""
        <RuntimeConfiguration>
          <ApiKeyEnv>{{envName}}</ApiKeyEnv>
          <BaseUrl>https://x/v1</BaseUrl>
          <Model>m</Model>
        </RuntimeConfiguration>
        """);

        var config = RuntimeConfiguration.Load(path);

        Assert.Null(config.ResolveApiKey());
        Assert.Contains(envName, config.DescribeMissingApiKey());
    }
}

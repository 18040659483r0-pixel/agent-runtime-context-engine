using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Tests;

public sealed class RuntimeConfigurationTests
{
    private static string WriteTempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidJson = """
    {
      "provider": "openai-compatible",
      "baseUrl": "https://api.example.com/v1/openai",
      "model": "m-1",
      "apiKeyEnv": "AGENTRUNTIME_TEST_KEY",
      "timeoutSeconds": 30,
      "temperature": 0.2
    }
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
    public void 非法JSON_抛InvalidData()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(WriteTempConfig("{ not json")));
    }

    [Fact]
    public void 缺model_校验失败()
    {
        var path = WriteTempConfig("""{ "baseUrl": "https://x/v1", "model": "" }""");

        Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
    }

    [Fact]
    public void 不支持的provider_校验失败()
    {
        var path = WriteTempConfig("""{ "provider": "anthropic", "baseUrl": "https://x/v1", "model": "m" }""");

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
            { "baseUrl": "https://x/v1", "model": "m", "apiKeyEnv": "{{envName}}" }
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
            { "baseUrl": "https://x/v1", "model": "m", "apiKeyEnv": "{{envName}}", "apiKeyFile": "{{keyFile}}" }
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
        { "baseUrl": "https://x/v1", "model": "m", "apiKeyEnv": "{{envName}}" }
        """);

        var config = RuntimeConfiguration.Load(path);

        Assert.Null(config.ResolveApiKey());
        Assert.Contains(envName, config.DescribeMissingApiKey());
    }
}

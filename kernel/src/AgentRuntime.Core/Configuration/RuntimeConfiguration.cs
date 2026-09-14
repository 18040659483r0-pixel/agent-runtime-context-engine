using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Configuration;

/// <summary>
/// <c>config.json</c> 的映射（V0：字段极少，够跑通即可；不做多 Provider 配置系统）。
/// <para>
/// 密钥不写进本文件：只写「去哪个环境变量 / 哪个本地文件取」，见 <see cref="ResolveApiKey"/>。
/// </para>
/// </summary>
public sealed class RuntimeConfiguration
{
    public const string DefaultFileName = "config.json";

    /// <summary>Provider 协议标识；V0 只支持 "openai-compatible"。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openai-compatible";

    /// <summary>接口根地址，须含 API 前缀（如 https://api.deepinfra.com/v1/openai）；不要带 /chat/completions。</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>读取密钥的环境变量名（默认 AGENTRUNTIME_API_KEY）。</summary>
    [JsonPropertyName("apiKeyEnv")]
    public string ApiKeyEnv { get; set; } = "AGENTRUNTIME_API_KEY";

    /// <summary>可选：从本地文件读密钥（首行；支持 ~ 展开）。环境变量优先。</summary>
    [JsonPropertyName("apiKeyFile")]
    public string? ApiKeyFile { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>采样温度；null = 不发送。</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// 启用的模块名，顺序 = prompt 中的贡献顺序。**空数组 = 裸聊（等价 V0）**。
    /// 目前支持："system-rules"（顶端铁则/人格）、"session"（多轮会话）。
    /// </summary>
    [JsonPropertyName("modules")]
    public List<string> Modules { get; set; } = [];

    /// <summary>"system-rules" 模块注入的 system 文本；留空则该模块不注入。</summary>
    [JsonPropertyName("systemRules")]
    public string? SystemRules { get; set; }

    /// <summary>"session" 模块保留的最大轮数（0 = 不限）。</summary>
    [JsonPropertyName("sessionMaxTurns")]
    public int SessionMaxTurns { get; set; }

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RuntimeConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到配置文件：{path}", path);
        }

        var json = File.ReadAllText(path);
        RuntimeConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<RuntimeConfiguration>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件不是合法 JSON：{path}（{ex.Message}）", ex);
        }

        if (config is null)
        {
            throw new InvalidDataException($"配置文件内容为空：{path}");
        }

        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (!string.Equals(Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"V0 只支持 provider=\"openai-compatible\"，当前为 \"{Provider}\"。");
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidDataException("config.json 缺少 baseUrl。");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidDataException("config.json 缺少 model。");
        }

        if (TimeoutSeconds <= 0)
        {
            throw new InvalidDataException("timeoutSeconds 必须为正整数。");
        }

        if (SessionMaxTurns < 0)
        {
            throw new InvalidDataException("sessionMaxTurns 不能为负数。");
        }

        var unknown = Modules.FirstOrDefault(m => !KnownModules.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            throw new InvalidDataException($"未知模块：\"{unknown}\"（可用：{string.Join("、", KnownModules)}）。");
        }
    }

    /// <summary>当前版本支持的模块名（新增模块时在这里登记）。</summary>
    public static readonly string[] KnownModules = ["system-rules", "session"];

    /// <summary>是否裸聊（一个模块都不挂）。</summary>
    public bool IsBare => Modules.Count == 0;

    /// <summary>
    /// 取密钥：环境变量（<see cref="ApiKeyEnv"/>）优先，其次 <see cref="ApiKeyFile"/>。
    /// 找不到返回 null，由调用方给出人类可读的提示（绝不在日志/异常里回显密钥本身）。
    /// </summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyFile))
        {
            var path = ExpandHome(ApiKeyFile);
            if (File.Exists(path))
            {
                var line = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>密钥缺失时给用户的指引（只说去哪取，不涉及密钥内容）。</summary>
    public string DescribeMissingApiKey() =>
        $"未找到 API Key：请设置环境变量 {ApiKeyEnv}，或在 config.json 里指定 apiKeyFile 指向本地密钥文件" +
        "（该文件不要提交进 SVN）。";

    public static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}

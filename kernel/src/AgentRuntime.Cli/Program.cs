using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Modules;
using AgentRuntime.Providers;

namespace AgentRuntime.Cli;

/// <summary>
/// 组合根（Console/CLI，无 UI、无 Web、无 DI 容器）：
/// <code>
/// Program → AgentRuntimeEngine → [可插拔模块…] → IModelClient → OpenAICompatibleClient → HttpClient
/// </code>
/// 模块由 config.json 的 <c>modules</c> 决定；<c>--bare</c> 一个都不挂（裸聊）。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitProvider = 3;

    private static async Task<int> Main(string[] args)
    {
        string? configPath = null;
        var verbose = false;
        var chat = false;
        string? modulesOverride = null;
        var message = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--modules" when i + 1 < args.Length:
                    modulesOverride = args[++i];
                    break;
                case "--bare":
                    modulesOverride = string.Empty;
                    break;
                case "--chat":
                    chat = true;
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return ExitOk;
                default:
                    message.Add(args[i]);
                    break;
            }
        }

        try
        {
            configPath ??= LocateDefaultConfig();
            var config = RuntimeConfiguration.Load(configPath);

            var apiKey = config.ResolveApiKey();
            if (apiKey is null)
            {
                Console.Error.WriteLine(config.DescribeMissingApiKey());
                return ExitUsage;
            }

            var modules = BuildModules(config, modulesOverride);

            using var http = new HttpClient();
            var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
            {
                BaseUrl = config.BaseUrl,
                ApiKey = apiKey,
                TimeoutSeconds = config.TimeoutSeconds,
            });

            var engine = new AgentRuntimeEngine(client, new RuntimeOptions
            {
                Model = config.Model,
                Temperature = config.Temperature,
            }, modules);

            if (verbose)
            {
                var label = engine.IsBare ? "裸聊（无模块）" : string.Join(", ", engine.ModuleNames);
                Console.Error.WriteLine($"[modules] {label}");
            }

            return chat
                ? await RunChatAsync(engine, config, client, verbose)
                : await RunOnceAsync(engine, config, client, verbose, message);
        }
        catch (ModelClientException ex)
        {
            Console.Error.WriteLine($"[provider] {ex.Message}");
            if (!string.IsNullOrEmpty(ex.ResponseBody))
            {
                Console.Error.WriteLine($"[provider:body] {ex.ResponseBody}");
            }

            return ExitProvider;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return ExitUsage;
        }
    }

    private static IReadOnlyList<IRuntimeModule> BuildModules(RuntimeConfiguration config, string? modulesOverride)
    {
        if (modulesOverride is null)
        {
            return ModuleRegistry.Create(config);
        }

        // --modules "a,b" 覆盖配置；--bare / --modules "" = 裸聊
        var names = modulesOverride
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var effective = new RuntimeConfiguration
        {
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            Modules = names,
            SystemRules = config.SystemRules,
            SessionMaxTurns = config.SessionMaxTurns,
        };
        effective.Validate();

        return ModuleRegistry.Create(effective);
    }

    private static async Task<int> RunOnceAsync(
        AgentRuntimeEngine engine,
        RuntimeConfiguration config,
        OpenAICompatibleClient client,
        bool verbose,
        List<string> message)
    {
        var text = message.Count > 0 ? string.Join(' ', message) : ReadMessageFromStdin();
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("没有输入内容。用法：AgentRuntime.Cli [选项] [\"你的问题\"]");
            return ExitUsage;
        }

        var result = await engine.ChatAsync(text);

        // 一对一：响应体原样出 stdout，诊断信息出 stderr（保持可管道）。
        Console.Out.WriteLine(result.Response);

        if (verbose)
        {
            WriteDiagnostics(config, client, result, engine, turn: null);
        }

        return ExitOk;
    }

    private static async Task<int> RunChatAsync(
        AgentRuntimeEngine engine,
        RuntimeConfiguration config,
        OpenAICompatibleClient client,
        bool verbose)
    {
        if (verbose)
        {
            Console.Error.WriteLine($"[chat] 多轮模式：每行一句，Ctrl-D 或 exit 结束。会话={engine.SessionId ?? "ephemeral"}");
        }

        while (true)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Error.Write("> ");
            }

            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is "exit" or "quit")
            {
                break;
            }

            try
            {
                var result = await engine.ChatAsync(line);
                Console.Out.WriteLine(result.Response);
                Console.Out.Flush();

                if (verbose)
                {
                    WriteDiagnostics(config, client, result, engine, turn: engine.Turn);
                }
            }
            catch (ModelClientException ex)
            {
                Console.Error.WriteLine($"[provider] {ex.Message}");
            }
        }

        return ExitOk;
    }

    private static string ReadMessageFromStdin()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadToEnd().Trim();
        }

        Console.Error.Write("> ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    private static string LocateDefaultConfig()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, RuntimeConfiguration.DefaultFileName),
            Path.Combine(AppContext.BaseDirectory, RuntimeConfiguration.DefaultFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"找不到 {RuntimeConfiguration.DefaultFileName}（已查找：{string.Join("、", candidates)}）；可用 --config 指定。");
    }

    private static void WriteDiagnostics(
        RuntimeConfiguration config,
        OpenAICompatibleClient client,
        RuntimeResult result,
        AgentRuntimeEngine engine,
        int? turn)
    {
        var prefix = turn is null ? "" : $"[turn {turn}] ";
        Console.Error.WriteLine("--- runtime diagnostics ---");
        Console.Error.WriteLine($"{prefix}endpoint        : {client.Endpoint}");
        Console.Error.WriteLine($"{prefix}model           : {config.Model}");
        Console.Error.WriteLine($"{prefix}modules         : {(engine.IsBare ? "(none · 裸聊)" : string.Join(", ", engine.ModuleNames))}");
        Console.Error.WriteLine($"{prefix}messages.sent   : {result.MessageCount}");
        Console.Error.WriteLine($"{prefix}response.id     : {result.Raw.Id}");
        Console.Error.WriteLine($"{prefix}finish_reason   : {(result.Raw.Choices.Count > 0 ? result.Raw.Choices[0].FinishReason : "(none)")}");

        if (result.Usage is { } usage)
        {
            Console.Error.WriteLine($"{prefix}tokens.prompt   : {usage.PromptTokens}");
            Console.Error.WriteLine($"{prefix}tokens.completion: {usage.CompletionTokens}");
            Console.Error.WriteLine($"{prefix}tokens.total    : {usage.TotalTokens}");
            Console.Error.WriteLine($"{prefix}tokens.cached   : {usage.CachedTokens}");
            Console.Error.WriteLine($"{prefix}tokens.uncached : {usage.UncachedTokens}");
        }

        Console.Error.WriteLine($"{prefix}latency.total_ms: {result.Timing.TotalMs:F1}");
        Console.Error.WriteLine($"{prefix}latency.provider_ms: {result.Timing.ProviderCallMs:F1}");
        Console.Error.WriteLine($"{prefix}latency.overhead_ms: {result.Timing.RuntimeOverheadMs:F1}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AgentRuntime V1 —— 裸聊 / 多轮，模块热插拔");
        Console.WriteLine();
        Console.WriteLine("用法：AgentRuntime.Cli [选项] [\"你的问题\"]");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --config <path>    配置文件（默认在当前目录 / 程序目录找 config.json）");
        Console.WriteLine("  --bare             裸聊：不挂任何模块（等价 V0）");
        Console.WriteLine("  --modules <a,b>    覆盖配置里的模块列表（空串 = 裸聊）");
        Console.WriteLine("  --chat             多轮模式：每行一句，Ctrl-D 或 exit 结束");
        Console.WriteLine("  --verbose, -v      打印 token / 缓存 / 耗时 / 模块诊断（stderr）");
        Console.WriteLine("  --help, -h         显示本帮助");
        Console.WriteLine();
        Console.WriteLine($"可用模块：{string.Join("、", RuntimeConfiguration.KnownModules)}");
        Console.WriteLine("密钥来源：config.json 的 apiKeyEnv 指向的环境变量，或 apiKeyFile 指向的本地文件。");
    }
}

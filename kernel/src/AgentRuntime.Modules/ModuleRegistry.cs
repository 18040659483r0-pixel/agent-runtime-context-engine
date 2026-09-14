using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Modules;

/// <summary>
/// 模块注册表：把配置里的模块名变成模块实例。
/// <para>
/// **新增模块只改这里**（+ 在 <see cref="RuntimeConfiguration.KnownModules"/> 登记名字），
/// 引擎与 Cli 都不动 —— 这就是热插拔的接线点。
/// </para>
/// </summary>
public static class ModuleRegistry
{
    public static IReadOnlyList<IRuntimeModule> Create(RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var modules = new List<IRuntimeModule>(config.Modules.Count);

        foreach (var name in config.Modules)
        {
            switch (name.ToLowerInvariant())
            {
                case SystemRulesModule.ModuleName:
                    // 铁则文本留空 = 该模块不注入任何内容（比抛错实用：留空即可关掉效果）
                    if (!string.IsNullOrWhiteSpace(config.SystemRules))
                    {
                        modules.Add(new SystemRulesModule(config.SystemRules));
                    }

                    break;

                case SessionModule.ModuleName:
                    modules.Add(new SessionModule(config.SessionMaxTurns));
                    break;

                default:
                    throw new InvalidDataException($"未知模块：\"{name}\"。");
            }
        }

        return modules;
    }
}

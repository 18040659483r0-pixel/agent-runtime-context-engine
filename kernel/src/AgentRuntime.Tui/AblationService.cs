using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Hosting;
using AgentRuntime.Modules;

namespace AgentRuntime.Tui;

/// <summary>
/// **会话级消融开关**（P6）—— 临时摘掉某个模块，**只影响本会话**。
/// <para>
/// 两条纪律：
/// </para>
/// <list type="number">
/// <item><b>不写配置文件</b>（T4）：改动只活在内存里的模块装配上；退出即恢复。</item>
/// <item><b>协议区不可摘</b>（T6 / P3）：<c>protocol</c> 一律报错 —— 闸门在 TUI 面同样有牙，
/// 而且不是「这里也写一句判断」：重建后仍走内核的 <see cref="ModuleRegistry.EnsureProtocolPresent"/> 复验。</item>
/// </list>
/// <para>
/// 副作用（必须说出来，不能藏）：换装配 = 换一套模块实例 ⇒ 内存态状态（<c>session</c> 的历史）重建、
/// 轮次归零；落盘状态（事件流 / 快照 / 白板 / 草稿 / 焦点缓存）在原地不受影响。
/// </para>
/// </summary>
public sealed class AblationService
{
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本会话被摘掉的模块名（顺序不代表语义）。</summary>
    public IReadOnlyCollection<string> Disabled => _disabled;

    /// <summary>可摘模块 = 已知模块 − 协议区（协议区不是「可选项」，列出来只为让人看懂为什么被拒）。</summary>
    public static IReadOnlyList<string> AblatableModules =>
        RuntimeConfiguration.KnownModules
            .Where(m => !IsProtocol(m))
            .ToArray();

    /// <summary>是否是协议区（不可摘的那个）。</summary>
    public static bool IsProtocol(string name) =>
        string.Equals(name, ProtocolModule.ModuleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// **P3 闸门（TUI 面）**：协议区必报错；未知模块必报错（列出可用清单，不静默当没这个模块）。
    /// <para>刻意抛异常（而不是返回一句错误文案）：把「拒绝」变成调用方绕不过去的类型事实。</para>
    /// </summary>
    public static void EnsureAblatable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException($"/ablate 需要模块名。可摘：{string.Join("、", AblatableModules)}；protocol 不可摘。");
        }

        if (IsProtocol(name))
        {
            throw new InvalidDataException(
                "协议区（R1-P）不可摘：它是内核契约（架构固有 · 收尾不可改 · 用户不可摘）—— P3 闸门在 TUI 面同样成立。");
        }

        if (!RuntimeConfiguration.KnownModules.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"未知模块：\"{name}\"。可用：{string.Join("、", RuntimeConfiguration.KnownModules)}（其中 {ProtocolModule.ModuleName} 不可摘）。");
        }
    }

    /// <summary>当前是否被摘掉。</summary>
    public bool IsDisabled(string name) => _disabled.Contains(name);

    /// <summary>
    /// 开 / 关（<paramref name="on"/> 为 null = **默认摘掉** —— 面板名就叫 ablate，不带参数时取“消融”而非“取反”），
    /// 并按新装配重建模块集。
    /// <para>返回给人看的说明行（含「轮次归零」这种副作用，不静默）。</para>
    /// </summary>
    public IReadOnlyList<string> Apply(RuntimeHost host, string name, bool? on)
    {
        ArgumentNullException.ThrowIfNull(host);
        EnsureAblatable(name);

        if (host.Config.Modules.Count == 0)
        {
            throw new InvalidDataException("当前配置的 modules 为空（裸聊 / 真空）：没有可摘的业务模块。");
        }

        // 不带参数 = 摘掉（ablate 的默认语义）；显式 on / off 才看参数。
        var wanted = on ?? false;
        var before = _disabled.Contains(name);
        if (wanted)
        {
            _disabled.Remove(name);
        }
        else
        {
            _disabled.Add(name);
        }

        // 只摘「配置里本来有的」模块：不在配置里的模块本来就没挂，谈「摘」没有意义。
        var effective = host.Config.Modules
            .Where(m => !_disabled.Contains(m))
            .ToList();

        var modules = RuntimeHost.BuildModules(host.Config, string.Join(",", effective), vacuum: false);

        // P3 闸门复验：不是「我觉得协议还在」，而是内核的守卫说它还在。
        ModuleRegistry.EnsureProtocolPresent(modules);

        host.ReplaceModules(modules);

        var verb = _disabled.Contains(name)
            ? "已摘除"
            : before ? "已挂回" : "本来就在挂（未变更）";

        return
        [
            $"[消融] {name} → {verb}（本会话生效，**不写配置文件**）",
            $"[消融] 当前模块：{(modules.Count == 0 ? "（无）" : string.Join(", ", modules.Select(m => m.Name)))}",
            $"[消融] 本会话已摘：{(_disabled.Count == 0 ? "（无）" : string.Join("、", _disabled))}",
            "[消融] 副作用：换装配 ⇒ 内存态会话历史重建、轮次归零；落盘状态（流 / 快照 / 白板 / 草稿）原地不动。",
        ];
    }
}

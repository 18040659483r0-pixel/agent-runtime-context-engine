using System.Text.RegularExpressions;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;

namespace AgentRuntime.Cli;

/// <summary>
/// **`worker` 子命令**（用户 2026-10-10 令）—— 把 **worker 模块从运行时拔掉 / 装回**的**正规入口**。
///
/// <list type="table">
/// <item><c>worker status [--config &lt;path&gt;]</c>：报事实（配置里开不开 · 生效模块清单里有没有 <c>worker-dispatch</c>）</item>
/// <item><c>worker off [--config &lt;path&gt;]</c>：**拔掉** —— 把配置里 <c>worker.enabled</c> 置 <c>false</c></item>
/// <item><c>worker on [--config &lt;path&gt;]</c>：**装回** —— 置 <c>true</c></item>
/// </list>
///
/// <para><b>为什么不新开一个开关</b>：运行时**已经有**这条唯一开关 —— <see cref="WorkerConfiguration.Enabled"/></para>
/// <para>（<c>ModuleRegistry.EffectiveModules</c> 据它**自动剔掉** <c>worker-dispatch</c>；<c>RuntimeHost.TryBuildWorkerRunner</c> 据它不建 runner）。
/// 本命令只是给这条开关一个**可操作、可见、可回退**的 CLI 面，不引入第二处真相（§十·58）。</para>
///
/// <para><b>定向改、不是重写</b>：只替换 <c>"worker"</c> 块里那个 <c>"enabled"</c> 的**值**，
/// 其余字节（注释 / 排版 / 键序）一律不动 —— 配置是人写的手稿，不是本命令的产物。</para>
///
/// <para>拔掉之后的**行为**（协议侧由运行时按事实给，不在这里编）：
/// 无 <c>worker-dispatch</c> ⇒ <c>WorkerAvailability.AbsentHint</c> 进流（「第 13 条不适用，原料活自己干」），
/// 仍发 <c>[DISPATCH]</c> 则 <c>DegradeText</c> **把活交回 master**并计一笔 —— 三态（配置级 / 运行期 / 会话内）同一判定。</para>
/// </summary>
public static class WorkerCli
{
    public static int RunAsync(string[] args)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "status";
        var cfg = Arg(args, "--config") ?? DefaultConfig();

        if (!File.Exists(cfg))
        {
            Console.Error.WriteLine($"[worker] 读不到配置 {cfg}（用 `--config <path>` 指一个）");
            return 2;
        }

        switch (sub.ToLowerInvariant())
        {
            case "status":
                return Status(cfg);
            case "off":
                return Set(cfg, enabled: false);
            case "on":
                return Set(cfg, enabled: true);
            default:
                Console.Error.WriteLine("用法：worker status | worker off | worker on   [--config <path>]");
                return 2;
        }
    }

    private static int Status(string cfg)
    {
        RuntimeConfiguration config;
        try
        {
            config = RuntimeConfiguration.Load(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] 配置读不了：{ex.Message}");
            return 2;
        }

        var effective = ModuleRegistry.EffectiveModules(config);
        var mounted = effective.Any(m => string.Equals(m, WorkerDispatchModule.ModuleName, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"[worker] 配置 = {cfg}");
        Console.WriteLine($"[worker] worker.enabled = {(config.Worker.Enabled ? "true" : "false")}");
        Console.WriteLine($"[worker] 生效模块清单{(mounted ? "含" : "**不含**")} \"{WorkerDispatchModule.ModuleName}\"");
        Console.WriteLine($"[worker] 生效：{string.Join(", ", effective)}");
        Console.WriteLine(mounted
            ? "[worker] ⇒ 派发可用（协议第 13 条生效）。"
            : "[worker] ⇒ **结构性无 worker**：运行时会让 master「原料活自己干」（AbsentHint），发了 [DISPATCH] 也只是被交回。");
        return 0;
    }

    /// <summary>把配置里 <c>"worker"</c> 块的 <c>"enabled"</c> 值定向替换（其余字节不动）。</summary>
    private static int Set(string cfg, bool enabled)
    {
        var text = File.ReadAllText(cfg);
        // 锚点：先找 "worker" 键 + 它的对象开头，再找**其后第一个** "enabled"（worker 段的第一个键）。
        var block = Regex.Match(text, @"""worker""\s*:\s*\{", RegexOptions.CultureInvariant);
        if (!block.Success)
        {
            Console.Error.WriteLine($"[worker] 配置里找不到 \"worker\" 块：{cfg}");
            return 2;
        }

        var tail = text[block.Index..];
        var m = Regex.Match(tail, @"""enabled""\s*:\s*(true|false)", RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            Console.Error.WriteLine("[worker] \"worker\" 块里找不到 \"enabled\" 键（不猜：请手工确认配置形状）。");
            return 2;
        }

        var oldValue = m.Groups[1].Value;
        var want = enabled ? "true" : "false";
        if (oldValue == want)
        {
            Console.WriteLine($"[worker] 已经是 {want}（未变更）—— {cfg}");
            return Status(cfg);
        }

        // 只替换那一个值（保长度之外的字节原样：用索引拼接，不动正则替换的其余分组）。
        var valueStart = block.Index + m.Index + m.Value.LastIndexOf(oldValue, StringComparison.Ordinal);
        var updated = string.Concat(
            text.AsSpan(0, valueStart), want, text.AsSpan(valueStart + oldValue.Length));

        File.WriteAllText(cfg, updated);
        Console.WriteLine($"[worker] worker.enabled {oldValue} → {want}（只改了这一个值，其余字节不动）");
        return Status(cfg);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string DefaultConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        var guess = Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "src", "AgentRuntime.Cli", "config.stage1.xml");
        return File.Exists(guess) ? guess : Path.Combine(Directory.GetCurrentDirectory(), "config.stage1.xml");
    }
}

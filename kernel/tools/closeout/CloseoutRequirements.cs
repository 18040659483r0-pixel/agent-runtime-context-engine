using System.Runtime.Serialization;
using System.Text;
using AgentRuntime.Core.Closeout;
using AgentRuntime.Core.Persistence;

namespace CloseoutTool;

// ================================================================ 收尾的「要件模块」
//
// 用户 2026-10-16 13:20/13:32 定的口径：
//   · 每一个**要件**都是一个**模块**；加/删/改模块 = 改**声明**（`tools/closeout/requirements/*.xml`），不改代码。
//   · **权限两档**：`Access(平时, 收尾中)`。
//   · **模块类别**：Core（必达，红 ⇒ 阻断）/ Extension（扩展，红/不可达 ⇒ 只上账）。
//   · **锁定**：`memory`（兜底）与 `closeout`（承载声明自证）**必须存在且为 Core** —— 删声明也不许消失。
//
// B 批（本批）：**加声明加载器 + 生效指纹**。判据本体仍在代码里（按 id 绑定）；阈值/路径数据化留 C 批。

/// <summary>读 / 写。</summary>
internal enum Level
{
    Read,
    Write,
}

/// <summary>**两档权限**：`Outside` = 平时（非收尾），`During` = 收尾中。</summary>
internal readonly record struct Access(Level Outside, Level During)
{
    public static readonly Access ReadOnly = new(Level.Read, Level.Read);

    public static readonly Access CloseoutWritable = new(Level.Read, Level.Write);

    public static readonly Access AlwaysWritable = new(Level.Write, Level.Write);

    public override string ToString() => $"{Word(Outside)}→{Word(During)}";

    private static string Word(Level l) => l == Level.Read ? "r" : "rw";
}

/// <summary>写入发生的**时刻**（与 <see cref="Access"/> 的两档一一对应）。</summary>
internal enum When
{
    /// <summary>平时（非收尾）。</summary>
    Outside,

    /// <summary>收尾中。</summary>
    During,
}

/// <summary>模块类别 —— 唯一"有牙"的那一维。</summary>
internal enum ModuleClass
{
    /// <summary>**必达**：它红 ⇒ 阻断收尾。</summary>
    Core,

    /// <summary>**扩展**：它红 / 不可达 ⇒ 只上账，不阻断。</summary>
    Extension,
}

/// <summary>模块声明的一个落点。</summary>
/// <param name="Role">角色（src / derived / doc …，人读）。</param>
/// <param name="Path">相对 <c>Project</c> 的路径。</param>
/// <param name="Must">是否**必须存在**（true ⇒ 声明自证会红）。</param>
internal sealed record Artifact(string Role, string Path, bool Must);

/// <summary>一个**要件模块**。</summary>
internal interface ICloseoutRequirement
{
    string Id { get; }

    string Title { get; }

    int Order { get; }

    ModuleClass Class { get; }

    Access Access { get; }

    IReadOnlyList<Artifact> Artifacts { get; }

    /// <summary>它自己的判据（**对象**，不是名字字符串）。</summary>
    IReadOnlyList<CloseoutCli.Res> Checks();
}

/// <summary>由声明构造的模块。</summary>
internal sealed class DeclaredRequirement(RequirementDecl decl, ModuleClass cls, Access access) : ICloseoutRequirement
{
    public string Id => decl.Id;

    public string Title => !string.IsNullOrEmpty(decl.Title) ? decl.Title : decl.Id;

    public int Order => decl.Order;

    public ModuleClass Class => cls;

    public Access Access => access;

    public IReadOnlyList<Artifact> Artifacts => [.. (decl.Artifacts ?? []).Select(a => new Artifact(a.Role ?? "", a.Path ?? "", a.Must))];

    public IReadOnlyList<CloseoutCli.Res> Checks() => [.. BuiltinChecks.Of(Id).SelectMany(f => f())];
}

/// <summary>
/// **判据本体**（B 批仍在代码里，按 <c>Id</c> 绑定）。<see cref="Known"/> = 认识的 id 集合：
/// 声明了一个不认识的 id ⇒ **响亮报错**（不许静默产出一个空模块）。
/// </summary>
internal static class BuiltinChecks
{
    public static readonly string[] Known =
        ["closeout", "memory", "rules", "pitfalls", "knowledge", "frozen", "ledger", "bulletin", "handoff", "glossary", "docs", "archive", "decision"];

    /// <summary>
    /// **可被声明改的判据 id**（C 批第一刀：只登记**真的会被兑现**的那些 ——
    /// 声明一条没被兑现的 id，等于给一个**假作用**，那比不写还坏）。
    /// </summary>
    public static readonly string[] KnownChecks =
        ["closeout.declarations", "memory.szone", "memory.watermark", "pitfalls.placements"];

    public static IReadOnlyList<Func<IEnumerable<CloseoutCli.Res>>> Of(string id) => id switch
    {
        "closeout" => [() => [CloseoutCli.CheckModuleDeclarations()]],
        "memory" => [() => [CloseoutCli.CheckSzonePresence()], () => [CloseoutCli.CheckWaterLevel()]],
        "pitfalls" =>
        [
            () => [CloseoutCli.RunGate("坑集索引")],
            () => [CloseoutCli.RunGate("公开坑集变体")],
            () => [CloseoutCli.CheckPitfallPlacements()],
            () => [CloseoutCli.CheckRecurrence()],
        ],
        "knowledge" => [() => [CloseoutCli.RunGate("知识树")]],
        "decision" => [() => [CloseoutCli.CheckDecisionBooks()]],
        "frozen" => [() => [CloseoutCli.RunGate("冻结语料")]],
        "ledger" => [() => [CloseoutCli.CheckLedger()]],
        "archive" => [() => [CloseoutCli.CheckArchiveDeclaration(), .. CloseoutCli.Vcs.Checks()]],
        _ => [],
    };
}

/// <summary>**生效的模块集**（声明优先；没有声明目录 ⇒ 内置默认）+ 自证问题清单 + **指纹**。</summary>
internal sealed class RequirementSet
{
    private RequirementSet(IReadOnlyList<ICloseoutRequirement> modules, IReadOnlyList<string> problems, bool declared,
        IReadOnlyDictionary<string, CheckDecl> checks)
    {
        Modules = modules;
        Problems = problems;
        Declared = declared;
        Checks = checks;
        Fingerprint = Compute(modules, checks);
    }

    public IReadOnlyList<ICloseoutRequirement> Modules { get; }

    /// <summary>声明自证的问题（空 = 通过）。</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>是否来自声明目录（false = 内置默认）。</summary>
    public bool Declared { get; }

    /// <summary>**生效模块集的指纹**（前 12 位）—— 判据/权限被改过，报告头看得见。</summary>
    public string Fingerprint { get; }

    /// <summary>判据声明表（`id` ⇒ 声明）。</summary>
    public IReadOnlyDictionary<string, CheckDecl> Checks { get; }

    /// <summary>该判据声明的 severity（没声明 ⇒ null ⇒ 用代码默认）。</summary>
    public string? SevOf(string checkId) =>
        Checks.TryGetValue(checkId, out var c) && !string.IsNullOrEmpty(c.Sev) ? c.Sev.Trim().ToLowerInvariant() : null;

    /// <summary>该判据声明的一个参数（没声明 ⇒ null ⇒ 用代码默认）。</summary>
    public string? ParamOf(string checkId, string key) =>
        Checks.TryGetValue(checkId, out var c)
            ? (c.Params ?? []).FirstOrDefault(p => string.Equals(p.K ?? "", key, StringComparison.Ordinal))?.V
            : null;

    /// <summary>该绝对路径归哪个模块所有（**最长前缀优先**：文件级声明覆盖目录级）。</summary>
    public (ICloseoutRequirement Module, Artifact Art)? Owner(string fullPath)
    {
        (ICloseoutRequirement, Artifact)? best = null;
        var bestLen = -1;
        foreach (var m in Modules)
        {
            foreach (var a in m.Artifacts)
            {
                var root = Path.GetFullPath(Path.Combine(CloseoutCli.Project, a.Path));
                if (!fullPath.StartsWith(root, StringComparison.Ordinal))
                {
                    continue;
                }

                if (root.Length > bestLen)
                {
                    bestLen = root.Length;
                    best = (m, a);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// **写守卫**（D 批）：这个路径在**这个时刻**能不能写？<c>null</c> = 放行；否则 = **拒绝理由**。
    /// <para>三条口径：① 只用 <c>Project</c> 下的路径受管（扩展自己的地盘 —— 如存档区在 Project 之外 —— 归扩展）；
    /// ② 路径不归任何模块 ⇒ **拒绝**（fail-closed，不许写“没人声明的角落”）；
    /// ③ 该模块在该时刻是 <c>Read</c> ⇒ **拒绝**（只读是真的写不了，不是靠纪律）。</para>
    /// </summary>
    public string? GuardWritable(string fullPath, When when)
    {
        var p = Path.GetFullPath(fullPath);
        if (!p.StartsWith(CloseoutCli.Project + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;   // Project 之外：扩展自己的地盘（存档区那类），CORE 不管
        }

        var rel = Path.GetRelativePath(CloseoutCli.Project, p);
        if (Owner(p) is not { } o)
        {
            return $"落点不在任何模块的清单里：{rel} ⇒ 拒绝写入（fail-closed，不许写没人声明的角落）";
        }

        var lvl = when == When.During ? o.Module.Access.During : o.Module.Access.Outside;
        if (lvl == Level.Write)
        {
            return null;
        }

        var at = when == When.During ? "收尾中" : "平时";
        return $"模块 `{o.Module.Id}` 在**{at}**是只读（{o.Module.Access}）⇒ 拒绝写入：{rel}";
    }

    /// <summary>声明目录（**唯一一处** = <c>AgentRuntime.Core.Closeout.CloseoutDeclarations.Dir</c>；运行时读**同一份**）。</summary>
    public static string Dir => CloseoutDeclarations.Dir;

    /// <summary>**锁定**：这两个必须存在且为 Core（删声明也不许消失）。</summary>
    private static readonly string[] Locked = ["closeout", "memory"];

    public static RequirementSet Load()
    {
        if (!Directory.Exists(Dir))
        {
            return new RequirementSet(Defaults(), [$"没有声明目录（{Dir}）⇒ 用内置默认"], declared: false,
                new Dictionary<string, CheckDecl>(StringComparer.Ordinal));
        }

        var problems = new List<string>();
        var decls = new List<RequirementDecl>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.xml").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var d = DataContractXml.ReadFile<RequirementDecl>(f);
                // ⚠️ DataContractSerializer **不走构造函数** ⇒ 声明里缺的元素是 **null**（不是初始化值）⇒ 全部按可空处理
                if (d is null || string.IsNullOrEmpty(d.Id))
                {
                    problems.Add($"{Path.GetFileName(f)}：读不出 Id ⇒ 整件跳过（不猜）");
                    continue;
                }

                decls.Add(d);
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(f)}：读不出来（{ex.GetType().Name}）⇒ 整件跳过 —— 请修");
            }
        }

        // 认识的 id 才收；不认识的**响亮**（不许静默产出空模块）
        foreach (var bad in decls.Where(d => !BuiltinChecks.Known.Contains(d.Id)).Select(d => d.Id).Distinct())
        {
            problems.Add($"未知模块 id「{bad}」⇒ 跳过（认识的 id：{string.Join(" / ", BuiltinChecks.Known)}）");
        }

        decls = [.. decls.Where(d => BuiltinChecks.Known.Contains(d.Id))];

        // **锁定件**：声明里缺/被禁用/被降级 ⇒ 补回内置那条 + 报问题（fail-closed：兜底不因删文件而丢）
        foreach (var id in Locked)
        {
            var d = decls.FirstOrDefault(x => x.Id == id);
            if (d is null)
            {
                problems.Add($"**{id} 是锁定件**：必须声明且 `Enabled=\"true\"`、`Class=\"core\"` ⇒ 已按内置补回");
                decls.Add(DefaultDecl(id));
                continue;
            }

            if (!d.Enabled || !Cls(d.Class).HasValue || Cls(d.Class)!.Value != ModuleClass.Core)
            {
                problems.Add($"**{id} 是锁定件**：不许禁用、不许降级为 extension ⇒ 已按内置纠正");
                d.Enabled = true;
                d.Class = "core";
            }
        }

        // 落点自证：越界 / 必达缺失
        foreach (var d in decls)
        {
            foreach (var a in d.Artifacts ?? [])
            {
                var full = Path.GetFullPath(Path.Combine(CloseoutCli.Project, a.Path));
                var inside = full.StartsWith(CloseoutCli.Project + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                             || full.StartsWith(CloseoutCli.Live + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                             || string.Equals(full, CloseoutCli.Project, StringComparison.Ordinal);
                if (!inside)
                {
                    problems.Add($"{d.Id}：落点越界（{a.Path} 落在 Project/Live 之外）");
                    continue;
                }

                if (a.Must && !File.Exists(full) && !Directory.Exists(full))
                {
                    problems.Add($"{d.Id}：必达落点不存在（{a.Path}）");
                }
            }
        }

        // 判据声明：只登记**真会被兜现**的 id（不认识的 ⇒ 响亮红，因为在声明里它等于一个“假作用”）
        var checks = new Dictionary<string, CheckDecl>(StringComparer.Ordinal);
        foreach (var d in decls)
        {
            foreach (var c in d.Checks ?? [])
            {
                if (!BuiltinChecks.KnownChecks.Contains(c.Id))
                {
                    problems.Add($"{d.Id}：判据 id「{c.Id}」本版**不被兜现** ⇒ 跳过（可改的：{string.Join(" / ", BuiltinChecks.KnownChecks)}）");
                    continue;
                }

                checks[c.Id] = c;
            }
        }

        var modules = decls
            .Where(d => d.Enabled)
            .OrderBy(d => d.Order)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => (ICloseoutRequirement)new DeclaredRequirement(d, Cls(d.Class) ?? ModuleClass.Core, Acc(d.Outside, d.During)))
            .ToList();

        return new RequirementSet(modules, problems, declared: true, checks);
    }

    private static ModuleClass? Cls(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "core" => ModuleClass.Core,
        "extension" => ModuleClass.Extension,
        _ => null,
    };

    private static Access Acc(string? outside, string? during) =>
        new(Word(outside), Word(during));

    private static Level Word(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() is "rw" or "write" ? Level.Write : Level.Read;

    /// <summary>指纹 = 生效模块集的**规范文本**的 sha256 前 12 位（顺序 id/order/class/权限/条数）。</summary>
    private static string Compute(IReadOnlyList<ICloseoutRequirement> modules, IReadOnlyDictionary<string, CheckDecl> checks)
    {
        var sb = new StringBuilder();
        foreach (var m in modules)
        {
            sb.Append($"{m.Id}|{m.Order}|{(m.Class == ModuleClass.Core ? "core" : "ext")}|{m.Access}|{m.Artifacts.Count}\n");
        }

        // 判据声明也进指纹（**改了阈值/severity 也算改过判据**，必须看得见）
        foreach (var (id, c) in checks.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            sb.Append($"{id}|{c.Sev}|{c.Enabled}|{string.Join(",", (c.Params ?? []).OrderBy(p => p.K ?? "", StringComparer.Ordinal).Select(p => $"{p.K}={p.V}"))}\n");
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12]
            .ToLowerInvariant();
    }

    /// <summary>内置默认（**只在没有声明目录时**用；顺序/权限与声明一致）。</summary>
    private static IReadOnlyList<ICloseoutRequirement> Defaults() =>
        [.. new[] { "closeout", "memory", "rules", "pitfalls", "knowledge", "frozen", "ledger", "bulletin", "handoff", "glossary", "docs", "archive" }
            .Select(DefaultDecl)
            .OrderBy(d => d.Order)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => (ICloseoutRequirement)new DeclaredRequirement(d, Cls(d.Class) ?? ModuleClass.Core, Acc(d.Outside, d.During)))];

    private static RequirementDecl DefaultDecl(string id) => id switch
    {
        "closeout" => new() { Id = id, Title = "收尾自身（声明自证）", Order = 5, Class = "core", Outside = "r", During = "rw" },
        "memory" => new() { Id = id, Title = "记忆系统（兜底）", Order = 10, Class = "core", Outside = "r", During = "r" },
        "rules" => new() { Id = id, Title = "铁则区（含 rules-changeOrder.md）", Order = 20, Class = "core", Outside = "r", During = "rw" },
        "pitfalls" => new() { Id = id, Title = "踩坑集", Order = 30, Class = "core", Outside = "rw", During = "rw" },
        "knowledge" => new() { Id = id, Title = "知识库", Order = 40, Class = "core", Outside = "r", During = "rw" },
        "frozen" => new() { Id = id, Title = "冻结语料", Order = 50, Class = "core", Outside = "r", During = "rw" },
        "ledger" => new() { Id = id, Title = "收尾账本", Order = 60, Class = "core", Outside = "r", During = "rw" },
        "bulletin" => new() { Id = id, Title = "公告板", Order = 70, Class = "core", Outside = "r", During = "rw" },
        "handoff" => new() { Id = id, Title = "交接件", Order = 80, Class = "core", Outside = "r", During = "rw" },
        // protocol clause 7 把「glossary」也列进 session closeout 要收敛的东西 ⇒ **要件清单里必须有它**（否则协议承诺的落点没人管）。
        "glossary" => new() { Id = id, Title = "术语表（GLOSSARY.md）", Order = 85, Class = "core", Outside = "r", During = "rw" },
        "docs" => new() { Id = id, Title = "设计文档", Order = 90, Class = "core", Outside = "r", During = "rw" },
        "archive" => new() { Id = id, Title = "存档（SVN / VCS）", Order = 100, Class = "extension", Outside = "r", During = "rw" },
        _ => new() { Id = id, Title = id, Order = 999, Class = "core", Outside = "r", During = "r" },
    };
}

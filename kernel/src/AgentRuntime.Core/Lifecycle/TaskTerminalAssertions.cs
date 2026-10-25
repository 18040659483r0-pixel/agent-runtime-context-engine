using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **任务类别**（P2-1 §11.3）—— 「这条任务改动了什么」决定它 [DONE] 之前**该成立什么**。
/// <para>为什么必须按类分：读数「65 次 [DONE] 里 12 次改动后没验证」把整盘算违规 ⇒ **尺子自己假红**
/// （只改文档的任务本就不需要 build/test）。类别是**断言的入口**。</para>
/// </summary>
public enum TaskClassKind
{
    /// <summary>**A 只读问答**：没有 edit/write。</summary>
    ReadOnly,

    /// <summary>**B 改代码**：改动落在 src/ · tests/。</summary>
    CodeChange,

    /// <summary>**C 文档 / 知识改**：改动落在 *.md。</summary>
    DocChange,

    /// <summary>**D 流程 / 收尾**：含闸门命令 / svn commit。</summary>
    Process,

    /// <summary>**E 派发**：出现 [DISPATCH] / Worker 结果。</summary>
    Dispatch,

    /// <summary>**F 跨会话 / 长任务**：有续跑 / 跨 session 痕迹。</summary>
    CrossSession,
}

/// <summary>一条终局断言的判定（**三态**：豁免与违规必须分得开，否则豁免会藏进绿里）。</summary>
public enum TerminalVerdict
{
    /// <summary>成立。</summary>
    Satisfied,

    /// <summary>**违规** ⇒ 不许硬发 [DONE]。</summary>
    Violated,

    /// <summary>**显式豁免**（该类本不需要此验证）—— 必须写明理由并单独记数。</summary>
    Exempt,
}

/// <summary>一条终局断言（类 + 判定 + 人话理由；理由带 seq 便于回查）。</summary>
public sealed record TerminalAssertion(TaskClassKind Class, TerminalVerdict Verdict, string Reason)
{
    /// <summary>挡不挡 [DONE]（只有 Violated 挡）。</summary>
    public bool Blocks => Verdict == TerminalVerdict.Violated;

    /// <summary>类的中文短名（屏上 / 报告用；不进协议正文）。</summary>
    public string ClassLabel => Class switch
    {
        TaskClassKind.ReadOnly => "A 只读问答",
        TaskClassKind.CodeChange => "B 改代码",
        TaskClassKind.DocChange => "C 文档/知识改",
        TaskClassKind.Process => "D 流程/收尾",
        TaskClassKind.Dispatch => "E 派发",
        TaskClassKind.CrossSession => "F 跨会话",
        _ => Class.ToString(),
    };
}

/// <summary>归一化后的事实种类（事件流 → 断言能读的形状；分两步才能让断言是纯函数）。</summary>
public enum TaskFactKind
{
    /// <summary>改动落在 src/ · tests/。</summary>
    CodeMutation,

    /// <summary>改动落在 *.md。</summary>
    DocMutation,

    /// <summary>改动落在别处。</summary>
    OtherMutation,

    /// <summary>一次验证（build / gate / generator）；Ok = 成功。</summary>
    Verification,

    /// <summary>提交（svn commit）。</summary>
    Commit,

    /// <summary>提交后读回版本号。</summary>
    CommitReadBack,

    /// <summary>一次派发。</summary>
    Dispatch,

    /// <summary>Worker 结果；Ok = 四字段齐且 open: 为空。</summary>
    WorkerResult,

    /// <summary>跨会话 / 续跑痕迹。</summary>
    Resume,

    /// <summary>交接件已落盘且非空（F 类的存在性断言）。</summary>
    Handoff,

    /// <summary>终局块正文（A 类要它非空）。</summary>
    Body,
}

/// <summary>一条事实（带序号，理由里要能引回流）。</summary>
public sealed record TaskFact(
    TaskFactKind Kind,
    bool Ok = true,
    string? Path = null,
    string? Label = null,
    string? Detail = null,
    long Seq = 0);

/// <summary>
/// **逐类终局断言**（P2-1 §11.3）—— **唯一声明处**：任务类判定 + 每类终局谓词都在这里。
/// <para>宿主（RuntimeHost）与命令行审计（Cli --terminal-audit）**共用本类**：一份判据两处用，
/// 读数与行为才不会各说一套。</para>
/// <para>三条通用口径：① **顺序断言**（证据必须晚于它依赖的改动）；② **豁免要显式**；
/// ③ **违规不许静默**（<see cref="TerminalAssertion.Blocks"/> 为真必须进屏 / 进流 / 进报告）。</para>
/// </summary>
public static class TaskTerminalAssertions
{
    /// <summary>build / test 类标记（**唯一声明处**；与尺子同口径）。</summary>
    public static readonly string[] BuildHints =
        ["dotnet test", "dotnet build", "dotnet run", "msbuild", "pytest", "npm test", "run-tests"];

    /// <summary>闸门类标记（--check / --selftest）。</summary>
    public static readonly string[] GateHints = ["--check", "--selftest", "Closeout.dll", "verify"];

    // ⚠️ 这里**不再**有关键词清单（2026-09-25 P1-2 遗留①）：
    // 关键词兜底会**假阳** —— `cat tools/x.py` 只是看文件，却因命中 "build" 被判成「跑过一次生成器」
    // ⇒ C 类拿到一次假的「派生层已重算」。真正的判据只有一条：**命令是否调用了仓内脚本**（§十·32）。
    /// <summary>
    /// **生成器的派生规则**（§十·32：按派生规则判，不按字面量清单判）——
    /// 命令**调用**了仓内的一个脚本（<c>python3 tools/….py</c> / <c>sh scripts/….sh</c> / <c>./tools/….sh</c>）。
    /// <para>为什么不能只留关键词表：新生成器一出现，关键词表**必漏** ⇒ C 类看不到「派生层已重算」，
    /// 判成**显式豁免**（静默假绿）。派生规则让「新脚本」当天就被认出来。</para>
    /// </summary>
    private static readonly Regex RepoScriptCall = new(
        @"(?:^|[\s;&|])(?:python3?|bash|sh|zsh|\./)[ \t]+(?<path>[^\s""']*(?:tools|scripts)/[\w\-./]+\.(?:py|sh))\b",
        RegexOptions.Compiled);

    /// <summary>
    /// **已知不是生成器**的脚本（唯一例外清单，**公开可数**）—— 长期运行的服务，不是「从源重算派生物」。
    /// <para>为什么必须是**显式清单**而不是「关键词表漏掉」：漏掉是**静默**的（判成豁免、看着像绿）；
    /// 写在这里则**可被覆盖率闸门点名核对**（<c>GeneratorCoverageTests</c>）。</para>
    /// </summary>
    public static readonly string[] NonGeneratorScripts = ["mock-provider"];

    /// <summary>
    /// **终局门提示的来源标记**（进 `Hint` 事件的账本字段 `Source`；与 DecisionReport.RequestSource 同族）。
    /// <para>只是账本字段（**不进 prompt**）⇒ 零协议代价、**不改协议正文**（前缀不归零）。</para>
    /// </summary>
    public const string GateSource = "terminal-gate";

    private static readonly Regex ExitOk = new(@"exit 0\b", RegexOptions.Compiled);
    private static readonly Regex Landing = new(@"落点\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex ArgsPath = new(@"""path""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Revision = new(@"[rR]\d{2,}|Revision|版本", RegexOptions.Compiled);

    /// <summary>
    /// 一条命令归到哪类验证（gate / generator / build；空串 = 不是验证）。
    /// <para>顺序固定为 **gate → generator → build**：<c>--closeout-check</c> 既像闸门又像脚本调用，
    /// 必须先判成闸门（D 类只认闸门；判成 generator 会让 D 类看不见它）。</para>
    /// </summary>
    public static string ClassifyVerification(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        if (GateHints.Any(h => command.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return "gate";
        }

        if (IsRepoScriptCall(command) && !IsExemptScript(command))
        {
            return "generator";
        }

        return BuildHints.Any(h => command.Contains(h, StringComparison.OrdinalIgnoreCase)) ? "build" : string.Empty;
    }

    /// <summary>
    /// **调用**了仓内脚本吗（派生规则）—— 必须由解释器 / <c>./</c> 起头，**只是提到路径不算**
    /// （<c>grep -rn 'tools/x.py'</c> 不是「跑了一次生成器」）。
    /// </summary>
    public static bool IsRepoScriptCall(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return RepoScriptCall.IsMatch(command);
    }

    /// <summary>这条命令调用的是**例外清单**里的脚本吗（服务类，不算派生物重算）。</summary>
    public static bool IsExemptScript(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (Match match in RepoScriptCall.Matches(command))
        {
            var path = match.Groups["path"].Value;
            if (NonGeneratorScripts.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>改动落点为哪一类（**按派生规则判**：看路径形状，不看文件名清单）。</summary>
    public static TaskFactKind ClassifyMutation(string? path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/');
        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return TaskFactKind.DocMutation;
        }

        var isCode = p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        var underCode = p.Contains("/src/", StringComparison.Ordinal)
            || p.Contains("/tests/", StringComparison.Ordinal)
            || p.StartsWith("src/", StringComparison.Ordinal)
            || p.StartsWith("tests/", StringComparison.Ordinal);

        return isCode && underCode ? TaskFactKind.CodeMutation : TaskFactKind.OtherMutation;
    }

    /// <summary>
    /// **按类判定**（纯函数）。返回所有适用类各一条断言（一条任务可以同时是 B 与 D）。
    /// <para>顺序即阅读顺序：B → D → E → F → C → A。</para>
    /// </summary>
    public static IReadOnlyList<TerminalAssertion> Assess(IReadOnlyList<TaskFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var result = new List<TerminalAssertion>();
        var lastCode = LastIndex(facts, TaskFactKind.CodeMutation);
        var lastDoc = LastIndex(facts, TaskFactKind.DocMutation);
        var lastAny = Math.Max(lastCode, Math.Max(lastDoc, LastIndex(facts, TaskFactKind.OtherMutation)));

        var okBuild = LastVerified(facts, "build", true);
        var okGen = LastVerified(facts, "generator", true);
        var okGate = LastVerified(facts, "gate", true);
        var anyGate = LastVerified(facts, "gate", null);
        var badGate = LastVerified(facts, "gate", false);
        var anyGateOrGen = anyGate >= 0 || LastVerified(facts, "generator", null) >= 0;

        // ---- B 改代码：最后一次成功 build/test 必须晚于最后一次代码改动 ----
        if (lastCode >= 0)
        {
            var ok = okBuild > lastCode;
            result.Add(new TerminalAssertion(
                TaskClassKind.CodeChange,
                ok ? TerminalVerdict.Satisfied : TerminalVerdict.Violated,
                ok
                    ? $"最后一次成功 build/test（index={okBuild}）晚于最后一次代码改动（index={lastCode}）"
                    : $"最后一次成功 build/test 不晚于最后一次代码改动（最后改动 index={lastCode}·seq={facts[lastCode].Seq}；最后成功验证 index={(okBuild < 0 ? "无" : okBuild.ToString())}）⇒ 不许 [DONE]"));
        }

        // ---- D 流程 / 收尾：闸门绿 + 提交后读回 r 号 ----
        var hasCommit = LastIndex(facts, TaskFactKind.Commit) >= 0;
        var hasReadBack = LastIndex(facts, TaskFactKind.CommitReadBack) >= 0;
        if (hasCommit || hasReadBack || anyGate >= 0)
        {
            string? problem = null;
            if (hasCommit && !hasReadBack)
            {
                problem = "提交后没有读回版本号（提交后必须 svn update / status 核对一次）";
            }
            else if (badGate > okGate)
            {
                problem = $"闸门 --check 有红项未收敛（最后失败 index={badGate}·seq={facts[badGate].Seq}，此后没有成功）";
            }

            result.Add(new TerminalAssertion(
                TaskClassKind.Process,
                problem is null ? TerminalVerdict.Satisfied : TerminalVerdict.Violated,
                problem ?? $"闸门 exit 0（index={okGate}）+ 提交读回（{(hasReadBack ? "有" : "无提交")}）"));
        }

        // ---- E 派发：必须收到 [WORKER] 四字段；open: 非空 ⇒ 不得据此 DONE ----
        var hasDispatch = LastIndex(facts, TaskFactKind.Dispatch) >= 0;
        var lastWorker = LastIndex(facts, TaskFactKind.WorkerResult);
        if (hasDispatch || lastWorker >= 0)
        {
            string? problem = null;
            if (lastWorker < 0)
            {
                problem = "派发了但没有收到 [WORKER] 契约四字段（结论/证据/改动/未决）⇒ 归属不明，改 [NEED-USER]";
            }
            else if (!facts[lastWorker].Ok)
            {
                // **按类取字段**（设计稿 §四 · 2026-09-30）：终局自验只看**这一类**的必需字段在不在 ——
                // 不再要求「四字段齐」（那正是“失败回执永远活不过闸门 ⇒ 死循环”的根）。
                // 旧形状（没有 `kind=`）落到兼容口径：四字段齐 ⇒ 说 open / 缺 ⇒ 说没交回执。
                var receipt = facts[lastWorker].Detail;
                var seq = facts[lastWorker].Seq;
                var kind = WorkerReceiptShape.KindOf(receipt);
                if (kind is null)
                {
                    problem = !WorkerFieldsPresent(receipt)
                        ? $"派发了但回执里没有 [WORKER] 契约四字段（结论/证据/改动/未决）—— seq={seq} ⇒ 归属不明"
                        : $"Worker 的 open: 非空（seq={seq}）⇒ 不得据此 [DONE]";
                }
                else
                {
                    var missingField = WorkerReceiptShape.MissingKindField(receipt, kind.Value);
                    var forbidden = WorkerReceiptShape.PresentForbiddenField(receipt, kind.Value);
                    problem = missingField is not null
                        ? $"回执 kind={ReceiptKinds.Name(kind.Value)} 缺该类必需字段 `{missingField}=`（seq={seq}）⇒ 不得据此 [DONE]"
                        : forbidden is not null
                            ? $"回执 kind=done 却带了 `{forbidden}=`（该类禁止）—— seq={seq} ⇒ 自相矛盾，不得据此 [DONE]"
                            : $"Worker 未结算：kind={ReceiptKinds.Name(kind.Value)}（seq={seq}）⇒ 不得据此 [DONE]";
                }
            }

            result.Add(new TerminalAssertion(
                TaskClassKind.Dispatch,
                problem is null ? TerminalVerdict.Satisfied : TerminalVerdict.Violated,
                problem ?? (WorkerReceiptShape.KindOf(facts[lastWorker].Detail) is { } k
                    ? $"回执 kind={ReceiptKinds.Name(k)} 字段齐（seq={facts[lastWorker].Seq}）"
                    : $"收到 [WORKER] 四字段且 open: 为空（seq={facts[lastWorker].Seq}）")));
        }

        // ---- F 跨会话：交接件必须落盘且非空 ----
        if (LastIndex(facts, TaskFactKind.Resume) >= 0)
        {
            var landed = LastIndex(facts, TaskFactKind.Handoff) >= 0;
            result.Add(new TerminalAssertion(
                TaskClassKind.CrossSession,
                landed ? TerminalVerdict.Satisfied : TerminalVerdict.Violated,
                landed
                    ? "有续跑痕迹且交接件已落盘"
                    : "有续跑 / 跨会话痕迹，但交接件没落盘（存在性断言不成立）⇒ 下一轮无从接续，不许 [DONE]"));
        }

        // ---- C 文档 / 知识改：最后一次生成器 / --check 必须晚于最后一次文档改动 ----
        if (lastDoc >= 0)
        {
            var lastGen = Math.Max(okGen, okGate);
            TerminalVerdict verdict;
            string reason;
            if (lastGen > lastDoc)
            {
                verdict = TerminalVerdict.Satisfied;
                reason = $"生成器 / --check（index={lastGen}）晚于最后一次文档改动（index={lastDoc}）";
            }
            else if (!anyGateOrGen)
            {
                // **显式豁免**（不是默认放行）：本任务里根本没有生成器 / 闸门命令可跑
                // （纯散文类文档：memory / handoff / PITFALLS …）⇒ 豁免会被单独记数（规格 §11.4）。
                verdict = TerminalVerdict.Exempt;
                reason = "本任务没有任何生成器 / 闸门命令可跑（纯文档改动，无派生层要重算）⇒ 显式豁免";
            }
            else
            {
                verdict = TerminalVerdict.Violated;
                {
                    var root = Environment.GetEnvironmentVariable("WB_ROOT");
                    var hint = string.IsNullOrWhiteSpace(root)
                        ? "跑一次收尾引擎的 `--check`（路径见仓库根 tools/closeout）"
                        : $"跑 `dotnet {root}/tools/closeout/bin/Release/net10.0/Closeout.dll --check`（用 $WB_ROOT，别自己改它的值）";
                    reason = $"改动过文档但最后一次生成器 / --check 不在其后（最后文档改动 index={lastDoc}·seq={facts[lastDoc].Seq}；最后生成器 index={(lastGen < 0 ? "无" : lastGen.ToString())}）⇒ 派生物可能落后；{hint}";
                }
            }

            result.Add(new TerminalAssertion(TaskClassKind.DocChange, verdict, reason));
        }

        // ---- A 只读问答：没有改动 ⇒ 无需验证；但正文必须非空（协议第 6 条） ----
        if (lastAny < 0)
        {
            var bodyIndex = LastIndex(facts, TaskFactKind.Body);
            var bodyEmpty = bodyIndex >= 0 && string.IsNullOrWhiteSpace(facts[bodyIndex].Detail);
            result.Add(new TerminalAssertion(
                TaskClassKind.ReadOnly,
                bodyEmpty ? TerminalVerdict.Violated : TerminalVerdict.Satisfied,
                bodyEmpty
                    ? "终局块没写正文（协议第 6 条要求块头后跟正文）⇒ 没有可交付的结论"
                    : "无改动 ⇒ 无需验证（只读问答）"));
        }

        return result;
    }

    /// <summary>这条任务的断言里有没有**挡门**的项（宿主据此决定放不放 [DONE] 过）。</summary>
    public static bool Blocks(IReadOnlyList<TerminalAssertion> assertions) => assertions.Any(a => a.Blocks);

    /// <summary>一句人话（进屏 / 进流都用它；**短**：只列违规项，豁免只给计数）。</summary>
    public static string Describe(IReadOnlyList<TerminalAssertion> assertions)
    {
        var bad = assertions.Where(a => a.Blocks).ToList();
        var exempt = assertions.Count(a => a.Verdict == TerminalVerdict.Exempt);
        var tail = exempt > 0 ? $"（另有 {exempt} 项显式豁免）" : string.Empty;
        return bad.Count == 0
            ? $"终局自验通过{tail}"
            : $"终局自验**没过**{tail}：" + string.Join("；", bad.Select(a => $"{a.ClassLabel} —— {a.Reason}"));
    }

    /// <summary>
    /// **当前任务**的事实（从**最后一条 UserInput** 起）。
    /// <para>为什么必须按任务切片：一条流里可以有多个任务，拿上一条任务的改动去判这一条的 <c>[DONE]</c> ⇒ 假红。</para>
    /// </summary>
    public static List<TaskFact> TaskFactsOf(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var list = events as IReadOnlyList<SessionEvent> ?? events.ToList();
        var start = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Kind == SessionEventKind.UserInput)
            {
                start = i;
            }
        }

        return FactsOf(list.Skip(start));
    }

    /// <summary>
    /// **终局门**（P2-1 §11.3）：给定模型的终局声明 + 当前任务的流 ⇒ 按类判定。
    /// <para>只挡 <c>[DONE]</c>；其余终局是正当出口，从不挡（规格 §11.3 出口列）。</para>
    /// </summary>
    public static TerminalGateReport GateOf(IEnumerable<SessionEvent> events, TerminalState declared)
    {
        if (declared != TerminalState.Done)
        {
            return TerminalGateReport.None;
        }

        var assertions = Assess(TaskFactsOf(events));
        return new TerminalGateReport(true, Blocks(assertions), assertions);
    }

    /// <summary>最后一次「某类验证」的 index（ok 为 null = 不论成败）；没有 ⇒ -1。</summary>
    private static int LastVerified(IReadOnlyList<TaskFact> facts, string label, bool? ok)
    {
        var found = -1;
        for (var i = 0; i < facts.Count; i++)
        {
            if (facts[i].Kind != TaskFactKind.Verification
                || !string.Equals(facts[i].Label, label, StringComparison.Ordinal))
            {
                continue;
            }

            if (ok is not null && facts[i].Ok != ok)
            {
                continue;
            }

            found = i;
        }

        return found;
    }

    /// <summary>某类事实最后一次出现的 index；没有 ⇒ -1。</summary>
    private static int LastIndex(IReadOnlyList<TaskFact> facts, TaskFactKind kind)
    {
        var found = -1;
        for (var i = 0; i < facts.Count; i++)
        {
            if (facts[i].Kind == kind)
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    /// **事件流 → 事实**（唯一的形状翻译处）：把「怎么从正文认出证据」收在这里，断言本身保持纯函数。
    /// <para>与尺子同口径：验证判据 = 命令回显 exit 0。</para>
    /// </summary>
    public static List<TaskFact> FactsOf(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var facts = new List<TaskFact>();
        foreach (var e in events)
        {
            var text = e.Text ?? string.Empty;
            switch (e.Kind)
            {
                case SessionEventKind.ToolResult when e.Source is "edit" or "write":
                    if (!text.Contains("失败", StringComparison.Ordinal))
                    {
                        var landing = LandingPathOf(text);
                        var mutation = ClassifyMutation(landing);
                        facts.Add(new TaskFact(mutation, Path: landing, Seq: e.Seq));

                        // F 类存在性断言的唯一可观测形态：交接件（handoff/）真的被写下来了。
                        if (landing is not null && landing.Replace('\\', '/').Contains("/handoff/", StringComparison.Ordinal))
                        {
                            facts.Add(new TaskFact(TaskFactKind.Handoff, Path: landing, Seq: e.Seq));
                        }
                    }

                    break;

                case SessionEventKind.ToolResult when e.Source == "exec":
                    var label = ClassifyVerification(text);
                    if (label.Length > 0)
                    {
                        facts.Add(new TaskFact(TaskFactKind.Verification, ExitOk.IsMatch(text), Label: label, Seq: e.Seq));
                    }

                    if (IsCommit(text))
                    {
                        facts.Add(new TaskFact(TaskFactKind.Commit, Seq: e.Seq));
                    }

                    if (IsReadBack(text))
                    {
                        facts.Add(new TaskFact(TaskFactKind.CommitReadBack, Seq: e.Seq));
                    }

                    break;

                case SessionEventKind.Hint:
                    // 跨会话 / 续跑痕迹：宿主自己发的「中断 ⇒ 按接续理解」那一种提示。
                    if (text.Contains("中断", StringComparison.Ordinal) || text.Contains("接续", StringComparison.Ordinal))
                    {
                        facts.Add(new TaskFact(TaskFactKind.Resume, Seq: e.Seq));
                    }

                    break;

                case SessionEventKind.WorkerDispatch:
                    facts.Add(new TaskFact(TaskFactKind.Dispatch, Seq: e.Seq));
                    break;

                case SessionEventKind.WorkerResult:
                    facts.Add(new TaskFact(TaskFactKind.WorkerResult, WorkerProtocolComplete(text), Detail: text, Seq: e.Seq));
                    break;

                case SessionEventKind.AgentOutput:
                    // 终局块的**正文**（A 类要它非空）；取法与 TerminalReport.Parse 同一口径。
                    var terminal = TerminalReport.Parse(text);
                    if (terminal.IsTerminal)
                    {
                        facts.Add(new TaskFact(TaskFactKind.Body, Detail: terminal.Detail, Seq: e.Seq));
                    }

                    break;
            }
        }

        return facts;
    }

    /// <summary>写结果的落点（优先固定形状「→ 落点 …」，退路取参数里的 path）。</summary>
    public static string? LandingPathOf(string text)
    {
        if (Landing.Match(text) is { Success: true } m)
        {
            return m.Groups[1].Value;
        }

        return ArgsPath.Match(text) is { Success: true } a ? a.Groups[1].Value : null;
    }

    /// <summary>改动归类（落点取不到 ⇒ 记 OtherMutation：不猜成代码，也不静默丢）。</summary>
    public static TaskFactKind MutationFactOf(string text) => ClassifyMutation(LandingPathOf(text));

    /// <summary>提交动作（svn commit / git commit）。</summary>
    /// <summary>提交**真的落地**才算（2026-09-28 · 修昨晚那条假红）。
    /// <para>旧写法只看结果文本里有没有 <c>svn commit</c> —— 于是**我在一轮里跑的 grep 模式**
    ///（`grep -rn 'svn commit' src/`）被算成「提交过」，接着 D 类判据报「提交后没读回版本号」。</para>
    /// <para>判据改成<b>落地证据</b>：svn 的成功行 <c>Committed revision</c>（或 git 的 <c>files changed</c>）。
    /// 附带效果正是要的那条豁免：<b>零提交 ⇒ 不产生 Commit 事实 ⇒ 不进「提交后没读回」分支</b>。</para></summary>
    private static bool IsCommit(string text) =>
        text.Contains("Committed revision", StringComparison.OrdinalIgnoreCase)
        || (text.Contains("git commit", StringComparison.OrdinalIgnoreCase)
            && text.Contains("file changed", StringComparison.OrdinalIgnoreCase));

    /// <summary>提交后**读回版本号**（svn update / status / info，且回显里出现修订号）。</summary>
    private static bool IsReadBack(string text)
    {
        var readBack = text.Contains("svn update", StringComparison.OrdinalIgnoreCase)
            || text.Contains("svn status", StringComparison.OrdinalIgnoreCase)
            || text.Contains("svn info", StringComparison.OrdinalIgnoreCase);
        return readBack && Revision.IsMatch(text);
    }

    /// <summary>
    /// **回执里有没有契约四字段**（结论 / 证据 / 改动 / 未决）—— 只判字段在不在，**不判内容**。
    /// <para>为什么要与「open 清不清」分开：那是**两种病**（「没交回执」vs「交了但没做完」）。
    /// 合成一个 bool 之后，消息只能报其中一种 —— 真机把**空回执**报成「open: 非空」，
    /// 判据把人指到错的方向（§十·31：量法错会伪装成结论）。</para>
    /// </summary>
    public static bool WorkerFieldsPresent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 四字段的唯一形状声明处 = WorkerReceiptShape（主人 2026-09-29 完整验证现场）：
        // 渲染侧写 `key="…"`、闸门侧此前只认 `key:` ⇒ 「明明有四字段却说没有」⇒ [DONE] 永远过不了。
        // 现在两边共用同一份：字段名 + 「`:` 或 `=` 都认、大小写不敏感、不要求行首」。
        return WorkerReceiptShape.HasAllRequired(text);
    }

    /// <summary><c>open:</c> 那一格是不是空的（**先有字段，才谈得上这一格**）。</summary>
    public static bool WorkerOpenClear(string? text) =>
        WorkerFieldsPresent(text) && IsEmptyMarker(ExtractField(text!, "open"));

    /// <summary>
    /// Worker 契约完整 = 四字段齐 **且** open 清（两病合一的兼容口径）。
    /// <para>新代码请分别用 <see cref="WorkerFieldsPresent"/> 与 <see cref="WorkerOpenClear"/>，好让消息按真病给。</para>
    /// </summary>
    public static bool WorkerProtocolComplete(string? text) =>
        WorkerFieldsPresent(text) && WorkerOpenClear(text);

    /// <summary>open: 的内容是不是「无 / none / -」这类空标记。</summary>
    public static bool IsEmptyMarker(string? value)
    {
        var v = (value ?? string.Empty).Trim().Trim('.', '。', '·', ' ');
        return v.Length == 0
            || v.Equals("none", StringComparison.OrdinalIgnoreCase)
            || v == "无"
            || v == "-";
    }

    private static string ExtractField(string text, string name)
    {
        // 认两种写法（同一件事）：`open: 还要读 201-1851`（worker 的块）与 `open="none"`（master 可见的渲染行）。
        return WorkerReceiptShape.Value(text, name);
    }
}

/// <summary>
/// **终局门的一次判定**（P2-1 §11.3）：模型报了终局之后，宿主按类自验的结果。
/// <para><see cref="Rejected"/> = **模型报的 [DONE] 不许过**（不许静默放行；出口见 <see cref="TaskTerminalAssertions.Describe"/> 的人话）。</para>
/// <para>只有 <c>[DONE]</c> 会被挡：<c>[NO-SOLUTION]</c> / <c>[NEED-USER]</c> 本就是正当出口。</para>
/// </summary>
public sealed record TerminalGateReport(bool Evaluated, bool Rejected, IReadOnlyList<TerminalAssertion> Assertions)
{
    /// <summary>没跑门（本轮没有终局块 / 不是 <c>[DONE]</c>）：不判违规，也不挡。</summary>
    public static readonly TerminalGateReport None = new(false, false, []);

    /// <summary>显式豁免的条数（**必须单独报**，不许混进「0 违规」——规格 §11.4）。</summary>
    public int ExemptCount => Assertions.Count(a => a.Verdict == TerminalVerdict.Exempt);

    /// <summary>违规项（挡门的那几条）。</summary>
    public IReadOnlyList<TerminalAssertion> Violations => Assertions.Where(a => a.Blocks).ToList();

    /// <summary>一句人话（屏上 / 进流都用它）。</summary>
    public string Describe() => TaskTerminalAssertions.Describe(Assertions);
}

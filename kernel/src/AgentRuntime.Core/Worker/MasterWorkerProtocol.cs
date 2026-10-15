using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **master-worker 运行模式的共用协议段**（主人 2026-09-30 16:58 令：两边必须具有**同一份**）。
/// <para><b>为什么要有它</b>：这条模式的规矩此前被**拆成两份各自改写** —— master 面在协议区第 13 条、
/// worker 面在worker 协议区第 5/7 条。两份一改写就不同源：worker 手里因此**没有"必须给哪些键"那张表**，
/// 于是它按直觉写散文、运行时按表把它拦下（真机 2026-09-30）。**同一件事只能有一处声明**（§十·17）。</para>
/// <para><b>纪律</b>：本段由 <see cref="DispatchIntents"/> 与 <see cref="ReceiptKinds"/> 的两张表**渲染**而成，
/// 不手写；两侧文本各自把它**逐字**嵌进去，并由夹具钉住「两侧都含同一份」（与 <c>ToolFaceClause</c> 同款闸门）。</para>
/// </summary>
public static class MasterWorkerProtocol
{
    /// <summary>本段的**唯一正文**（渲染产物；两侧共用同一份字符串）。</summary>
    public static readonly string Text = Render();

    public static int LineCount => Text.Split('\n').Length;

    public static int CharCount => Text.Length;

    /// <summary>正文指纹（前 8 字节 hex）：两侧文本各自的账本里都能回查到它。</summary>
    public static string Fingerprint =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..8];

    private static string Render()
    {
        var sb = new StringBuilder();

        sb.Append("[MASTER-WORKER] one shared clause -- the SAME text on both sides.\n");
        sb.Append("A dispatch is TWO things, both required: one sentence of your own saying what you want (verbatim),\n");
        sb.Append("and ONE combination picked from this closed list. Each entry is a TYPE (its meaning) with one INSTANCE:\n");
        foreach (var kind in Enum.GetValues<DispatchIntentKind>())
        {
            var keys = DispatchIntents.DeliveryKeys(kind);
            sb.Append("  ").Append(DispatchIntents.Name(kind)).Append(" -- TYPE: ").Append(DispatchIntents.Meaning(kind)).Append('\n');
            sb.Append("    delivers: ").Append(keys.Count > 0 ? string.Join(" ", keys.Select(k => "<" + k.TrimEnd('=') + ">")) : "(none)").Append('\n');
            sb.Append("    instance: ").Append(DispatchIntents.Instance(kind)).Append('\n');
        }

        // **side_effects 的唯一合法值按 kind 固定**（`DispatchIntents.IllegalCombination` 正是按它判等）——
        // 从**同一张表**渲染出来，**不让模型猜**。真机 2026-10-08：master 给 EXEC 写 `none` ⇒ 派发被语法闸门退回、
        // 白走一轮（条目 2 与条目 7 各撞一次）。顺带点明省缺写法：**省略它，运行时按类补上**。
        var allKinds = Enum.GetValues<DispatchIntentKind>();
        var fxNone = string.Join("/", allKinds.Where(k => DispatchIntent.DefaultSideEffects(k) == "none").Select(DispatchIntents.Name));
        var fxLocal = string.Join("/", allKinds.Where(k => DispatchIntent.DefaultSideEffects(k) == "local").Select(DispatchIntents.Name));
        sb.Append("Modifiers on the same line: criteria= (full | lines A-B | count N | members {...} | free \"...\"), "
                + "when= (sync|async), side_effects -- its ONLY legal value is fixed by the kind: none for " + fxNone
                + ", local for " + fxLocal + " (omit it and the runtime fills it in).\n");
        sb.Append("Shape: ONE element -- \"[INTENT] <intent><purpose>the sentence</purpose><kind>KIND</kind>"
                + "<criteria>full|lines A-B|count N|members {...}|free \"...\"</criteria><when>sync|async</when><sideEffects>none|local</sideEffects>"
                + "<must>what counts as done</must><ask>your own sentence</ask><scope>/abs/one</scope></intent>\" -- members by NAME (order does not matter); "
                + "at most 2 per reply.\n");

        sb.Append("The receipt is ONE element too: \"[RESULT] <receipt><kind>done</kind><answer>what you found out</answer>...\" -- kind is one of:\n");
        foreach (var kind in Enum.GetValues<ReceiptKind>())
        {
            sb.Append("  ").Append(ReceiptKinds.Name(kind)).Append(" -- TYPE: ").Append(ReceiptKinds.Meaning(kind)).Append('\n');
            sb.Append("    members: ").Append(string.Join(" ", ReceiptKinds.RequiredFields(kind).Select(k => "<" + k.TrimEnd('=') + ">"))).Append('\n');
            sb.Append("    instance: ").Append(ReceiptKinds.Instance(kind)).Append('\n');
        }

        // **[PACE] 节拍**（主人 2026-10-09 定）：闸门由运行时换算/显式设定 —— 同一件事只许一处声明。
        sb.Append("[PACE] the runtime times EVERY dispatch against a per-dispatch GATE (seconds): if it has not come back by then it STOPS the worker\n");
        sb.Append("and tells you to split the work (it does NOT retry -- a retry is just the same too-big chunk again).\n");
        sb.Append("One dispatch is NOT one generation: a worker may issue SEVERAL provider requests, so the gate is set generously (not from a single generation).\n");
        sb.Append("Below ").Append(PaceBudget.DefaultHardBelowTokPerSec.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" tok/s do NOT split further (splitting makes the task more complex, not smaller): ask the human -- swap the worker or raise the gate.\n");
        sb.Append("YOU may set the gate for this session: reply with one line \"[PACE] gate=<seconds>\" (").Append(PaceRequest.GateMinSeconds).Append("..").Append(PaceRequest.GateMaxSeconds)
          .Append(") -- or the older \"[PACE] ceiling=300|600|1200|off\"; anything outside is refused loudly, and the runtime echoes the effective gate. Ask the human first for \"off\" (wait it out with no ceiling).\n");
        sb.Append("When the worker or its provider changes, re-calibrate (the account describes a specific endpoint).\n");

        sb.Append("A receipt whose element is missing a required member of its kind is NOT the thing it claims: it is not done,\n");
        sb.Append("and it comes back. There is no \"not needed\" option -- if a member does not apply here, you picked the wrong\n");
        sb.Append("kind or criteria, so say it in the combination. One member per name; order does not matter. Every key is a MEMBER of\n");
        sb.Append("the receipt element -- NEVER inside <answer>: answer carries the prose only.\n");

        sb.Append("Receipt modifiers mirror the dispatch one-for-one: criteria= (echo the crit you were given), covered= ("
                + string.Join("|", ReceiptCombination.CoveredValues) + "), side_effects= ("
                + string.Join("|", ReceiptCombination.SideEffectValues) + "), scope = the absolute addresses touched.\n");

        // v46（2026-10-07 主人口径）：**临时回执（裁决请求）**—— 卡在“不是我能定的那件事”上时，
        // 不猜、也不结算：交一份**临时回执**给 master 裁定，链**继续**。形状与 [INTENT] 同一套。
        sb.Append("[RULING] a step that needs a decision that is NOT yours: do NOT guess and do NOT settle -- hand the Master a TEMPORARY receipt and CONTINUE (the ruling comes back as an event). shape: \"[RULING] <ruling><ask>what must be decided</ask><kind>KIND</kind><options>/abs/one</options></ruling>\" (add that kind's keys as members). Kinds (with their keys):\n");
        foreach (var ruling in Enum.GetValues<RulingKind>())
        {
            sb.Append("  ").Append(RulingKinds.Name(ruling)).Append(" -- ")
              .Append(string.Join(" ", RulingKinds.RequiredFields(ruling))).Append(" -- ")
              .Append(RulingKinds.Short(ruling)).Append('\n');
        }

        // v47（2026-10-07 主人口径）：**信封**（身份 + 时刻 + id + re + 模式）· **裁定回信**（临时回执的对偶）· **作废**。
        sb.Append("[ENVELOPE] every message between the two sides carries ONE line -- who (fingerprint) to whom, when (datetime), which one (id = hash(from|at)), and which one it answers (re=). Modes (direction): "
                + string.Join(" | ", Enum.GetValues<MessageMode>().Select(m => $"{MessageModes.Name(m)} {MessageModes.Direction(m)}")) + ".\n");
        sb.Append("[RULING-ANSWER] the Master's reply to an interim receipt -- its DUAL, same element shape: \"[RULING-ANSWER] <verdict><decision>DECISION</decision><value>the pick or the new direction</value><re>the interim id</re></verdict>\" -- DECISION is "
                + string.Join("|", Enum.GetValues<RulingDecision>().Select(RulingReplies.Name))
                + "\" + " + string.Join(" / ", Enum.GetValues<RulingDecision>().Select(d => $"{RulingReplies.KeyName(d)}:"))
                + " + \"re: <the interim id>\". Pick one of the offered options, or give a NEW direction of your own; ESCALATE hands it to the human. It continues.\n");
        sb.Append("[ABORT] the Master voids ONE in-flight dispatch by its envelope id: \"[ABORT] <id>\". The voided dispatch no longer counts; the chain continues.\n");

        // v37（主人 2026-10-06 口径）：形状层 / 实质层**分工**写进共用段 —— 两侧拿到的是**同一份**（§十·58）。
        // 2026-10-06：把**语义规则**从运行时搬进协议区（运行时只机械执行）—— 两条都由**代码里的唯一声明表**渲染。
        sb.Append("[WINDOW] a window is a READ SOURCE or a WRITE TARGET: a read source must ALREADY exist; a write target only needs its directory. Read sources: " + string.Join(", ", Enum.GetValues<DispatchIntentKind>().Where(DispatchIntents.WindowMustExist).Select(DispatchIntents.Name)) + ".\n");
        sb.Append("[FEEDBACK] a format-class failure (fix the shape, NO pitfall) is: " + string.Join(", ", FeedbackPolicy.FormatClass.Select(c => c.ToString())) + "; everything else attributable is a REAL ERROR (record a pitfall).\n");
        sb.Append("The SAME format gap repeating inside one task is ESCALATED to a real error (record it): the 1st time it is only a shape notice, the 2nd time it counts as a real one.\n");
        sb.Append("[CHAIN] DEFAULT IS CONTINUE on both sides. A reply stops the chain ONLY when the Master picks one of exactly three kinds: "
                + "solved (with the decision report) / unsolvable (ask the human) / needs-a-human-ruling. "
                + "The WORKER stops the same way, by its receipt kind: done / impossible / need_human stop it; partial / blocked / refused (or a missing kind) CONTINUE. "
                + "Everything else -- a tool call, a dispatch, a \"[BASE]\" ask, a bounced receipt -- CONTINUES; the runtime never interferes. "
                + "Whoever receives such an interaction MUST carry its continuation (the code enforces this by type). A human ruling that reveals a durable preference is recorded and converged into the USER-PREFERENCES set (a member of the rules zone).\n");
        sb.Append("[BASE] file addresses need a VERIFIED base -- verify ONCE, then trust your own record.\n");
        sb.Append("Master: before you emit any file address for the first time, ask the runtime by emitting a \"[BASE]\" block (one line, no arguments). "
                + "The runtime answers with an event carrying \"path base = <abs>\"; write that value into your own reply (it becomes a record in your stream) "
                + "and point your [FOCUS] at that event. From then on your stream carries the verified value -- do NOT ask again (a repeat ask only earns a reminder).\n");
        sb.Append("Worker: the dispatch carries the VERIFIED base as its \"base:\" line -- that is the value; never guess a tree.\n");
        sb.Append("[DUTY] shape vs substance -- the SAME split on both sides.\n");
        sb.Append("Worker: the substance is YOURS to certify. Your receipt carries the keys its kind requires plus a basis= anchor, and every number you state must be one the runtime can recompute from disk.\n");
        // v52（2026-10-07 主人口径）：**读数时效** —— 读数是**本轮那一次运行**的属性，不许被记忆 / 过往回执覆盖。
        // 真机：worker 的 exec stdout 里 sha 是对的，回执却声明了上一轮的旧 sha（记忆覆盖刚读到的值）。
        sb.Append("Freshness: a reading is true ONLY for the run that produced it -- quote a number ONLY from a TOOL RESULT of THIS dispatch. "
                + "A number you remember, or one an earlier receipt carried, is NOT a source; if you must reuse an earlier reading, label it as reuse WITH that turn, or write `unknown`. "
                + "An unstamped restatement is a false claim -- the runtime recomputes the numbers from disk and catches it.\n");
        sb.Append("Master: verify at the MINIMUM cost -- recompute those numbers (bytes / lines / sha256 / count) against disk and compare. Consistent => accept it. Inconsistent => send it back with the cause. Never redo the work to check it.\n");
        sb.Append("Runtime: SHAPE and SYNTAX only -- block shape, the closed kind set, the keys each kind requires, pairing, the terminal block. It never fills in, infers or backstops substance: a missing kind, key, cause or path is a MISS, not something it supplies. It also NAMES every shape/syntax problem in a notice you can read -- never a silent drop -- so fix that shape on your next try.\n");
        sb.Append("Evidence: after a task -- above all a CODE task -- BOTH sides state their evidence for the user. In this split the Worker ALSO hands the Master a SHORT recomputable evidence package (the numbers the runtime can recompute from disk: paths, bytes, lines, sha256, counts) so the Master checks it cheaply instead of redoing the work.\n");
        sb.Append("And when a shape/syntax fix worked, record the pitfall: the knowledge base is how the next session -- even a brand-new user -- stops repeating the same mistake.\n");

        // v38（2026-10-06）：把**两侧本该同一份**的三处（派发物段结构 / 工具面边界 / 路径基准）
        // 以及证据包形状 / free 判据含义 / 求解步边界，一并搬进共用段（§十·58：一条规矩只许一处声明）。
        sb.Append("[SPLIT] the dispatch and the evidence package -- the SAME text on both sides.\n");
        sb.Append("Dispatch sections, in order: TASK / TARGET-PROVIDER / KNOWLEDGE / base / WINDOW / MUST / ASK / CONTRACT. KNOWLEDGE is fact carried to the Worker; WINDOW entries carry their own size reading; MUST is what counts as done; ASK is the Master's own sentence, verbatim.\n");
        sb.Append("base: is the ONLY path base, and every address on EITHER side is ABSOLUTE -- a relative address is refused or resolved against base, never against a process cwd. The runtime prints the base value as a \"path base = <abs>\" line in your context -- read it there and build every address under it; never guess a tree. Tool face: the Master has index and scope ONLY -- read / list / write / edit / exec belong to the Worker; one shape, declared once.\n");
        sb.Append("Runtime feedback comes in TWO kinds, and telling them apart is your job: a SYNTAX/format notice means the protocol zone cannot enumerate every usage -- fix the format and send it again (do NOT record a pitfall for it); a REAL ERROR means something did not actually happen or a reading did not recompute -- record THAT one in your pitfalls, because the knowledge base is how the next session stops repeating it.\n");
        sb.Append("criteria=free \"...\" is the Master's own criterion: the Worker satisfies it by honest judgement -- carried, never machine-checked, and no receipt field is invented for it.\n");
        sb.Append("Evidence package (Worker -> Master) is a SHORT block of recomputable numbers -- path / bytes / lines / sha256 / counts / command + exit code -- that is what the Master recomputes; prose belongs in answer:, never in the package.\n");
        sb.Append("One dispatch IS one solve step: settling the receipt closes it. That receipt IS the Worker's decision report in MACHINE form -- "
                + "the combination (kind + its axes) plus the human sentence ([RESULT] / answer: / evidence:). The user-facing article stays the MASTER's.\n");

        return sb.ToString();
    }
}

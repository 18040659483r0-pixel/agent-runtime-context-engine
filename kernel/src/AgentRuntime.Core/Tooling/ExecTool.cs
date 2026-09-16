using System.Diagnostics;
using System.Text;
using AgentRuntime.Core.Security;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// <c>exec</c> —— 在**本机**跑一条命令（**需批**）。
/// <para>
/// <b>为什么有它</b>（主人 2026-09-16 12:58 定）：[WB] 要**自持**，就得能自己跑 <c>svn</c> 与语料重建脚本；
/// 先前的「论文里没有这一项」判断作废 —— <b>先跑通闭环，安全框架后补</b>。
/// </para>
/// <para>
/// <b>本版不做的</b>（都是"后补"的安全框架，不是遗漏）：命令白名单 / 网络限制。
/// <para>
/// <b>执行层围栏（T3 下半段第一刀，2026-09-16 21:4x）</b>：启用时命令改为在 seatbelt 沙箱里跑
/// （<see cref="SandboxFence"/>）—— 拒读写「保护集」、拒提权程序，且**启用而不可用时就拒跑**（fail-closed）。
/// 注意它**不改 uid**：关得住「[WB] 自己能造成的副作用」，关不住「[WB] 之外的同 uid 进程」（真降权见 §9.4-1，未做）。
/// </para>
/// 现在能守住的只有三条，而且都是**别把会话搞死**而非安全：
/// ① 超时（<see cref="ToolLimits.ExecTimeoutSeconds"/>，模型可调但不得越过硬上限）；
/// ② 输出上限（<see cref="ToolLimits.MaxOutputChars"/>，**超限明说**，不静默截）；
/// ③ 走审批闸门（一次一批、非交互 ⇒ 拒、**模型不得自批**）。
/// </para>
/// <para>
/// 语义与既有约定一致：**非 0 退出码是"结果"**（跑了但失败），不是"拒绝" —— 事实照原样回给模型。
/// </para>
/// </summary>
public sealed class ExecTool : ToolBase
{
    public override string Name => ToolNames.Exec;

    /// <summary>工具档（默认需批）；**真正生效的是 <see cref="RiskFor"/>**（按命令升档）。</summary>
    public override ToolRisk Risk => ToolRisk.Mutating;

    /// <summary>
    /// **按命令定档**（主人 2026-09-16 13:1x 定）：发布 / 不可逆命令 ⇒ <see cref="ToolRisk.Critical"/>（must-ask）。
    /// <para>判据在唯一声明处 <see cref="ExecCommandRisk.Classify"/>；**判不出 ⇒ 需批**（不降为免批）。</para>
    /// </summary>
    public override ToolRisk RiskFor(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return ExecCommandRisk.Classify(args.OptionalString("command"));
    }

    protected override string[] AllowedArguments => ["command", "timeoutSeconds"];

    public override string Describe(ToolArgs args) => $"exec {args.Canonical}";

    /// <summary>工作目录（判不出就用进程当前目录；只进审批面，不是围栏）。</summary>
    public static string WorkingDirectory
    {
        get
        {
            var cwd = Directory.GetCurrentDirectory();
            return string.IsNullOrWhiteSpace(cwd) ? "(判不出)" : cwd;
        }
    }

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);

        var command = args.RequireString("command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ToolUsageException("command 为空 / 只有空白 ⇒ 拒绝（没有要跑的东西）。");
        }

        if (args.OptionalInt("timeoutSeconds") is { } seconds && seconds <= 0)
        {
            throw new ToolUsageException($"timeoutSeconds={seconds} 非法（必须 > 0；不写就用默认值）。");
        }
    }

    /// <summary>
    /// 审批面：**完整命令原文** + 工作目录 + 超时（S2）；
    /// must-ask 档另加两条：① 显著标注本档含义；② **展开被引用的提交信息全文**（§十·34）。
    /// </summary>
    public override ApprovalFace Preview(ToolArgs args, ToolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(limits);

        var command = args.RequireString("command");
        var risk = RiskFor(args);
        var (notes, body, fullText) = MustAskExtras(command, risk);

        // 人在点头之前，应该看到“这一次是不是在笼子里跑”。
        notes.Add(SandboxFence.Summary());

        return ApprovalFaces.Command(
            Name, risk, args, limits,
            command,
            WorkingDirectory,
            TimeoutFor(args, limits),
            notes,
            body,
            fullText);
    }

    /// <summary>
    /// must-ask 档的附加审批面。**这里只有"多给人看"，没有"少给人看"**。
    /// <para>逐字展开 <c>-F &lt;文件&gt;</c> 引用的提交信息：只写"提交信息见某文件"＝人没看见内容＝那次点头不是知情同意。</para>
    /// </summary>
    private static (List<string> Notes, List<ApprovalLine> Body, string? FullText) MustAskExtras(
        string command, ToolRisk risk)
    {
        var notes = new List<string>();
        var body = new List<ApprovalLine>();
        var extra = new StringBuilder();

        if (risk == ToolRisk.Critical)
        {
            notes.Add("⚠️ **must-ask 档（发布 / 不可逆）**：这类动作要么别人会看见、要么回不去 —— 请**读完整段原文**再决定（本次点头只覆盖这一个动作）。");
        }

        foreach (var referenced in ExecMessageFiles.ReferencedIn(command))
        {
            string real;
            try
            {
                real = ToolPaths.Normalize(referenced);
            }
            catch (ToolUsageException)
            {
                // 路径判不出真身 ⇒ 不展开，但**要说清楚没展开**（否则就是让人在不知情下点头）。
                notes.Add($"⚠️ 引用的提交信息路径判不出真身：{referenced} ⇒ **未展开全文**。");
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(real);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                notes.Add($"⚠️ 提交信息文件读不到（{real}）：{ex.Message} ⇒ **未展开全文**。");
                body.Add(new ApprovalLine('*', $"── 提交信息全文（{real}）── 读不到，无法展开"));
                continue;
            }

            body.Add(new ApprovalLine('*', $"── 提交信息全文（{real}）──"));
            foreach (var line in LineDiff.SplitLines(content))
            {
                body.Add(new ApprovalLine('*', line));
            }

            extra.Append('\n').Append("--- 提交信息全文（").Append(real).Append("）---\n").Append(content);
            if (!content.EndsWith('\n'))
            {
                extra.Append('\n');
            }

            notes.Add($"提交信息已**逐字展开**（{real}；{LineDiff.LineCount(content)} 行 / {Encoding.UTF8.GetByteCount(content)} 字节）——决定型内容必须完整可见（§十·34）。");
        }

        return (notes, body, extra.Length == 0 ? null : extra.ToString());
    }

    protected override async ValueTask<ToolOutcome> RunAsync(
        ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var command = args.RequireString("command");
        var timeout = TimeSpan.FromSeconds(TimeoutFor(args, context.Limits));

        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var fence = SandboxFence.IsEnabled;
        if (fence && !OperatingSystem.IsWindows() && !SandboxFence.Available)
        {
            return ToolOutcome.Failure(
                $"exec {command} → 拒绝：执行层围栏已启用，但本机找不到 {SandboxFence.SandboxExec}。"
                + "（fail-closed：绝不静默降级成无围栏执行。要临时关掉请显式设 WB_SANDBOX=0。）");
        }

        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(command);
        }
        else if (fence)
        {
            var (fileName, arguments) = SandboxFence.Wrap(command);
            info.FileName = fileName;
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        if (!process.Start())
        {
            return ToolOutcome.Failure($"exec {args.Canonical} → 起不来（进程未启动）。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var killedByTimeout = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            killedByTimeout = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已经自己退了 —— 无所谓。
            }
        }

        var exit = killedByTimeout ? (int?)null : process.ExitCode;
        var body = BuildBody(command, stdout.ToString(), stderr.ToString(), exit, killedByTimeout, context.Limits);

        return killedByTimeout
            ? ToolOutcome.Failure($"{body}\n（超时 {timeout.TotalSeconds:0}s ⇒ 已终止；改 timeoutSeconds 或把命令拆小。）", body.Length >= context.Limits.MaxOutputChars)
            : exit == 0
                ? ToolOutcome.Success(body, body.Length >= context.Limits.MaxOutputChars)
                : ToolOutcome.Failure(body, body.Length >= context.Limits.MaxOutputChars);
    }

    /// <summary>超时判据（唯一处）：模型给了就用它，但要夹在硬上限内；没给用出厂档。</summary>
    private static int TimeoutFor(ToolArgs args, ToolLimits limits)
    {
        var requested = args.OptionalInt("timeoutSeconds") ?? limits.ExecTimeoutSeconds;
        return Math.Clamp(requested, 1, Math.Max(1, limits.AbsoluteMaxExecTimeoutSeconds));
    }

    private static string BuildBody(
        string command, string stdout, string stderr, int? exit, bool killed, ToolLimits limits)
    {
        var text = new StringBuilder();
        text.Append("exec ").Append(command).Append(" → ");
        text.Append(killed ? "超时被终止" : $"exit {exit}");
        text.Append('\n');

        if (stdout.Length > 0)
        {
            text.Append("--- stdout ---\n").Append(stdout);
        }

        if (stderr.Length > 0)
        {
            text.Append("--- stderr ---\n").Append(stderr);
        }

        var body = text.ToString();
        if (body.Length <= limits.MaxOutputChars)
        {
            return body;
        }

        // **超限明说**（不静默截）—— 截断必须让人/模型知道"后面还有"。
        return body[..limits.MaxOutputChars]
            + $"\n…（截断：仅前 {limits.MaxOutputChars} 字符，共 {body.Length} 字符）";
    }
}

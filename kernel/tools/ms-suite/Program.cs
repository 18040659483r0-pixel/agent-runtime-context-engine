// [WB] M 套件工具 —— `.stage1/ms/**` 的 python 驱动搬到 `tools/` 的 C# 版。
// 每一片 = 一个产物（纪律）：判据是**与盘上产物逐字节等价**。
using MsSuite;

if (args.Length == 0)
{
    Usage();
    return 2;
}

switch (args[0])
{
    case "metrics":
        return Metrics.Run(args[1..]);
    case "closeout-timing":
        return CloseoutTiming.Run(args[1..]);
    case "suite":
        return Suite.Run(args[1..]);
    case "m10":
        return M10.Run(args[1..]);
    case "selftest":
        return Selftest.Run(args[1..]);
    case "shadow-run":
        return Shadow.Run(args[1..]);
    case "shadow-task":
        return Shadow.Task(args[1..]);
    case "shadow-all":
        return Shadow.All(args[1..]);
    case "shadow-repeat":
        return Shadow.Repeat(args[1..]);
    default:
        Usage();
        return 2;
}

static void Usage() =>
    Console.Error.WriteLine(
        "用法：ms-suite <命令> [参数…]\n" +
        "  metrics <task> <stream.xml> <usage.xml> <wall>   从一次跑成的流/用量里算任务口径读数（TSV，同 metrics.py）\n" +
        "  closeout-timing [--run] [--proj <dir>] [--config <xml>] [--out <dir>]   真收尾计时三段（默认只打印计划；--run 才真跑）\n" +
        "  suite [--run] [--only M1,M2] [--proj <dir>] [--config <xml>] [--out <dir>]    M 套件全线跑 M1~M10（默认只打印计划；--run 才真跑）\n" +
        "  m10 [--run] [--proj <dir>] [--config <xml>]    M10 收尾件单跑（沙箱隔离区；默认只打印计划；--run 才真跑）\n" +
        "  selftest [--data <dir>]    metrics 与金标逐字节对证（参考输入 + 金标入库）\n" +
        "  shadow-run <base> <材料文件> [maxwait]      往 tmux 会话 `wb` 投材料、等 idle、抓 pane（原 drive.sh）\n" +
        "  shadow-task <base> <M> [max]                恢复夹具 + /reset --start + 投题 + 抓结果（原 task.sh）\n" +
        "  shadow-all <base> [M1 M2 …]                 全量重跑 + 逐题 metrics（原 runall.sh）\n" +
        "  shadow-repeat <base> <task> <n> [max]       同题 N 跑 + 报中位（原 repeat.sh）");

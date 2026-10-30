using CloseoutTool;

// [WB] 收尾流水线（closeout）—— `tools/closeout.py` 的 C# 替身（A1 旗舰刀）。
// 子命令与 py 同形。**本工具不是运行时的一部分**：按 R2 铁则（docs/RULES-RUNTIME.md），
// 运行时（src/）唯一能起进程处是工具面 ExecTool；本工具要起 svn / 调 CLI 闸门 ⇒ 归 tools/。

// **写守卫等声明类失败 ⇒ 干净的响亮出口**（不用未捕获异常喷栈）：
// 「只读」是**真的写不了**（D 批）；声明写错/落点越界/锁定件被降级都走这里。
try
{
    return Dispatch(args);
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine("[WB] 收尾中止（声明/写守卫）：" + ex.Message);
    Console.Error.WriteLine("  处置：改回声明（`tools/closeout/requirements/`）后重跑；权限口径见 `--check` 报告头的模块行。");
    return 3;
}

static int Dispatch(string[] args)
{
if (args.Contains("--decision-converge"))
{
    return DecisionConverge.Run();
}

if (args.Contains("--l4-gap"))
{
    return L4Gap.Run();
}

if (args.Contains("--kn-plan"))
{
    return KnLinks.Plan();
}

if (args.Contains("--kn-link"))
{
    var links = new List<string>();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--kn-link" && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            links.Add(args[i + 1]);
        }
    }

    return KnLinks.Link(links);
}

if (args.Contains("--l4-sync"))
{
    var max = 0;
    var at = Array.IndexOf(args, "--max");
    if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var parsed))
    {
        max = parsed;
    }

    return L4Gap.Sync(args.Contains("--dry-run"), max);
}

if (args.Contains("--apply"))
{
    return CloseoutCli.Apply();
}

if (args.Contains("--promote"))
{
    return CloseoutCli.Promote(args.Contains("--dry-run"));
}

if (args.Contains("--where"))
{
    return CloseoutCli.Where();
}

if (args.Contains("--selftest"))
{
    return CloseoutCli.Selftest();
}

if (args.Contains("--close"))
{
    return CloseoutCli.Close(args.Contains("--dry-run"));
}

if (args.Contains("--receipt"))
{
    return CloseoutCli.Receipt(args.Contains("--write"));
}

if (!args.Contains("--check"))
{
    PrintHelp();
    return 2;
}

return CloseoutCli.Check(args.Contains("--xml"));
}

static void PrintHelp()
{
    Console.WriteLine("[WB] 收尾流水线（closeout）—— 把「收尾欠不欠账」变成可随时跑、可自证的机器判据");
    Console.WriteLine();
    Console.WriteLine("  --check      只读：逐项重算并报红绿（block 红 ⇒ 退出码 1）");
    Console.WriteLine("  --xml        机器可读输出（强类型 XML；与 --check 合用）");
    Console.WriteLine("  --apply      幂等重生**确定派生物**（坑集索引 / 公开变体 / 水位索引区 / worker 索引）");
    Console.WriteLine("  --promote    知识晋级：活版 → versions/knowledge-vNNNN.md + 账本两处（已在 = no-op；--dry-run 只报）");
    Console.WriteLine("  --close      顺序闸门：先 --check（block 全绿）→ 才写「收敛点」到水位 + 账本");
    Console.WriteLine("  --dry-run    与 --close 合用：只打印不写");
    Console.WriteLine("  --selftest   负例自证：注入缺陷断言闸门变红（在 /tmp 副本上跑）");
    Console.WriteLine("  --decision-converge  决策库收敛**报告**（只读、不写回）：空档 / 去重疑点 / 孤儿 / 上行可达");
    Console.WriteLine("  --l4-gap     条目→L4 覆盖**报告**（只读）：哪些坑/决策条目还没变成 L4 节点（水位 = 目标树里的锚）");
    Console.WriteLine("  --l4-sync [--dry-run] [--max N]  条目→L4 **机械搬运**：逐条写入 L4（逐字）并抬水位；--max 控制本次条数");
    Console.WriteLine("  --kn-plan    知识树归位清单（只读）：未挂父的 L4 + 现有 L3 目录");
    Console.WriteLine("  --kn-link <子>=<父>（可重复）  把 L4 挂到某个 L3（幂等 · 单亲 · 逐层）");
    Console.WriteLine("  --receipt [--write]  收尾回执（4 组事实 + 增量）：两库版本/计数/孤儿/未标记 L4");
    Console.WriteLine("  --where      打印绝对根与规范调用（治起点目录/相对路径类失败）");
    Console.WriteLine();
    Console.WriteLine(CloseoutCli.CloseOrder);
}

// [WB] 运维/发布/同步 CLI（原 11 个 `.sh` 的 C# 替身）。
using WbCli;

if (args.Length == 0)
{
    Usage();
    return 2;
}

switch (args[0])
{
    case "run":
        return Launch.Run(args[1..]);
    case "bench":
        return Launch.Bench(args[1..]);
    case "tui":
        return Launch.Tui(args[1..]);
    case "scan":
        return Scan.Run(args[1..]);
    case "pack":
        return Pack.Run(args[1..]);
    case "sync":
        return Sync.Run(args[1..]);
    case "commit":
        return Commit.Run(args[1..]);
    case "img2jpg":
        return Img2Jpg.Run(args[1..]);
    case "install-t3":
        return InstallT3.Run(args[1..]);
    default:
        Usage();
        return 2;
}

static void Usage() =>
    Console.Error.WriteLine(
        "用法：wb-cli <命令> [参数…]\n" +
        "  run [--config <xml>] [args…]          启动 CLI（原 run.sh；默认 config.xml，自带 --config 则不覆盖）\n" +
        "  bench [args…]                         启动 benchmark（原 benchmark/run-bench.sh）\n" +
        "  tui [args…]                           启动 TUI2（TUI1 已于 2026-10-29 退役）\n" +
        "  scan <待发布目录>                      发布前扫描（原 tools/pre-publish-scan.sh）\n" +
        "  pack [--tier A|B] [--out <dir>]       打包冻结语料（原 tools/pack-transfer.sh）\n" +
        "  sync status|push|pull                 [WB] 活版 ↔ 存档区同步（原 wb-sync.sh）\n" +
        "  commit -F <信息文件> [--no-add] <路径…> 提交并**回读改动路径清单**（add 输出不吞；坑 #317）\n" +
        "  img2jpg <图> [宽] [质]                 任意图 → 高压缩 JPG（原 img2jpg.sh）\n" +
        "  install-t3 [--dry-run|--uninstall] [--allow-uid N]   判定端安装器（需 root；原 install-t3.sh）");

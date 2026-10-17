// 自检：把 metrics 的「与 python 逐字节等价」判据**固化进工程**（参考输入 + 金标一起入库）。
// 这样 python 参考退役后，判据仍在跑（纪律：判据别比被测物活得长）。
using System.Text;

namespace MsSuite;

internal static class Selftest
{
    private const string ExpectedM4 =
        "M4\tturns_all=2\tturns_task=2\ttools=1\tdenials=0\tcomp_task=510\tcomp_all=510\t" +
        "ms_task=3777\tms_all=3777\tunc_task=577\tcached_task=132864\treport=0\twall=self";

    public static int Run(string[] a)
    {
        var root = Paths.RepoRoot();
        var dir = Paths.Arg(a, "--data", Path.Combine(root, "tools/ms-suite/testdata"));
        var stream = Path.Combine(dir, "m4.stream.xml");
        var usage = Path.Combine(dir, "m4.usage.xml");
        if (!File.Exists(stream) || !File.Exists(usage))
        {
            Console.Error.WriteLine($"[ms-suite] 缺 testdata：{dir}");
            return 3;
        }

        var got = Capture(() => Metrics.Run(["M4", stream, usage, "self"]));
        if (got == ExpectedM4)
        {
            Console.WriteLine("✅ metrics 自检：与金标逐字节一致（M4）");
            return 0;
        }

        Console.Error.WriteLine("❌ metrics 自检：与金标不一致");
        Console.Error.WriteLine("  期望：" + ExpectedM4);
        Console.Error.WriteLine("  实得：" + got);
        return 1;
    }

    private static string Capture(Func<int> f)
    {
        var orig = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            f();
        }
        finally
        {
            Console.SetOut(orig);
        }

        return sw.ToString().TrimEnd('\n');
    }
}

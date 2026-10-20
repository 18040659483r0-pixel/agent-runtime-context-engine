using System.Text;
using AgentRuntime.Hosting;
using AgentRuntime.Tui2;

// AgentRuntime.Tui2 —— 方案 B 的宿主入口（docs/DESIGN-TUI-EDITOR-B.md）。
// 只做三件事：① 起 Terminal.Gui + Editor；② 用自有 Utf8InputDriver 喂键；③ 把提交的正文交给 RuntimeHost 跑一轮。
// 不碰 AgentRuntime.Core / 协议区 / 工具面。
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // 无控制台：照旧（--selftest 不依赖它）。
        }

        var options = Tui2Options.Parse(args);

        if (options.Help)
        {
            Tui2Options.PrintUsage();
            return 0;
        }

        if (options.SelfTest)
        {
            return SelfTest.RunAll(Console.Out);
        }

        if (options.AutoTestSeconds > 0)
        {
            return App.RunAutoTest(options.EvidencePath, options.AutoTestSeconds);
        }

        if (options.InputTest)
        {
            return InputTest.Run(options);
        }

        return App.Run(options);
    }
}
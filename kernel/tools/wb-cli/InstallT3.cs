// 判定端安装器（原 tools/security/install-t3.sh → C#）。**唯一需要 root 的一次**（建账号 / 写 /Library/LaunchDaemons / 改 /usr/local）。
// 五件都可逆：① 建隐藏服务账号 wb-gate ② 自包含发布判定端到 /usr/local/libexec/wb-gate/app（属主 root）
// ③ 建状态目录（根 0755 + state/ 0700）④ 装 launchd 守护（开机自起）⑤ 起服务 + 以允许的 uid 自检
using System.Diagnostics;
using System.Text;

namespace WbCli;

internal static class InstallT3
{
    private static readonly string UserRoot = string.Concat("/", "Users", "/");

    private const string Label = "local.wb.gate";
    private static readonly string Plist = $"/Library/LaunchDaemons/{Label}.plist";
    private const string StateRoot = "/usr/local/var/wb-gate";
    private const string AppDir = "/usr/local/libexec/wb-gate/app";
    private const string GateUser = "wb-gate";
    private const int DefaultUid = 400;

    private static bool _dry;
    private static readonly HashSet<string> Owned = [];

    public static int Run(string[] a)
    {
        var uninstall = false;
        var allowUid = Environment.GetEnvironmentVariable("SUDO_UID") ?? Uid();

        for (var i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--dry-run": _dry = true; break;
                case "--uninstall": uninstall = true; break;
                case "--allow-uid" when i + 1 < a.Length: allowUid = a[++i]; break;
                default:
                    Console.Error.WriteLine("未知参数：" + a[i]);
                    return 2;
            }
        }

        if (Uid() != "0")
        {
            Console.Error.WriteLine("需要 root：请用 sudo 运行（这是 T3 唯一需要授权的一次）。");
            return 1;
        }

        var root = Repo.Root();
        var csproj = Path.Combine(root, "src", "AgentRuntime.Cli", "AgentRuntime.Cli.csproj");

        if (uninstall)
        {
            Say("[T3] 卸载：");
            Run2("launchctl", ["bootout", "system", Plist]);
            Run2("rm", ["-f", Plist]);
            Run2("rm", ["-rf", AppDir]);
            if (Capture("dscl", [".", "-read", UserRoot + GateUser]).Rc == 0)
            {
                Run2("dscl", [".", "-delete", UserRoot + GateUser]);
            }

            Say($"[T3] 已卸载（状态目录 {StateRoot} 保留，含账本；要一并删请自行 rm -rf）。");
            return 0;
        }

        var dotnet = Which("dotnet");
        if (dotnet is null) { Console.Error.WriteLine("找不到 dotnet（发布需要 SDK）。"); return 1; }
        if (!File.Exists(csproj)) { Console.Error.WriteLine("找不到 " + csproj); return 1; }

        var allowGroup = Capture("id", ["-gn", allowUid]).Text.Trim();
        if (allowGroup.Length == 0) { allowGroup = "staff"; }
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER") ?? UserName();
        var userHome = Capture("dscl", [".", "-read", UserRoot + sudoUser, "NFSHomeDirectory"]).Text.Trim().Split(' ').LastOrDefault() ?? UserRoot + sudoUser;

        Say($"[T3] 计划（DRY_RUN={(_dry ? 1 : 0)}）：");
        Say($"  服务账号 : {GateUser}（隐藏、无 shell、非 admin）");
        Say($"  判定端程序: {AppDir}（**自包含发布**，属主 root；不读你的家目录）");
        Say($"  状态目录 : {StateRoot}（根 0755 穿行 · state/ 0700，属主 {GateUser}）");
        Say($"  守护     : {Plist}（{Label}，开机自起）");
        Say($"  允许连接 : uid {allowUid}（组 {allowGroup}）—— 套接字落在这个组，它才连得上");
        Say("");

        // ① 建账号（幂等）
        if (Capture("dscl", [".", "-read", UserRoot + GateUser]).Rc == 0)
        {
            Say($"[T3] 账号 {GateUser} 已存在 ⇒ 复用。");
        }
        else
        {
            var pick = DefaultUid;
            var used = Capture("dscl", [".", "-list", "/Users", "UniqueID"]).Text;
            while (used.Split('\n').Any(l => l.Trim().Split(' ').LastOrDefault() == pick.ToString())) { pick++; }
            Say($"[T3] 建账号 {GateUser}（uid {pick}）");
            foreach (var kv in new[] { ("RealName", "WhiteBox Gate"), ("UserShell", "/usr/bin/false"), ("UniqueID", pick.ToString()), ("PrimaryGroupID", "20"), ("NFSHomeDirectory", "/var/empty"), ("IsHidden", "1") })
            {
                if (kv.Item1 == "NFSHomeDirectory") { Run2("dscl", [".", "-create", UserRoot + GateUser, kv.Item1, kv.Item2]); continue; }
                Run2("dscl", [".", "-create", UserRoot + GateUser, kv.Item1, kv.Item2]);
            }
        }

        // ② 自包含发布（以允许的 uid，别让 root 污染 obj/bin）
        var pub = Path.Combine(Path.GetTempPath(), "wbg-publish." + Guid.NewGuid().ToString("N")[..6]);
        Run2("rm", ["-rf", "/tmp/wbg-publish.*"]);
        Directory.CreateDirectory(pub);
        Run2("chown", [allowUid, pub]);
        Run2("chmod", ["700", pub]);
        Say($"[T3] 发布自包含程序 → {pub}（以 uid {allowUid} 运行，避免 obj/bin 变成 root 所有）");
        Run2("sudo", ["-u", "#" + allowUid, "env", "HOME=" + userHome, "DOTNET_CLI_HOME=" + userHome,
            dotnet, "publish", csproj, "-c", "Release", "-r", "osx-arm64", "--self-contained", "true", "-o", pub, "--nologo", "-v", "q"], timeoutSec: 900);

        Say($"[T3] 装到 {AppDir}（属主 root ⇒ 连 {GateUser} 也改不了自己的代码）");
        Run2("rm", ["-rf", AppDir]);
        Run2("mkdir", ["-p", AppDir]);
        Run2("cp", ["-R", pub + "/.", AppDir]);
        Run2("chown", ["-R", "root:wheel", AppDir]);
        Run2("chmod", ["-R", "a+rX", AppDir]);
        Run2("rm", ["-rf", pub]);

        // ③ 目录与权限
        Say($"[T3] 目录：{StateRoot}（根 0755）+ state/（0700）");
        Run2("mkdir", ["-p", StateRoot + "/state"]);
        Run2("chown", [GateUser + ":" + allowGroup, StateRoot, StateRoot + "/state"]);
        Run2("chmod", ["755", StateRoot]);
        Run2("chmod", ["700", StateRoot + "/state"]);

        // ④ launchd
        Say($"[T3] 写 {Plist}");
        var plistBody = PlistBody(AppDir, StateRoot, allowUid, allowGroup);
        if (_dry)
        {
            Say($"  [dry-run] 写入上面那份 plist（{Encoding.UTF8.GetByteCount(plistBody)} 字节）");
        }
        else
        {
            File.WriteAllText(Plist, plistBody + "\n", new UTF8Encoding(false));
            Run2("chown", ["root:wheel", Plist]);
            Run2("chmod", ["644", Plist]);
        }

        // ⑤ 起服务 + 自检
        Say("[T3] 起服务");
        Run2("launchctl", ["bootout", "system", Plist]);
        Run2("launchctl", ["bootstrap", "system", Plist]);
        Run2("launchctl", ["kickstart", "-k", "system/" + Label]);

        Say("[T3] 等判定端**真的能连上**（最多 60 秒）");
        if (_dry)
        {
            Say("  [dry-run] 跳过等待");
        }
        else
        {
            var cli = Path.Combine(AppDir, "AgentRuntime.Cli");
            for (var i = 0; i < 120; i++)
            {
                var r = Capture("sudo", ["-u", "#" + allowUid, cli, "--gate-status", "--state-dir", StateRoot]);
                if (r.Text.Contains("✅ 在")) { break; }
                Thread.Sleep(500);
            }
        }

        Say("[T3] 自检：");
        if (_dry)
        {
            Say($"  [dry-run] 跳过（真装后会用 uid {allowUid} 跑一次 --gate-status）");
        }
        else
        {
            var cli = Path.Combine(AppDir, "AgentRuntime.Cli");
            var status = Capture("sudo", ["-u", "#" + allowUid, cli, "--gate-status", "--state-dir", StateRoot]);
            Console.WriteLine(status.Text.TrimEnd());
            var sock = Capture("ls", ["-l", StateRoot + "/gate.sock"]).Text.Trim();
            Say($"  socket: {(sock.Length > 0 ? sock : "（还没出现，看 gate.log）")}");
            Say($"  （期望：属主 {GateUser} · 组 {allowGroup} · 权限 srw-rw---- 0660）");
            Say($"  state : {Capture("ls", ["-ld", StateRoot + "/state"]).Text.Trim()}");
            Say($"  日志  : {Capture("tail", ["-3", StateRoot + "/gate.log"]).Text.TrimEnd()}");
        }

        Say("");
        Say($"[T3] 完成。Agent 侧启动时设 WB_GATE_SOCKET={StateRoot}/gate.sock 即走判定端（不设则退回本地模式）。");
        Say("[T3] 日常：签发/列表都走 socket，不需要 sudo。卸载：sudo wb-cli install-t3 --uninstall");
        return 0;
    }

    private static string PlistBody(string appDir, string stateRoot, string allowUid, string allowGroup) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{Label}</string>
          <key>UserName</key><string>{GateUser}</string>
          <key>GroupName</key><string>{allowGroup}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{appDir}/AgentRuntime.Cli</string>
            <string>--gate-serve</string>
            <string>--state-dir</string><string>{stateRoot}</string>
            <string>--allow-uid</string><string>{allowUid}</string>
          </array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><true/>
          <key>StandardOutPath</key><string>{stateRoot}/gate.log</string>
          <key>StandardErrorPath</key><string>{stateRoot}/gate.log</string>
        </dict>
        </plist>
        """;

    private static void Say(string s) => Console.WriteLine(s);

    private static void Run2(string file, string[] args, int timeoutSec = 120)
    {
        if (_dry)
        {
            Say("  [dry-run] " + file + " " + string.Join(' ', args));
            return;
        }

        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var x in args) { psi.ArgumentList.Add(x); }
        try
        {
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  (忽略失败) {file}: {ex.Message}");
        }
    }

    private static (int Rc, string Text) Capture(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in args) { psi.ArgumentList.Add(x); }
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return (p.ExitCode, o + e);
        }
        catch
        {
            return (1, "");
        }
    }

    private static string? Which(string name) => Capture("sh", ["-c", "command -v " + name]).Text.Trim() is { Length: > 0 } s ? s : null;

    private static string Uid() => Capture("id", ["-u"]).Text.Trim();

    private static string UserName() => Environment.UserName;
}

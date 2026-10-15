using System;
using System.IO;
using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Tests;

/// <summary>
/// **按派生规则取「master 仍持有的需批工具」并造一个无害调用**（§十·32：名字由枚举派生，别写死字面量）。
/// <para>为什么要有它：master 的兜底工具面正在**逐层剥离**（read → list → exec → write → edit）——
/// 写死工具名的测试每剥一把就得改一次，且漏改会红在别处；这里让测试跟着**当前的面**走。</para>
/// <para>判据不变：它仍是一个**需批**动作（测「留档 / 可见性」要的就是「本该问人」的那一类）。</para>
/// </summary>
internal static class ToolFaceProbe
{
    /// <summary>master 面上第一个**需批**工具（剥到没有时抛错 —— 提示这层测试已无处可挂，要换面重写）。</summary>
    public static ITool FirstMutatingOnMasterFace()
    {
        foreach (var t in ToolSet.For(ToolNames.Role.Master))
        {
            if (t.Risk != ToolRisk.ReadOnly)
            {
                return t;
            }
        }

        throw new InvalidOperationException(
            "master 面上已没有需批工具 —— 「需批动作留档可见」这层测试需要换面重写（不该静默放过）。");
    }

    /// <summary>那个工具的一个**无害**调用（行式形状；落到系统临时区，不碰真实数据、不发布任何东西）。</summary>
    public static string HarmlessCall(ITool tool)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-probe");
        Directory.CreateDirectory(dir);
        return tool.Name switch
        {
            ToolNames.Write => $"[TOOL] write\npath: {Path.Combine(dir, "probe.txt")}\ncontent: x",
            ToolNames.Edit => $"[TOOL] edit\npath: {Path.Combine(dir, "probe.txt")}\noldText: x\nnewText: y",
            ToolNames.Exec => "[TOOL] exec\ncommand: echo hi",
            _ => throw new InvalidOperationException($"没有为工具 \"{tool.Name}\" 定义无害调用（新增需批工具时在这里补一条）。"),
        };
    }

    /// <summary>一步到位：当前 master 面上的一个需批调用。</summary>
    public static string MutatingCall() => HarmlessCall(FirstMutatingOnMasterFace());

    /// <summary>master 侧被拒的调用（2026-10-10：master 已持**全量**面 ⇒ 改用**未登记的工具名**取证 fail-closed 拒绝
    /// —— 未登记的名字一律归对外类 must-ask ⇒ 默认拒；比「面里没有它」更稳的一条判据）。</summary>
    public static string MasterDeniedCall() => "[TOOL] bash\ncommand: ls";

    /// <summary>worker 侧第一个需批工具的无害调用（供 worker 侧测试保留旧不变量）。</summary>
    public static string WorkerMutatingCall()
    {
        foreach (var t in ToolSet.For(ToolNames.Role.Worker))
        {
            if (t.Risk != ToolRisk.ReadOnly)
            {
                return HarmlessCall(t);
            }
        }

        throw new InvalidOperationException("WorkerFace 上没有需批工具 —— 不该发生（WorkerFace 含 write/edit/exec）。");
    }
}
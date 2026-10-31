using AgentRuntime.Presentation;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 调色板色彩空间（2026-10-31 真机：winx64 的 P3/10-bit 面板上红蓝过饱和、黄色偏淡）。
/// <para>ANSI 24 位色没有色彩空间标记、约定 sRGB；`WB_TUI_COLORSPACE=display-p3` 时做 sRGB→P3 预补偿。</para>
/// </summary>
public sealed class ColorProfileTests
{
    [Fact]
    public void srgb模式_原样返回()
    {
        Assert.Equal((246, 196, 83), ColorProfile.Adapt(246, 196, 83, ColorProfile.ColorSpace.Srgb));
        Assert.Equal("38;2;246;196;83", ColorProfile.TrueColor("#F6C453"));
    }

    [Fact]
    public void p3模式_纯色按已知矩阵换算()
    {
        // 参考：sRGB(255,0,0) ≈ P3(234,51,35)；绿(0,255,0) ≈ (117,251,76)；蓝(0,0,255) ≈ (0,0,245)。
        var red = ColorProfile.Adapt(255, 0, 0, ColorProfile.ColorSpace.DisplayP3);
        var green = ColorProfile.Adapt(0, 255, 0, ColorProfile.ColorSpace.DisplayP3);
        var blue = ColorProfile.Adapt(0, 0, 255, ColorProfile.ColorSpace.DisplayP3);

        Assert.InRange(red.R, 232, 236);
        Assert.InRange(red.G, 49, 53);
        Assert.InRange(red.B, 33, 37);

        Assert.InRange(green.R, 115, 119);
        Assert.InRange(green.G, 249, 253);
        Assert.InRange(green.B, 74, 78);

        Assert.InRange(blue.R, 0, 2);
        Assert.InRange(blue.G, 0, 2);
        Assert.InRange(blue.B, 243, 247);
    }

    [Fact]
    public void p3模式不改变亮度排序_黄色仍是亮色()
    {
        var gold = ColorProfile.Adapt(246, 196, 83, ColorProfile.ColorSpace.DisplayP3);
        Assert.True(gold.R > gold.G && gold.G > gold.B, $"金色通道次序应保持 R>G>B，实得 {gold}");
    }

    [Fact]
    public void TrueColor按当前模式生成()
    {
        var original = ColorProfile.Mode;
        try
        {
            ColorProfile.Mode = ColorProfile.ColorSpace.Srgb;
            Assert.Equal("38;2;255;0;0", ColorProfile.TrueColor("#FF0000"));

            ColorProfile.Mode = ColorProfile.ColorSpace.DisplayP3;
            var sgr = ColorProfile.TrueColor("#FF0000");
            Assert.StartsWith("38;2;23", sgr);   // P3 坐标约 234
            Assert.NotEqual("38;2;255;0;0", sgr);
        }
        finally
        {
            ColorProfile.Mode = original;
        }
    }
}

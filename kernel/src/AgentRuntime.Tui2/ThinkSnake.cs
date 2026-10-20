namespace AgentRuntime.Tui2;

/// <summary>
/// **「远端模型正在思考」的单行贪吃蛇动画**（与 TUI1 `SplitSession.ThinkSnakeFrame` 同一算法）。
/// <para>8 个 Braille 字符 ≈ 4 个汉字宽；16×4 像素网格蛇形走位；100ms 一帧。
/// 状态栏文字本身是灰色；动画只放在这一行的最后。</para>
/// </summary>
public static class ThinkSnake
{
    public const int IntervalMs = 100;

    private const int Cells = 8;
    private const int DotRows = 4;
    private const int TailPixels = 12;

    private static readonly (int X, int Y)[] Path = BuildPath();

    private static (int X, int Y)[] BuildPath()
    {
        const int columns = Cells * 2;
        var raw = new List<(int X, int Y)>();
        for (var x = 0; x < columns; x++)
        {
            if (x % 2 == 0)
            {
                for (var y = DotRows - 1; y >= 0; y--) raw.Add((x, y));
            }
            else
            {
                for (var y = 0; y < DotRows; y++) raw.Add((x, y));
            }
        }

        for (var x = columns - 1; x >= 0; x--)
        {
            if (x % 2 == 1)
            {
                for (var y = DotRows - 1; y >= 0; y--) raw.Add((x, y));
            }
            else
            {
                for (var y = 0; y < DotRows; y++) raw.Add((x, y));
            }
        }

        var path = new List<(int X, int Y)>(raw.Count);
        foreach (var cell in raw)
        {
            if (path.Count == 0 || path[^1] != cell) path.Add(cell);
        }

        if (path.Count > 1 && path[0] == path[^1]) path.RemoveAt(path.Count - 1);
        return [.. path];
    }

    /// <summary>按时间取一帧（纯函数；长度恒为 8）。</summary>
    public static string Frame(long tickMs)
    {
        var head = (int)(tickMs / IntervalMs % Path.Length);
        var builder = new System.Text.StringBuilder(Cells);
        for (var cell = 0; cell < Cells; cell++)
        {
            var mask = 0;
            for (var dx = 0; dx < 2; dx++)
            {
                for (var y = 0; y < DotRows; y++)
                {
                    var on = false;
                    for (var distance = 0; distance < TailPixels; distance++)
                    {
                        var index = (head - distance + Path.Length) % Path.Length;
                        if (Path[index] == (cell * 2 + dx, y))
                        {
                            on = true;
                            break;
                        }
                    }

                    if (on) mask |= DotBit(dx, y);
                }
            }

            builder.Append(mask == 0 ? ' ' : (char)(0x2800 + mask));
        }

        return builder.ToString();
    }

    private static int DotBit(int column, int row) => column == 0
        ? row switch { 0 => 1, 1 => 2, 2 => 4, _ => 64 }
        : row switch { 0 => 8, 1 => 16, 2 => 32, _ => 128 };
}

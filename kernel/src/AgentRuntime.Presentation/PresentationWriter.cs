using System.Text;

namespace AgentRuntime.Presentation;

/// <summary>
/// **呈现层转发器（把它套在 <see cref="Console.Out"/> 上）** —— 逐行过一遍呈现层：
/// 标记被吃掉、角色上色（不上色时就是纯文本）。
/// <para>
/// 为什么值得有它：宿主的打印点常常上百处（CLI 106 处）。**一处包住 stdout**，就不必逐个改打印点，
/// 也不会出现「这行走了呈现层、那行没走」的口径分裂。
/// </para>
/// <para>
/// 口径：**按行缓冲**（遇到 <c>\n</c> 才整流渲染）；<see cref="Flush"/> 时把没换行的尾巴也渲染出去
/// （REPL 的提示符没有换行 —— 不冲掉它，人看不见提示）。
/// </para>
/// </summary>
public sealed class PresentationWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly Presenter _presenter;
    private readonly StringBuilder _buffer = new();

    public PresentationWriter(TextWriter inner, Presenter presenter)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public override Encoding Encoding => _inner.Encoding;

    /// <summary>本转发器用的呈现门面（宿主可读它拿 <c>Describe()</c> 之类）。</summary>
    public Presenter Presenter => _presenter;

    public override void Write(char value)
    {
        switch (value)
        {
            case '\n':
                FlushLine();
                _inner.Write('\n');
                return;
            case '\r':
                return;                     // CRLF：\r 丢掉（Unix 上多余）
            default:
                _buffer.Append(value);
                return;
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var c in value)
        {
            Write(c);
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        FlushLine();
        _inner.Flush();
    }

    private void FlushLine()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var line = _buffer.ToString();
        _buffer.Clear();
        _inner.Write(_presenter.Style.Render(_presenter.Rich(line)));
    }
}

using System.Text;

namespace LakeCode.Eval.Worker.Output;

internal sealed class BoundedOutput : TextWriter
{
    private readonly StringBuilder _text = new();
    private readonly object _sync = new();
    private bool _truncated;
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value)
    {
        lock (_sync)
        {
            if (_text.Length < 32000) _text.Append(value);
            else _truncated = true;
        }
    }
    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_sync)
        {
            var count = Math.Min(32000 - _text.Length, value.Length);
            _text.Append(value.AsSpan(0, count));
            if (count < value.Length) _truncated = true;
        }
    }
    public void MarkTruncated() { lock (_sync) _truncated = true; }
    public void Clear() { lock (_sync) { _text.Clear(); _truncated = false; } }
    public override string ToString()
    {
        lock (_sync) return _text + (_truncated ? "\n[Output truncated; limit is 32000 characters.]" : "");
    }
}

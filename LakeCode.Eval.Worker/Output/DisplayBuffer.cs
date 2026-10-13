namespace LakeCode.Eval.Worker.Output;

internal sealed class DisplayLimitException : Exception;
internal sealed class DisplayBuffer : MemoryStream
{
    private const int Limit = 128000;
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _ = buffer.AsSpan(offset, count); // Validate the range before writing anything.
        var remaining = Limit - (int)Length;
        base.Write(buffer, offset, Math.Min(remaining, count));
        if (count > remaining) throw new DisplayLimitException();
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var remaining = Limit - (int)Length;
        var bytes = buffer[..Math.Min(remaining, buffer.Length)].ToArray();
        // MemoryStream's span overload can dispatch back to our byte[] override.
        // Call the byte[] base implementation directly to avoid recursive dispatch.
        base.Write(bytes, 0, bytes.Length);
        if (buffer.Length > remaining) throw new DisplayLimitException();
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LakeCode.Eval.Worker.Output;

public sealed class EvalGlobals(ToolProxy tools, TextWriter output)
{
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new TupleDisplayConverter() }
    };
    public ToolProxy Tools { get; } = tools;
    public void Display(object? value)
    {
        if (value is string text) { output.WriteLine(text); return; }
        using var buffer = new DisplayBuffer();
        try
        {
            using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = DisplayOptions.Encoder
            });
            JsonSerializer.Serialize(json, value, DisplayOptions);
        }
        catch (DisplayLimitException)
        {
            output.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            output.WriteLine("[Display truncated during serialization.]");
            if (output is BoundedOutput bounded) bounded.MarkTruncated();
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Display serialization failed: {error.Message}", error);
        }
        output.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }
}

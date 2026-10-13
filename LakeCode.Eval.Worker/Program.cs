using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LakeCode.Eval.Contracts;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(15000);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
var output = new BoundedOutput();
Console.SetOut(output);
Console.SetError(output);
Console.SetIn(TextReader.Null);
var globals = new EvalGlobals(new ToolProxy(reader, writer), output);
var options = ScriptOptions.Default
    .AddReferences(typeof(JsonSerializer).Assembly, typeof(EvalGlobals).Assembly,
        typeof(Enumerable).Assembly, typeof(System.Text.Json.Nodes.JsonNode).Assembly)
    .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic",
        "System.Threading.Tasks", "System.Text.Json", "System.Text.Json.Nodes");
ScriptState<object>? state = null;
var generation = Guid.NewGuid().ToString("N");
long cell = 0;
while (await reader.ReadLineAsync() is { } line)
{
    var message = JsonSerializer.Deserialize<EvalMessage>(line)!;
    if (message.Kind != "execute") throw new InvalidDataException("Expected execute.");
    output.Clear();
    cell++;
    string? errorKind = null;
    var stateStatus = "preserved";
    try
    {
        // Capture runtime exceptions in ScriptState so the executed submission remains
        // in the continuation chain, including declarations made before the exception.
        state = state is null
            ? await CSharpScript.Create<object>(message.Code!, options, typeof(EvalGlobals))
                .RunAsync(globals, catchException: _ => true)
            : await state.ContinueWithAsync(message.Code!, options, catchException: _ => true);
        if (state.Exception is { } runtimeError)
        {
            errorKind = "runtime";
            stateStatus = "partially-mutated";
            output.WriteLine($"{runtimeError.GetType().Name}: {runtimeError.Message}");
        }
        else if (state.ReturnValue is { } value) globals.Display(value);
    }
    catch (CompilationErrorException error)
    {
        errorKind = "compilation";
        output.WriteLine(string.Join(Environment.NewLine, error.Diagnostics));
    }
    catch (Exception error)
    {
        errorKind = "runtime";
        stateStatus = "partially-mutated";
        output.WriteLine($"{error.GetType().Name}: {error.Message}");
    }
    await writer.WriteLineAsync(JsonSerializer.Serialize(new EvalMessage("result", Output: output.ToString(),
        IsError: errorKind is not null, ErrorKind: errorKind, StateStatus: stateStatus,
        Generation: generation, Cell: cell)));
}

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

internal sealed class TupleDisplayConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ITuple).IsAssignableFrom(typeToConvert);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TupleConverter<>).MakeGenericType(typeToConvert))!;
    private sealed class TupleConverter<T> : JsonConverter<T>
    {
        public TupleConverter() { }
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Display converter is write-only.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var tuple = (ITuple)value!;
            writer.WriteStartArray();
            for (int i = 0; i < tuple.Length; i++) JsonSerializer.Serialize(writer, tuple[i], options);
            writer.WriteEndArray();
        }
    }
}

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

public sealed class ToolProxy(StreamReader reader, StreamWriter writer)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<string> CallAsync(string name, object? arguments = null)
    {
        var result = await TryCallAsync(name, arguments);
        if (result.IsError) throw new InvalidOperationException(result.Output);
        return result.Output;
    }
    public Task<EvalToolResult> TryCallAsync(string name, object? arguments = null) =>
        ExchangeAsync(new EvalMessage("tool", Name: name,
            Arguments: JsonSerializer.SerializeToElement(arguments ?? new { })));
    public async Task<string> ListToolsAsync()
    {
        var result = await ExchangeAsync(new EvalMessage("catalog"));
        if (result.IsError) throw new InvalidOperationException(result.Output);
        return result.Output;
    }
    private async Task<EvalToolResult> ExchangeAsync(EvalMessage message)
    {
        await _gate.WaitAsync();
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message));
            var line = await reader.ReadLineAsync() ?? throw new EndOfStreamException("Host disconnected.");
            var reply = JsonSerializer.Deserialize<EvalMessage>(line)!;
            if (reply.Kind != "tool_result") throw new InvalidDataException("Expected tool_result.");
            return new EvalToolResult(reply.Output ?? "", reply.IsError);
        }
        finally { _gate.Release(); }
    }
    public Task<string> ReadFileAsync(string path, int startLine = 1, int lineCount = 0) =>
        CallAsync("read_file", new { path, startLine, lineCount });
    public Task<string> ListDirectoryAsync(string path = ".", int offset = 0) =>
        CallAsync("list_directory", new { path, offset });
}

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

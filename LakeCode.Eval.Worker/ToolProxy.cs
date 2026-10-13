using System.Text.Json;
using LakeCode.Eval.Contracts;

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

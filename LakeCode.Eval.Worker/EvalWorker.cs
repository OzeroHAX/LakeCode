using System.IO.Pipes;
using System.Text.Json;
using LakeCode.Eval.Contracts;
using LakeCode.Eval.Worker.Output;

namespace LakeCode.Eval.Worker;

internal static class EvalWorker
{
    public static async Task RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var output = new BoundedOutput();
        Console.SetOut(output);
        Console.SetError(output);
        Console.SetIn(TextReader.Null);
        var globals = new EvalGlobals(new ToolProxy(reader, writer), output);
        var session = new EvalSession(globals, output);

        while (await reader.ReadLineAsync() is { } line)
        {
            var message = JsonSerializer.Deserialize<EvalMessage>(line)!;
            if (message.Kind != "execute") throw new InvalidDataException("Expected execute.");
            var result = await session.ExecuteAsync(message.Code!);
            await writer.WriteLineAsync(JsonSerializer.Serialize(result));
        }
    }
}

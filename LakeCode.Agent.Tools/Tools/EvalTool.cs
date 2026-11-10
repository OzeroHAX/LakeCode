using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LakeCode.Eval.Contracts;

namespace LakeCode.Agent.Tools;

/// <summary>
/// Executes one C# script in a persistent worker process over a named pipe. The tool owns the worker
/// lifetime and forwards bridge calls to a host-supplied invoker; it does not decide whether eval is
/// permitted in the current mode, which is a hook decision.
/// </summary>
public sealed class EvalTool(
    string root,
    Func<string, JsonElement, CancellationToken, Task<string>> invokeTool,
    Func<string> describeTools) : AgentTool, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _diagnosticSync = new();
    private readonly System.Text.StringBuilder _diagnostics = new();
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _drainLifetime;
    private Task _stdoutDrain = Task.CompletedTask;
    private Task _stderrDrain = Task.CompletedTask;
    private bool _disposed;
    private ExecutionBudget? _budget;

    public override string Name => "eval";

    public override string Description =>
        "Execute one C# script in a persistent worker. Variables survive later calls. No security sandbox: " +
        "code has user OS permissions. Repository retrieval must use the file tools through the tool bridge. " +
        "Use Display(value), Console.WriteLine, and await Tools.CallAsync(toolName, new { arguments }). " +
        "Errors distinguish compilation, runtime and worker failure. Output is limited to 32000 characters.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ExecutesProcess);

    public override JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("code", "C# script; top-level await is supported.")
        .Text("action", "What the script does.")
        .Text("purpose", "Why the script is useful for the current task.")
        .Text("whyNeededNow", "Why this script is needed now.")
        .Text("alternativesConsidered", "Why a safer or narrower alternative was not chosen.")
        .Text("expectedResult", "Concrete result expected from the script.")
        .Text("sideEffects", "Relevant files, network, data, processes, worker state, and persistence side effects, or none.")
        .Integer("timeout", "Execution timeout in seconds; 1-3600, default 30. Timeout destroys worker state.",
            minimum: 1, maximum: 3600, defaultValue: 30)
        .Boolean("reset", "Restart the C# worker before execution, discarding previous variables.", false));

    /// <summary>Classifier and human approval waits do not consume the execution budget.</summary>
    internal IDisposable? PauseTimeout() => _budget?.Pause();

    public void Reset()
    {
        _gate.Wait();

        try
        {
            StopWorker();
            CloseChannel();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        StopWorker();
        // Active execution owns channel cleanup until it releases the gate.
        _gate.Wait();

        try
        {
            CloseChannel();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }

    protected override async ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var code = arguments.RequireString("code", allowEmpty: true);
        RequireAll(
            arguments,
            "action",
            "purpose",
            "whyNeededNow",
            "alternativesConsidered",
            "expectedResult",
            "sideEffects");
        var timeout = arguments.Integer("timeout", 30);
        var reset = arguments.Boolean("reset", false);

        if (string.IsNullOrWhiteSpace(code))
        {
            return Failure("code must not be empty.");
        }

        if (timeout is < 1 or > 3600)
        {
            return Failure("timeout must be between 1 and 3600 seconds.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
            _budget = new ExecutionBudget(deadline, TimeSpan.FromSeconds(timeout));
            using var stop = deadline.Token.Register(StopWorker);
            var executionSent = false;

            try
            {
                deadline.Token.ThrowIfCancellationRequested();

                if (reset)
                {
                    StopWorker();
                    CloseChannel();
                }

                await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);
                executionSent = true;
                await _writer!
                    .WriteLineAsync(JsonSerializer.Serialize(new EvalMessage("execute", Code: code)).AsMemory(), deadline.Token)
                    .ConfigureAwait(false);

                while (true)
                {
                    var line = await _reader!.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("C# worker exited before returning a result.");

                    var message = JsonSerializer.Deserialize<EvalMessage>(line)
                        ?? throw new InvalidDataException("Empty worker message.");

                    if (message.Kind == "result")
                    {
                        var output = string.IsNullOrEmpty(message.Output) ? "(no output)" : message.Output;

                        return message.IsError
                            ? new AgentToolResult(
                                true,
                                $"Error: {message.ErrorKind}; state={message.StateStatus}; worker={message.Generation}; " +
                                $"cell={message.Cell}\n{output}")
                            : new AgentToolResult(false, output);
                    }

                    if (message.Kind == "catalog")
                    {
                        await _writer
                            .WriteLineAsync(
                                JsonSerializer.Serialize(new EvalMessage("tool_result", Output: describeTools())).AsMemory(),
                                deadline.Token)
                            .ConfigureAwait(false);

                        continue;
                    }

                    if (message.Kind != "tool")
                    {
                        throw new InvalidDataException("Unexpected worker message.");
                    }

                    string result;
                    bool failed;

                    try
                    {
                        result = await invokeTool(
                                message.Name!,
                                message.Arguments ?? JsonSerializer.SerializeToElement(new { }),
                                deadline.Token)
                            .ConfigureAwait(false);

                        failed = result.StartsWith("Error:", StringComparison.Ordinal);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        result = "Error: " + error.Message;
                        failed = true;
                    }

                    await _writer
                        .WriteLineAsync(
                            JsonSerializer.Serialize(new EvalMessage("tool_result", Output: result, IsError: failed)).AsMemory(),
                            deadline.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
                StopWorker();
                CloseChannel();

                if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "C# execution cancelled; worker state was discarded.",
                        cancellationToken);
                }

                return new AgentToolResult(
                    true,
                    $"Error: timeout after {timeout} seconds; state=discarded. All previous variables are gone. " +
                    $"Tool side effects may have occurred.{DiagnosticTail()}");
            }
            catch (Exception error)
            {
                StopWorker();
                CloseChannel();

                return new AgentToolResult(
                    true,
                    $"Error: {(executionSent ? "transport" : "startup")}; state=discarded. {error.Message}\n" +
                    $"C# worker state was discarded. " +
                    $"{(executionSent ? "Tool side effects may have occurred." : "Script was not sent to the worker.")}" +
                    DiagnosticTail());
            }
        }
        finally
        {
            _budget = null;
            _gate.Release();
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _pipe is { IsConnected: true })
        {
            return;
        }

        StopWorker();
        CloseChannel();

        var assembly = Path.Combine(AppContext.BaseDirectory, "eval-worker", "LakeCode.Eval.Worker.dll");

        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("C# worker was not deployed alongside LakeCode.", assembly);
        }

        var name = "lakecode-eval-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var appHost = Path.Combine(
            Path.GetDirectoryName(assembly)!,
            OperatingSystem.IsWindows() ? "LakeCode.Eval.Worker.exe" : "LakeCode.Eval.Worker");

        var hasAppHost = File.Exists(appHost);

        var start = new ProcessStartInfo(hasAppHost ? appHost : "dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!hasAppHost)
        {
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add(name);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_diagnosticSync)
        {
            _diagnostics.Clear();
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start C# worker.");
        _process.StandardInput.Close();
        _drainLifetime = new CancellationTokenSource();
        _stdoutDrain = DrainAsync(_process.StandardOutput, "stdout", _drainLifetime.Token);
        _stderrDrain = DrainAsync(_process.StandardError, "stderr", _drainLifetime.Token);

        if (cancellationToken.IsCancellationRequested)
        {
            StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };
    }

    private void StopWorker()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CloseChannel()
    {
        // Dispose the writer before the reader closes the shared pipe.
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _reader?.Dispose();
        _pipe?.Dispose();

        // Cancel and join readers before releasing resources. Do not wait for EOF:
        // descendants could retain inherited stream handles.
        _drainLifetime?.Cancel();
        Task.WhenAll(_stdoutDrain, _stderrDrain).GetAwaiter().GetResult();
        _drainLifetime?.Dispose();
        _drainLifetime = null;
        _stdoutDrain = Task.CompletedTask;
        _stderrDrain = Task.CompletedTask;
        _process?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
        _process = null;
    }

    private async Task DrainAsync(StreamReader reader, string source, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];

        try
        {
            int count;

            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                lock (_diagnosticSync)
                {
                    _diagnostics.Append('[').Append(source).Append("] ").Append(buffer, 0, count);

                    if (_diagnostics.Length > 4000)
                    {
                        _diagnostics.Remove(0, _diagnostics.Length - 4000);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException error)
        {
            lock (_diagnosticSync)
            {
                _diagnostics.Append($"\n[{source} read failed: {error.Message}]");

                if (_diagnostics.Length > 4000)
                {
                    _diagnostics.Remove(0, _diagnostics.Length - 4000);
                }
            }
        }
    }

    private string DiagnosticTail()
    {
        lock (_diagnosticSync)
        {
            return _diagnostics.Length == 0
                ? string.Empty
                : "\nWorker diagnostics (last 4000 characters):\n" + _diagnostics;
        }
    }

    private sealed class ExecutionBudget(CancellationTokenSource source, TimeSpan duration)
    {
        private readonly CancellationTokenSource _deadline = source;
        private TimeSpan _remaining = duration;
        private long _started = Stopwatch.GetTimestamp();

        public IDisposable Pause()
        {
            _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            _remaining -= Stopwatch.GetElapsedTime(_started);
            return new Resume(this);
        }

        private sealed class Resume(ExecutionBudget budget) : IDisposable
        {
            public void Dispose()
            {
                budget._started = Stopwatch.GetTimestamp();
                budget._deadline.CancelAfter(
                    budget._remaining > TimeSpan.Zero ? budget._remaining : TimeSpan.Zero);
            }
        }
    }
}

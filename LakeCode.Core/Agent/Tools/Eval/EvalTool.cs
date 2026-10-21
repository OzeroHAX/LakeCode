using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LakeCode.Eval.Contracts;

namespace LakeCode.Core.Agent.Tools.Eval;

internal sealed class EvalTool(string root, Func<string, JsonElement, CancellationToken, Task<string>> invokeTool,
    Func<string> describeTools) : IDisposable
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(RunAsync, name: "eval");

    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;
    private ExecutionBudget? _budget;

    // Classifier and human approval waits do not consume the execution budget.
    internal IDisposable? PauseTimeout() => _budget?.Pause();

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
                budget._deadline.CancelAfter(budget._remaining > TimeSpan.Zero ? budget._remaining : TimeSpan.Zero);
            }
        }
    }
    private CancellationTokenSource? _drainLifetime;
    private Task _stdoutDrain = Task.CompletedTask;
    private Task _stderrDrain = Task.CompletedTask;
    private readonly object _diagnosticSync = new();
    private readonly System.Text.StringBuilder _diagnostics = new();

    [Description("Execute one C# script in a persistent worker. Variables survive later calls. No security sandbox: code has user OS permissions. Use Display(value), Console.WriteLine, and await Tools.CallAsync(toolName, new { arguments }); ReadFileAsync and ListDirectoryAsync shortcuts are available. Tools.ListToolsAsync() returns tool names, descriptions and schemas as JSON. Tools.TryCallAsync(name, arguments) returns Output/IsError without throwing for tool errors. Bridge calls are serialized, even with Task.WhenAll. Tool results are strings; read_file includes line numbers. Display prints readable JSON and tuples as arrays. Errors distinguish compilation (state preserved), runtime (state may be partially mutated), and worker failure (state discarded). Calls must be awaited before the cell ends; do not leave background work. Tools cannot call eval recursively. Runtime state is not restored from saved history. Output is limited to 32000 characters.")]
    public async Task<string> RunAsync(
        [Description("C# script; top-level await is supported. Default imports: System, System.IO, System.Linq, System.Collections.Generic, System.Threading.Tasks, System.Text.Json and System.Text.Json.Nodes.")] string code,
        [Description("Required: explain the action, its purpose for this task, and expected side effects (files, network, data). Shown to the user before execution.")] string description,
        [Description("Execution timeout in seconds, including tool execution and worker startup, excluding classifier and manual execution-approval waits; 1–3600, default 30. Timeout destroys worker state.")] int timeout = 30,
        [Description("Restart the C# worker before execution, discarding previous variables.")] bool reset = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Error: description is required.";
        if (string.IsNullOrWhiteSpace(code)) return "Error: code must not be empty.";
        if (timeout is < 1 or > 3600) return "Error: timeout must be between 1 and 3600 seconds.";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
            _budget = new ExecutionBudget(deadline, TimeSpan.FromSeconds(timeout));
            using var stop = deadline.Token.Register(StopWorker);
            bool executionSent = false;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (reset) { StopWorker(); CloseChannel(); }
                await EnsureWorkerAsync(deadline.Token);
                executionSent = true;
                await _writer!.WriteLineAsync(JsonSerializer.Serialize(new EvalMessage("execute", Code: code)).AsMemory(), deadline.Token);
                while (true)
                {
                    var line = await _reader!.ReadLineAsync(deadline.Token)
                        ?? throw new EndOfStreamException("C# worker exited before returning a result.");
                    var message = JsonSerializer.Deserialize<EvalMessage>(line)
                        ?? throw new InvalidDataException("Empty worker message.");
                    if (message.Kind == "result")
                    {
                        var output = string.IsNullOrEmpty(message.Output) ? "(no output)" : message.Output;
                        return message.IsError
                            ? $"Error: {message.ErrorKind}; state={message.StateStatus}; worker={message.Generation}; cell={message.Cell}\n{output}"
                            : output;
                    }
                    if (message.Kind == "catalog")
                    {
                        await _writer.WriteLineAsync(JsonSerializer.Serialize(new EvalMessage("tool_result", Output: describeTools())).AsMemory(), deadline.Token);
                        continue;
                    }
                    if (message.Kind != "tool") throw new InvalidDataException("Unexpected worker message.");
                    string result;
                    bool failed;
                    try
                    {
                        result = await invokeTool(message.Name!, message.Arguments ?? JsonSerializer.SerializeToElement(new { }), deadline.Token);
                        failed = result.StartsWith("Error:", StringComparison.Ordinal);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { result = "Error: " + error.Message; failed = true; }
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(new EvalMessage("tool_result", Output: result, IsError: failed)).AsMemory(), deadline.Token);
                }
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
                StopWorker();
                CloseChannel();
                if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) throw new OperationCanceledException("C# execution cancelled; worker state was discarded.", cancellationToken);
                return $"Error: timeout after {timeout} seconds; state=discarded. All previous variables are gone. Tool side effects may have occurred.{DiagnosticTail()}";
            }
            catch (Exception error)
            {
                StopWorker();
                CloseChannel();
                return $"Error: {(executionSent ? "transport" : "startup")}; state=discarded. {error.Message}\n" +
                    $"C# worker state was discarded. {(executionSent ? "Tool side effects may have occurred." : "Script was not sent to the worker.")}{DiagnosticTail()}";
            }
        }
        finally { _budget = null; _gate.Release(); }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _pipe is { IsConnected: true }) return;
        StopWorker();
        CloseChannel();
        var assembly = Path.Combine(AppContext.BaseDirectory, "eval-worker", "LakeCode.Eval.Worker.dll");
        if (!File.Exists(assembly)) throw new FileNotFoundException("C# worker was not deployed alongside LakeCode.", assembly);
        var name = "lakecode-eval-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var appHost = Path.Combine(Path.GetDirectoryName(assembly)!,
            OperatingSystem.IsWindows() ? "LakeCode.Eval.Worker.exe" : "LakeCode.Eval.Worker");
        var hasAppHost = File.Exists(appHost);
        var start = new ProcessStartInfo(hasAppHost ? appHost : "dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!hasAppHost) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_diagnosticSync) _diagnostics.Clear();
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
        await _pipe.WaitForConnectionAsync(cancellationToken);
        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };
    }

    private void StopWorker()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private void CloseChannel()
    {
        // Dispose the writer before the reader closes the shared pipe.
        try { _writer?.Dispose(); } catch (IOException) { }
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
                    if (_diagnostics.Length > 4000) _diagnostics.Remove(0, _diagnostics.Length - 4000);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException error)
        {
            lock (_diagnosticSync)
            {
                _diagnostics.Append($"\n[{source} read failed: {error.Message}]");
                if (_diagnostics.Length > 4000) _diagnostics.Remove(0, _diagnostics.Length - 4000);
            }
        }
    }

    private string DiagnosticTail()
    {
        lock (_diagnosticSync) return _diagnostics.Length == 0 ? "" : "\nWorker diagnostics (last 4000 characters):\n" + _diagnostics;
    }

    public void Reset()
    {
        _gate.Wait();
        try { StopWorker(); CloseChannel(); } finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        StopWorker();
        // Active execution owns channel cleanup until it releases the gate.
        _gate.Wait();
        try { CloseChannel(); } finally { _gate.Release(); }
    }
}

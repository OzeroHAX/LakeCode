using System.Diagnostics;
using System.Text;

namespace LakeCode.Core.Agent.Tools.Bash;

/// <summary>Serializes the two pipes into a bounded display log; publication is batched by the caller.</summary>
internal sealed class BashOutputCapture(Action<BashExecution> publish)
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly long _started = Stopwatch.GetTimestamp();
    private DateTimeOffset? _lastOutputAt;
    private bool _dirty = true;
    private bool _truncated;

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _lastOutputAt = DateTimeOffset.UtcNow;
            var retained = Math.Min(text.Length, WorkspaceFiles.MaxOutputCharacters - _output.Length);
            _output.Append(text[..retained]);
            _truncated |= retained < text.Length;
            _dirty = true;
        }
    }

    public void Publish(bool force = false, int? exitCode = null)
    {
        lock (_gate)
        {
            if (!_dirty && !force) return;
            _dirty = false;
            publish(new BashExecution(_startedAt, _lastOutputAt, _output.ToString(), _truncated,
                Stopwatch.GetElapsedTime(_started).TotalSeconds, exitCode));
        }
    }

    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Publish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

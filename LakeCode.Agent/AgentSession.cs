using System.Collections.Immutable;

namespace LakeCode.Agent;

/// <summary>
/// One long-lived conversation. Owns history, sequential run discipline, cancellation and the current
/// snapshot; delegates the actual loop to <see cref="AgentRunner"/>.
/// Publishing is serialized and isolated: a failing subscriber cannot break the run, and events stay
/// ordered relative to state changes.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AgentRunner _runner;
    private readonly AgentHooks _hooks;
    private readonly AgentSessionOptions _options;
    private readonly AgentHistory _history;
    private readonly List<AgentEvent> _events = [];
    private CancellationTokenSource? _activeRun;
    private Task<AgentRunResult> _run = Task.FromResult(
        new AgentRunResult(AgentStatus.Idle, string.Empty, null, 0));
    private long _revision;
    private string? _runId;
    private string? _error;
    private AgentStatus _status = AgentStatus.Idle;
    private bool _disposed;

    public AgentSession(
        IAgentModel model,
        IEnumerable<IAgentTool> tools,
        AgentHooks? hooks = null,
        AgentSessionOptions? options = null,
        AgentHistory? history = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);

        _hooks = hooks ?? AgentHooks.None;
        _options = options ?? AgentSessionOptions.Default;
        _history = history ?? new AgentHistory();
        _runner = new AgentRunner(model, tools, _hooks, _options.MaxSteps);
    }

    /// <summary>Raised for every event, in order. Exceptions are isolated and never reach the run.</summary>
    public event Action<AgentEvent>? EventOccurred;

    public AgentHistory History => _history;

    public AgentState State
    {
        get
        {
            lock (_gate)
            {
                return CreateState();
            }
        }
    }

    /// <summary>Snapshot of the events published during this session instance.</summary>
    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public bool CanRun
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _activeRun is null;
            }
        }
    }

    /// <summary>
    /// Sends one user message and waits for the run to finish. Rejected while another run is active
    /// or after disposal; the caller decides whether to queue, retry or surface that to the user.
    /// </summary>
    public Task<AgentRunResult> RunAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        CancellationTokenSource generation;
        string runId;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_activeRun is not null)
            {
                throw new InvalidOperationException(
                    "Another run is active in this session. Wait for it to finish or cancel it.");
            }

            generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runId = AgentIds.Create();
            _activeRun = generation;
            _runId = runId;
            _error = null;
            _status = AgentStatus.Running;
            _revision++;
        }

        var run = ExecuteAsync(runId, prompt, generation);

        lock (_gate)
        {
            _run = run;
        }

        return run;
    }

    /// <summary>Requests cancellation of the active run. Completed side effects are not undone.</summary>
    public void Cancel()
    {
        CancellationTokenSource? generation;

        lock (_gate)
        {
            generation = _activeRun;
        }

        try
        {
            generation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its source between the check and the request.
        }
    }

    /// <summary>Waits for the active run, if any, to finish.</summary>
    public Task WaitAsync() => Volatile.Read(ref _run);

    /// <summary>Restorable payload for persistence: history plus the operational status.</summary>
    public AgentSessionSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return new AgentSessionSnapshot(_history.CreateSnapshot(), _status, _error);
        }
    }

    public void Rewind(int messageCount)
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Cannot rewind while a run is active.");
            }

            _history.Rewind(messageCount);
            _revision++;
        }
    }

    public bool RewindToToolCall(string toolCallId)
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Cannot rewind while a run is active.");
            }

            if (!_history.TryRewindToToolCall(toolCallId))
            {
                return false;
            }

            _revision++;
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? generation;
        Task run;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            generation = _activeRun;
            run = _run;
        }

        generation?.Cancel();

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal only waits for the run to observe cancellation; its outcome is already reported.
        }
        finally
        {
            generation?.Dispose();
        }
    }

    private async Task<AgentRunResult> ExecuteAsync(
        string runId,
        string prompt,
        CancellationTokenSource generation)
    {
        try
        {
            return await _runner.ExecuteAsync(_history, prompt, runId, Publish, generation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeRun, generation))
                {
                    _activeRun = null;
                }
            }

            generation.Dispose();
        }
    }

    private void Publish(AgentEvent agentEvent)
    {
        Action<AgentEvent>? handler;

        lock (_gate)
        {
            _events.Add(agentEvent);

            switch (agentEvent)
            {
                case AgentStarted started:
                    _runId = started.RunId;
                    _status = AgentStatus.Running;
                    break;

                case AgentCompleted:
                    _status = AgentStatus.Completed;
                    _error = null;
                    break;

                case AgentFailed failed:
                    _status = AgentStatus.Failed;
                    _error = failed.Error;
                    break;

                case AgentCanceled:
                    _status = AgentStatus.Canceled;
                    break;

                case AgentStopped stopped:
                    _status = AgentStatus.Stopped;
                    _error = stopped.Reason;
                    break;
            }

            _revision++;
            handler = EventOccurred;
        }

        handler?.Invoke(agentEvent);
    }

    private AgentState CreateState() =>
        new(_revision, _status, _activeRun is not null, _runId, _error, _history.Count);
}

/// <summary>Persistable session payload. Provider-specific and UI-specific data stay outside.</summary>
public sealed record AgentSessionSnapshot(
    AgentHistorySnapshot History,
    AgentStatus Status,
    string? Error)
{
    public static AgentSessionSnapshot Empty { get; } = new(AgentHistorySnapshot.Empty, AgentStatus.Idle, null);
}


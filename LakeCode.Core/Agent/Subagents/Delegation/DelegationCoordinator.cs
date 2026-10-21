using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LakeCode.Core.Providers;
using LakeCode.Core.Agent.Subagents;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Delegation;

/// <summary>Session-owned research jobs; child streams never re-enter the parent session.</summary>
internal sealed class DelegationCoordinator(
    Func<AskParentTool, string, SubagentRunner> createChild,
    Func<ToolCallUpdate?> getInvocation) : IDisposable, IAsyncDisposable
{
    private sealed class Job(DelegationSnapshot snapshot, ToolCallUpdate origin,
        SubagentRunner child, AskParentTool questions, ModelInfo model, string effort, ProviderProfile provider,
        CancellationTokenSource cancellation)
    {
        public DelegationSnapshot Snapshot = snapshot;
        public readonly ToolCallUpdate Origin = origin;
        public SubagentRunner? Child = child;
        public readonly AskParentTool Questions = questions;
        public readonly ModelInfo Model = model;
        public readonly string Effort = effort;
        public readonly ProviderProfile Provider = provider;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public Task Operation = Task.CompletedTask;
        public bool NotificationPending;
        public bool Settled;
        public readonly Queue<string> Messages = new();
        public bool BoundaryStop;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invocations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _operations = [];
    private TaskCompletionSource _changed = NewSignal();
    private (ModelInfo Model, string Effort, ProviderProfile Provider)? _request;
    private bool _disposed;

    public event Action<ToolCallUpdate>? Updated;
    public event Action<DelegationActivity>? Activity;
    public event Action? Ready;

    public DelegationSnapshot[] Snapshots { get { lock (_gate) return _jobs.Values.Select(job => job.Snapshot).ToArray(); } }
    public bool HasNotifications { get { lock (_gate) return _jobs.Values.Any(job => job.NotificationPending); } }

    public void Configure(ModelInfo model, string effort, ProviderProfile provider)
    { lock (_gate) _request = (model, effort, provider); }

    [Description("Start an isolated read-only research subagent in the background. Returns Running and taskId immediately; the child continues between user messages. Work independently by default. Inspect the relevant project area yourself before considering delegation. Do not delegate initial reconnaissance, ordinary edits or routine self-verification. For complex related work use at most one subagent only for a clearly useful independent investigation; multiple subagents require substantial research with genuinely independent directions. Before each launch briefly explain the independent question, your necessary parallel work and why the benefit outweighs the overhead. If in doubt, do not delegate. Respect user prohibitions; otherwise no routine user confirmation is needed. Up to four unfinished jobs is a safety ceiling, not a target; no writes, commands or nested agents. Start with task/context only. Resume NeedsParentInput with taskId/questionId/answer only. Results and questions are automatically queued to the parent; use wait_subagents when blocked on them. Do not duplicate delegated work or poll. Check returned evidence.")]
    public Task<string> DelegateAsync(
        [Description("Self-contained research task: scope, non-goals, expected evidence. Start only.")] string? task = null,
        [Description("Relevant decisions and context; the child has no parent conversation. Start only.")] string? context = null,
        [Description("Existing task ID. Resume only.")] string? taskId = null,
        [Description("Exact pending question ID. Resume only.")] string? questionId = null,
        [Description("Parent's answer. Resume only.")] string? answer = null,
        CancellationToken cancellationToken = default)
    {
        Job job;
        ChatMessage message;
        var start = NewSignal();
        DelegationSnapshot started;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var invocation = getInvocation() ?? throw new InvalidOperationException("No delegate invocation.");
            if (taskId is null)
            {
                if (string.IsNullOrWhiteSpace(task) || questionId is not null || answer is not null)
                    return Task.FromResult("Error: Start requires task and optional context only.");
                if (task.Length > 16_000 || context?.Length > 16_000)
                    return Task.FromResult("Error: task and context must each be at most 16000 characters.");
                if (_jobs.Values.Count(item => IsUnfinished(item.Snapshot)) >= 4)
                    return Task.FromResult("Error: Four research jobs are unfinished. Wait for results or answer pending questions first.");
                PruneDelivered();
                if (_jobs.Count >= 32)
                    return Task.FromResult("Error: Research result retention is full. Collect outstanding results with wait_subagents.");
                var settings = _request ?? throw new InvalidOperationException("No active parent request.");
                var questions = new AskParentTool();
                var child = createChild(questions, invocation.CallId);
                job = new Job(new DelegationSnapshot(Guid.NewGuid().ToString("N"), task, DelegationStatus.Running)
                    { Context = context, ProviderId = settings.Provider.Id, ProviderName = settings.Provider.Name,
                        ModelId = settings.Model.Id, ModelName = settings.Model.Name, ReasoningEffort = settings.Effort },
                    invocation, child, questions, settings.Model, settings.Effort, settings.Provider,
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                child.BeforeModelRequest += () =>
                {
                    lock (_gate)
                    {
                        if (job.Messages.Count == 0) return;
                        job.BoundaryStop = true;
                    }
                    throw new OperationCanceledException("Continue research with parent clarification after tools.");
                };
                child.ToolCallUpdated += update => Activity?.Invoke(new DelegationActivity(job.Snapshot.TaskId, "tool", ToolCall: update));
                child.ResponseCompleted += response =>
                {
                    foreach (var completedMessage in response.Messages)
                    {
                        foreach (var content in completedMessage.Contents)
                        {
                            if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                                Activity?.Invoke(new DelegationActivity(job.Snapshot.TaskId, "thinking", reasoning.Text));
                            else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                                Activity?.Invoke(new DelegationActivity(job.Snapshot.TaskId, "text", text.Text));
                        }
                        Activity?.Invoke(new DelegationActivity(job.Snapshot.TaskId, "message_end"));
                    }
                };
                _jobs.Add(job.Snapshot.TaskId, job);
                message = new ChatMessage(ChatRole.User, $"# Research assignment\n{task}\n\n# Parent context\n{context ?? "No additional context."}");
            }
            else
            {
                if (task is not null || context is not null || string.IsNullOrWhiteSpace(answer))
                    return Task.FromResult("Error: Resume requires taskId, questionId and nonempty answer only.");
                if (answer.Length > 16_000) return Task.FromResult("Error: answer must be at most 16000 characters.");
                if (!_jobs.TryGetValue(taskId, out job!) || job.Snapshot.Status != DelegationStatus.WaitingForParent ||
                    !job.Settled || job.Questions.Pending is not { } pending || pending.Id != questionId || job.Child is null)
                    return Task.FromResult("Error: Unknown, stale or no longer pending task/question. Jobs are not restored after restart or rewind.");
                message = new ChatMessage(ChatRole.User, $"# Parent answer\nQuestion ID: {pending.Id}\nQuestion: {pending.Question}\nAnswer: {answer}\n\nContinue the original research assignment.");
                job.Questions.AcceptAnswer();
                job.NotificationPending = false;
                job.Settled = false;
                job.Snapshot = job.Snapshot with { Status = DelegationStatus.Running, Answer = answer, Result = null, Error = null,
                    RequestNumber = job.Snapshot.RequestNumber + 1 };
            }
            _invocations[invocation.CallId] = job.Snapshot.TaskId;
            started = job.Snapshot;
            // The gate keeps worker execution behind the initial card publication.
            job.Operation = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await ExecuteAsync(job, message).ConfigureAwait(false);
            });
            _operations.RemoveAll(operation => operation.IsCompleted);
            _operations.Add(job.Operation);
            SignalChanged();
        }
        try
        {
            Publish(getInvocation() ?? job.Origin, started);
        }
        finally { start.TrySetResult(); }
        return Task.FromResult(JsonSerializer.Serialize(Output(started)));
    }

    [Description("Send a clarification or correction to an existing research subagent. Running jobs receive messages in order after the current tools, before the next model request. Completed jobs continue in the same session while retained (old delivered jobs may be evicted when retention is full). Does not interrupt an active tool or change permissions. For NeedsParentInput, answer with delegate/taskId/questionId/answer instead. Failed/Cancelled jobs cannot resume.")]
    public Task<string> SendMessageAsync(
        [Description("Existing research task ID.")] string taskId,
        [Description("Clarification or correction, at most 16000 characters.")] string message,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(message) || message.Length > 16_000)
                return Task.FromResult("Error: taskId and message must be nonempty; message must be at most 16000 characters.");
            if (!_jobs.TryGetValue(taskId, out var job) || job.Child is null || job.Cancellation.IsCancellationRequested)
                return Task.FromResult("Error: Unknown or no longer available research task.");
            if (job.Snapshot.Status == DelegationStatus.WaitingForParent)
                return Task.FromResult("Error: Answer the pending question using delegate with taskId, questionId and answer.");
            if (job.Snapshot.Status == DelegationStatus.Running)
            {
                job.Messages.Enqueue(message);
                return Task.FromResult(JsonSerializer.Serialize(new { status = "Queued", taskId }));
            }
            if (job.Snapshot.Status != DelegationStatus.Completed || !job.Settled)
                return Task.FromResult("Error: Research task is not ready for continuation.");
            if (_jobs.Values.Count(item => IsUnfinished(item.Snapshot)) >= 4)
                return Task.FromResult("Error: Four research jobs are unfinished. Wait for results or answer pending questions first.");

            var previous = job.Operation;
            job.NotificationPending = false;
            job.Settled = false;
            job.Snapshot = job.Snapshot with { Status = DelegationStatus.Running, Answer = message, Result = null, Error = null,
                RequestNumber = job.Snapshot.RequestNumber + 1 };
            // Wait for the preceding publication to finish; never run a session concurrently.
            job.Operation = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                Publish(job.Origin, job.Snapshot);
                await ExecuteAsync(job, ParentMessage(message)).ConfigureAwait(false);
            });
            _operations.RemoveAll(operation => operation.IsCompleted);
            _operations.Add(job.Operation);
            SignalChanged();
            return Task.FromResult(JsonSerializer.Serialize(new { status = "Running", taskId }));
        }
    }

    private static ChatMessage ParentMessage(string message) => new(ChatRole.User,
        "# Parent clarification\n" + message + "\n\nContinue the research with this clarification; inherited permissions and restrictions remain unchanged.");

    [Description("Wait for background research subagents, without polling or sleeping. Omit taskIds to watch running jobs and undelivered results; otherwise specify known IDs. Default returns on the first result or parent question. waitAll waits for all selected jobs, but returns early on NeedsParentInput so the parent can answer without deadlock. Timeout returns current states and does not cancel jobs. Results/questions returned here are consumed and will not be automatically delivered again.")]
    public async Task<string> WaitAsync(
        [Description("Optional task IDs; omit to watch current work.")] string[]? taskIds = null,
        [Description("Wait for all selected jobs; a parent question always wakes the wait.")] bool waitAll = false,
        [Description("Timeout in seconds, 1..600; jobs keep working after timeout.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds is < 1 or > 600) return "Error: timeoutSeconds must be 1..600.";
        Job[] selected;
        lock (_gate)
        {
            if (taskIds is not null)
            {
                if (taskIds.Length is < 1 or > 32 || taskIds.Any(id => id is null || !_jobs.ContainsKey(id)))
                    return "Error: taskIds must contain 1..32 known task IDs.";
                selected = taskIds.Distinct(StringComparer.Ordinal).Select(id => _jobs[id]).ToArray();
            }
            else selected = _jobs.Values.Where(job => job.NotificationPending || !job.Settled).ToArray();
            if (selected.Length == 0) return "Error: No running subagents or undelivered results. Answer any pending parent question with delegate.";
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                var ready = selected.Any(job => job.Settled);
                if (selected.Any(job => job.Settled && job.Snapshot.Status == DelegationStatus.WaitingForParent) ||
                    (waitAll ? selected.All(job => job.Settled) : ready))
                    return Collect(selected, waitAll ? "Ready" : "Result");
                if (DateTimeOffset.UtcNow >= deadline) return Collect(selected, "Timeout");
                changed = _changed.Task;
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { lock (_gate) return Collect(selected, "Timeout"); }
            try { await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { lock (_gate) return Collect(selected, "Timeout"); }
        }
    }

    public DelegationSnapshot? SnapshotForInvocation(string callId)
    {
        lock (_gate) return _invocations.TryGetValue(callId, out var id) && _jobs.TryGetValue(id, out var job) ? job.Snapshot : null;
    }

    public string? TakeNotifications()
    {
        lock (_gate)
        {
            var ready = _jobs.Values.Where(job => job.NotificationPending).ToArray();
            return ready.Length == 0 ? null : Collect(ready, "BackgroundNotifications");
        }
    }

    private string Collect(Job[] jobs, string status)
    {
        foreach (var job in jobs.Where(job => job.Settled)) job.NotificationPending = false;
        // Publication must finish before a result can be consumed; otherwise a concurrent completion
        // could be returned here and then delivered again by the readiness notification.
        return JsonSerializer.Serialize(new { status, tasks = jobs.Select(job => Output(job.Settled
            ? job.Snapshot : job.Snapshot with { Status = DelegationStatus.Running, Result = null, Error = null })).ToArray() });
    }

    private async Task ExecuteAsync(Job job, ChatMessage message)
    {
        DelegationSnapshot finished;
        var publishContinuation = false;
        while (true)
        {
            try
            {
                lock (_gate) job.BoundaryStop = false;
                if (publishContinuation) Publish(job.Origin, job.Snapshot);
                Activity?.Invoke(new DelegationActivity(job.Snapshot.TaskId, "request", message.Text));
                var result = new StringBuilder();
                try
                {
                    await foreach (var update in job.Child!.RunAsync(message, job.Model, job.Effort, job.Provider, job.Cancellation.Token).ConfigureAwait(false))
                    {
                        foreach (var text in update.Contents.OfType<TextContent>())
                        {
                            if ((long)result.Length + text.Text.Length > 32_000)
                                throw new InvalidDataException("Research result exceeds 32000 characters; no truncated result was accepted.");
                            result.Append(text.Text);
                        }
                    }
                }
                catch (OperationCanceledException) when (job.BoundaryStop && !job.Cancellation.IsCancellationRequested)
                {
                    // Same boundary recovery as a clarification in the main conversation:
                    // retain completed call/results before starting the next sequential run.
                    job.Child!.PreserveInterruptedHistory();
                }
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if (job.Questions.Failure is { } failure) throw new InvalidOperationException(failure);
                lock (_gate)
                {
                    if (job.Questions.Pending is { } question)
                        finished = job.Snapshot with { Status = DelegationStatus.WaitingForParent, QuestionId = question.Id,
                            Question = question.Question, Reason = question.Reason, Answer = null };
                    else if (job.Messages.TryDequeue(out var clarification))
                    {
                        message = ParentMessage(clarification);
                        job.Snapshot = job.Snapshot with { Answer = clarification, RequestNumber = job.Snapshot.RequestNumber + 1 };
                        publishContinuation = true;
                        continue;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(result.ToString())) throw new InvalidDataException("Research finished without a text result.");
                        finished = job.Snapshot with { Status = DelegationStatus.Completed, Result = result.ToString() };
                    }
                    // Commit under the same gate as message acceptance: no accepted message
                    // can be stranded between the final queue check and completion.
                    job.Snapshot = finished;
                    job.NotificationPending = false;
                }
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                lock (_gate)
                    job.Snapshot = finished = job.Snapshot with { Status = DelegationStatus.Cancelled, Result = null,
                        Error = "Background research cancelled." };
            }
            catch (Exception error)
            {
                lock (_gate)
                    job.Snapshot = finished = job.Snapshot with { Status = DelegationStatus.Failed, Error = error.Message };
            }
            break;
        }

        lock (_gate)
        {
            if (job.Cancellation.IsCancellationRequested)
                finished = finished with { Status = DelegationStatus.Cancelled, Result = null, Error = "Background research cancelled." };
            job.Snapshot = finished;
            job.NotificationPending = false;
        }
        if (finished.Status is DelegationStatus.Failed or DelegationStatus.Cancelled) ReleaseChild(job);
        Publish(job.Origin, finished);
        lock (_gate)
        {
            job.Settled = true;
            job.NotificationPending = finished.Status != DelegationStatus.Cancelled;
            SignalChanged();
        }
        Ready?.Invoke();
    }

    private void Publish(ToolCallUpdate origin, DelegationSnapshot snapshot)
    {
        Updated?.Invoke(origin with
        {
            Status = snapshot.Status switch
            {
                DelegationStatus.Running => ToolCallStatus.Running,
                DelegationStatus.Failed => ToolCallStatus.Failed,
                DelegationStatus.Cancelled => ToolCallStatus.Cancelled,
                _ => ToolCallStatus.Completed
            },
            Delegation = snapshot,
            Result = JsonSerializer.Serialize(Output(snapshot))
        });
    }

    private static object Output(DelegationSnapshot snapshot) => new
    {
        status = snapshot.Status == DelegationStatus.WaitingForParent ? "NeedsParentInput" : snapshot.Status.ToString(),
        taskId = snapshot.TaskId,
        questionId = snapshot.Status == DelegationStatus.WaitingForParent ? snapshot.QuestionId : null,
        question = snapshot.Status == DelegationStatus.WaitingForParent ? snapshot.Question : null,
        reason = snapshot.Status == DelegationStatus.WaitingForParent ? snapshot.Reason : null,
        result = snapshot.Result, error = snapshot.Error
    };

    private static bool IsUnfinished(DelegationSnapshot snapshot) => snapshot.Status is DelegationStatus.Running or DelegationStatus.WaitingForParent;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SignalChanged() { _changed.TrySetResult(); _changed = NewSignal(); }

    private void PruneDelivered()
    {
        if (_jobs.Count < 32) return;
        foreach (var job in _jobs.Values.Where(job => !IsUnfinished(job.Snapshot) && !job.NotificationPending && job.Operation.IsCompleted).ToArray())
        {
            ReleaseChild(job);
            _jobs.Remove(job.Snapshot.TaskId);
            job.Cancellation.Dispose();
            foreach (var call in _invocations.Where(pair => pair.Value == job.Snapshot.TaskId).Select(pair => pair.Key).ToArray()) _invocations.Remove(call);
        }
    }

    private static void ReleaseChild(Job job) { job.Child?.Dispose(); job.Child = null; }

    public void RequestCancel()
    {
        Job[] jobs;
        lock (_gate) jobs = _jobs.Values.Where(job => IsUnfinished(job.Snapshot)).ToArray();
        foreach (var job in jobs)
        {
            try { job.Cancellation.Cancel(); }
            catch (ObjectDisposedException)
            {
                // A finished, consumed job was pruned after this cancellation snapshot was captured.
            }
        }
    }

    public async Task CancelPendingAsync()
    {
        RequestCancel();
        Job[] jobs;
        lock (_gate) jobs = _jobs.Values.ToArray();
        Task[] operations;
        lock (_gate) operations = _operations.ToArray();
        await Task.WhenAll(operations).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            DelegationSnapshot? cancelled = null;
            lock (_gate)
            {
                job.NotificationPending = false;
                if (IsUnfinished(job.Snapshot))
                {
                    cancelled = job.Snapshot = job.Snapshot with { Status = DelegationStatus.Cancelled, Error = "Background research cancelled." };
                    SignalChanged();
                }
            }
            ReleaseChild(job);
            if (cancelled is not null) Publish(job.Origin, cancelled);
        }
    }

    public async Task ResetAsync()
    {
        await CancelPendingAsync().ConfigureAwait(false);
        Reset();
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_jobs.Values.Any(job => !job.Operation.IsCompleted)) throw new InvalidOperationException("Drain background research before resetting its session.");
            foreach (var job in _jobs.Values) { ReleaseChild(job); job.Cancellation.Dispose(); }
            _jobs.Clear();
            _invocations.Clear();
            _operations.Clear();
            SignalChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        await ResetAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        _lifetime.Dispose();
    }
}

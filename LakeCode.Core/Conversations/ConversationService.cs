using LakeCode.Core.Providers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.Ask;
using LakeCode.Core.Attachments;
using LakeCode.Core.Sessions;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Conversations;

/// <summary>
/// Owns one conversation and reads the agent stream exactly once. Commands, event notifications,
/// and state transitions are serialized by _gate. Subscribers must only enqueue notifications,
/// never block on operations or throw. No UI thread, controls, focus, or preview dependencies.
/// </summary>
public sealed class ConversationService
{
    private readonly object _gate = new();
    private readonly ProviderRegistry _providers;
    private readonly SessionStorage _sessions = new();
    private readonly string _workspaceRoot;
    private readonly CancellationToken _shutdown;
    private AgentLoop? _agent;
    private SessionSnapshot? _snapshot;
    private readonly List<PendingMessage> _pending = [];
    private Task _operation = Task.CompletedTask;
    private Task _pendingPersistence = Task.CompletedTask;
    private CancellationTokenSource? _generation;
    private Guid? _requestId;
    private bool _busy, _queuePaused, _replacementRequested, _stoppedAtBoundary;
    private bool _saveFailed, _contextFailed, _pendingSaveFailed;
    private int _boundaryStopRequested;
    private ModelInfo _statisticsModel;
    private RequestStatistics? _statistics;
    private double? _tokensPerSecond;
    private long _revision, _sequence;
    private readonly Dictionary<Guid, TaskCompletionSource<string[][]>> _questions = [];
    private readonly Dictionary<Guid, TaskCompletionSource<ExternalReadDecision>> _permissions = [];
    private ConversationState _state;

    public event Action<ConversationState>? StateChanged;
    public event Action<ConversationEventEnvelope>? EventOccurred;
    public ConversationState State { get { lock (_gate) return _state; } }
    public static string ResolveWorkspaceRoot() => WorkspaceFiles.ResolveRoot();

    public ConversationService(string workspaceRoot, ProviderRegistry providers,
        ModelInfo initialModel, CancellationToken shutdown = default)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _providers = providers;
        _statisticsModel = initialModel;
        _shutdown = shutdown;
        _state = CreateState();
    }

    public ConversationView GetConversation()
    {
        lock (_gate) return CreateView();
    }

    /// <summary>Returns false on rejection. Accepted work may finish later; state and events report it.</summary>
    public bool Execute(ConversationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return false;
            return command switch
            {
                SubmitMessage submit => Submit(submit),
                StopGeneration => Stop(),
                StartNewSession start => NewSession(start.ProviderId, start.ModelId),
                ListSessions => BeginListSessions(),
                OpenSession open => BeginOpenSession(open.SessionId),
                RewindToTurn rewind => BeginRewind(rewind.Index),
                AnswerQuestion answer => Answer(answer),
                ResolveReadPermission permission => ResolvePermission(permission),
                FailInteraction failure => Fail(failure),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        }
    }

    public async Task FinishAsync()
    {
        // Shutdown prevents dispatch of another turn; include every accepted persistence operation.
        while (true)
        {
            Task operation, persistence;
            lock (_gate) { operation = _operation; persistence = _pendingPersistence; }
            await Task.WhenAll(operation, persistence).ConfigureAwait(false);
            lock (_gate)
                if (operation == _operation && persistence == _pendingPersistence) { _agent?.Dispose(); _agent = null; return; }
        }
    }

    private ConversationState CreateState() => new(++_revision, _snapshot?.Id, _snapshot?.Title,
        _snapshot?.Turns.Count ?? 0, _busy, _generation is not null, _queuePaused,
        _saveFailed, _contextFailed, _pendingSaveFailed,
        _generation is { IsCancellationRequested: false } && _questions.Count == 0 && _permissions.Count == 0,
        !_busy && _pending.Count == 0 && !_saveFailed && !_contextFailed && !_pendingSaveFailed,
        _pending.Select(item => new PendingMessageState(item.Text, item.Images.Length, item.ModelId,
            item.ReasoningEffort, item.Mode, item.ProviderId)).ToImmutableArray(),
        _statisticsModel, _statistics, _tokensPerSecond);

    private void PublishState()
    {
        _state = CreateState();
        StateChanged?.Invoke(_state);
    }

    private void Emit(ConversationEvent fact) =>
        EventOccurred?.Invoke(new ConversationEventEnvelope(++_sequence, _snapshot?.Id, _requestId, fact));

    private bool Reject(string message) { Emit(new ConversationError(message)); return false; }

    private bool Stop()
    {
        _queuePaused = true;
        _replacementRequested = false;
        Interlocked.Exchange(ref _boundaryStopRequested, 0);
        _generation?.Cancel();
        PublishState();
        return true;
    }

    private bool NewSession(string providerId, string modelId)
    {
        if (_busy || !_pendingPersistence.IsCompleted) return false;
        if (_pending.Count > 0)
            return Reject("В сессии есть ожидающие сообщения. Выполните очередь перед созданием новой сессии.");
        if (_saveFailed)
            return Reject("Текущая сессия не сохранена. Откройте /sessions для повторной попытки сохранения перед переключением.");
        try { _statisticsModel = _providers.GetModel(providerId, modelId); }
        catch (Exception error) { return Reject(error.Message); }
        _statistics = null;
        _tokensPerSecond = null;
        _agent?.Dispose();
        _agent = null;
        _snapshot = null;
        _contextFailed = false;
        _pendingSaveFailed = false;
        _queuePaused = false;
        Emit(new ConversationReplaced(CreateView()));
        PublishState();
        return true;
    }

    private bool Submit(SubmitMessage command)
    {
        if (_busy && _generation is null) return false;
        if (_contextFailed || _saveFailed)
            return Reject("Отправка заблокирована: контекст или сессия не сохранены.");
        var images = command.Images.IsDefault ? ImmutableArray<ImageData>.Empty : command.Images;
        if (string.IsNullOrWhiteSpace(command.Text) && images.Length == 0)
        {
            if (!_busy && command.Mode == SubmissionMode.Enqueue)
            {
                _queuePaused = false;
                if (_snapshot is { } current) SchedulePendingSave(current.Id, _pending.ToArray(), dispatch: true);
                else StartNextPending();
                PublishState();
            }
            return false;
        }
        ProviderProfile provider;
        ModelInfo model;
        try
        {
            provider = _providers.Get(command.ProviderId);
            model = _providers.GetModel(command.ProviderId, command.ModelId);
            if (string.IsNullOrWhiteSpace(provider.ApiKey)) return Reject("Укажите API-ключ через /providers; черновик сохранён.");
        }
        catch (Exception error) { return Reject(error.Message); }
        if (!model.ReasoningEfforts.Contains(command.ReasoningEffort) || !Enum.IsDefined(command.Mode))
            return Reject("Настройки сообщения больше не поддерживаются.");
        if (images.Length > 0 && !model.InputModalities.Contains(ModelModality.Image))
            return Reject($"Модель {model.Name} не поддерживает изображения. Выберите другую модель или удалите вложения; черновик сохранён.");
        if (_pendingSaveFailed)
            return Reject("Очередь не сохранена. F6 с пустым полем повторит сохранение; черновик сохранён.");
        if (_pending.Count >= 32 || _pending.Sum(item => item.Images.Sum(image => (long)image.Data.Length)) +
            images.Sum(image => (long)image.Data.Length) > 96L * 1024 * 1024)
            return Reject("Очередь заполнена (32 сообщения или 96 MiB изображений). Черновик сохранён.");
        if (!model.InputModalities.Contains(ModelModality.Image) && _snapshot?.Turns.Any(turn => turn.Images.Count > 0) == true)
            return Reject("История содержит изображения. Выберите модель с image input или начните новую сессию; черновик сохранён.");
        SavedImage[] saved;
        try
        {
            ImageAttachments.ValidateTotal(images.Select(image => image.Data));
            // Own the bytes: later draft edits must not affect an accepted message.
            saved = images.Select(image => new SavedImage(image.Name, image.Data.ToArray())).ToArray();
            foreach (var image in saved) image.Decode();
        }
        catch (Exception error) { return Reject(error.Message); }
        var pending = new PendingMessage(command.Text, saved, model.Id, command.ReasoningEffort, command.Mode, provider.Id)
        { Connection = provider, Model = model };
        if (command.Mode == SubmissionMode.Interrupt) _pending.Insert(0, pending);
        else if (command.Mode == SubmissionMode.Clarify)
        {
            var index = _pending.FindIndex(item => item.Mode == SubmissionMode.Enqueue);
            _pending.Insert(index < 0 ? _pending.Count : index, pending);
        }
        else _pending.Add(pending);
        _queuePaused = false;
        if (_generation is not null)
        {
            if (command.Mode == SubmissionMode.Clarify) Interlocked.Exchange(ref _boundaryStopRequested, 1);
            else if (command.Mode == SubmissionMode.Interrupt)
            {
                _replacementRequested = true;
                _generation.Cancel();
            }
        }
        _snapshot ??= new SessionSnapshot { WorkspaceRoot = _workspaceRoot };
        _snapshot.PendingMessages = _pending.ToArray();
        SchedulePendingSave(_snapshot.Id, _pending.ToArray(), dispatch: !_busy);
        PublishState();
        return true;
    }

    private Task SchedulePendingSave(string id, PendingMessage[] messages, bool dispatch = false)
    {
        // Called under _gate. All durable queue writes retain command order.
        _pendingPersistence = _pendingPersistence.ContinueWith(async _ =>
        {
            try
            {
                await _sessions.SavePendingAsync(id, messages).ConfigureAwait(false);
                lock (_gate)
                {
                    _pendingSaveFailed = false;
                    if (dispatch) StartNextPending();
                    PublishState();
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _pendingSaveFailed = true;
                    _queuePaused = true;
                    Emit(new ConversationError($"Не удалось сохранить очередь: {error.Message}. F6 с пустым полем — повторить."));
                    PublishState();
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return _pendingPersistence;
    }

    private void StartNextPending()
    {
        if (_busy || _queuePaused || _contextFailed || _saveFailed || _pendingSaveFailed ||
            _shutdown.IsCancellationRequested || _pending.Count == 0) return;
        var pending = _pending[0];
        ImageData[] images;
        ModelInfo model;
        ProviderProfile provider;
        try
        {
            provider = pending.Connection ?? _providers.Get(pending.ProviderId);
            model = pending.Model ?? _providers.GetModel(pending.ProviderId, pending.ModelId);
            if (!model.ReasoningEfforts.Contains(pending.ReasoningEffort) ||
                (pending.Images.Length > 0 && !model.InputModalities.Contains(ModelModality.Image)))
                throw new InvalidDataException("Настройки ожидающего сообщения больше не поддерживаются.");
            if (string.IsNullOrWhiteSpace(provider.ApiKey)) throw new InvalidDataException("Укажите API-ключ через /providers.");
            if (!model.InputModalities.Contains(ModelModality.Image) && _snapshot?.Turns.Any(turn => turn.Images.Count > 0) == true)
                throw new InvalidDataException("История содержит изображения; ожидающему сообщению нужна модель с image input.");
            images = pending.Images.Select(image => image.Decode()).ToArray();
        }
        catch (Exception error)
        {
            _queuePaused = true;
            Emit(new ConversationError($"Не удалось отправить ожидающее сообщение: {error.Message}"));
            PublishState();
            return;
        }
        _pending.RemoveAt(0);
        Interlocked.Exchange(ref _boundaryStopRequested, 0);
        _replacementRequested = false;
        _stoppedAtBoundary = false;
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(pending.Text)) contents.Add(new TextContent(pending.Text));
        foreach (var image in images) contents.Add(new DataContent(image.Data, image.MediaType));
        var request = new ChatMessage(ChatRole.User, contents);
        var snapshot = _snapshot!;
        snapshot.PendingMessages = _pending.ToArray();
        snapshot.ModelMetadata = model;
        snapshot.ProviderId = provider.Id;
        snapshot.ModelId = model.Id;
        snapshot.ReasoningEffort = pending.ReasoningEffort;
        if (snapshot.Turns.Count == 0)
        {
            var title = string.Join(" ", pending.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            snapshot.Title = string.IsNullOrEmpty(title) ? "Сообщение с изображениями" : title[..Math.Min(title.Length, 80)];
        }
        var turn = new SessionTurn { Text = pending.Text, Images = pending.Images.ToList(),
            ModelMetadata = model, ProviderId = provider.Id, ModelId = model.Id, ReasoningEffort = pending.ReasoningEffort };
        snapshot.Turns.Add(turn);
        _busy = true;
        var generation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _generation = generation;
        _requestId = Guid.NewGuid();
        Emit(new TurnStarted(pending.Text, images.ToImmutableArray()));
        PublishState();
        _operation = Task.Run(async () =>
        {
            try { await SendAsync(request, model, pending.ReasoningEffort, provider, snapshot, turn, generation.Token).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    generation.Dispose();
                    _generation = null;
                    Emit(new TurnFinished(turn.Status, turn.Error));
                    _requestId = null;
                    _busy = false;
                    if (turn.Status != "completed" && !_stoppedAtBoundary && !_replacementRequested) _queuePaused = true;
                    if (_contextFailed || _saveFailed) _queuePaused = true;
                    PublishState();
                    // SendAsync, including final persistence, has finished before another run starts.
                    StartNextPending();
                }
            }
        });
    }

    private bool BeginListSessions()
    {
        if (_busy) return false;
        _busy = true;
        PublishState();
        _operation = Task.Run(async () =>
        {
            try
            {
                SessionSnapshot? unsaved;
                lock (_gate) unsaved = _saveFailed && _snapshot is not null ? CopySnapshot(_snapshot) : null;
                if (unsaved is not null)
                {
                    await _sessions.SaveAsync(unsaved, _shutdown).ConfigureAwait(false);
                    lock (_gate) _saveFailed = false;
                }
                var result = await _sessions.ListAsync(_workspaceRoot, _shutdown).ConfigureAwait(false);
                lock (_gate) Emit(new SessionsListed(result.Sessions.ToImmutableArray(), result.Errors.ToImmutableArray(), _snapshot?.Id));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception error) { lock (_gate) Emit(new ConversationError($"Не удалось загрузить список сессий: {error.Message}")); }
            finally { lock (_gate) { _busy = false; PublishState(); } }
        });
        return true;
    }

    private bool BeginOpenSession(string id)
    {
        if (_busy || !_pendingPersistence.IsCompleted) return false;
        if (_saveFailed) return Reject("Текущая сессия не сохранена. Повторите сохранение через /sessions.");
        _busy = true;
        PublishState();
        _operation = Task.Run(async () =>
        {
            try
            {
                var snapshot = await _sessions.LoadAsync(id, _shutdown).ConfigureAwait(false);
                if (snapshot.WorkspaceRoot != _workspaceRoot)
                    throw new InvalidDataException("Сессия относится к другому рабочему каталогу.");
                var model = snapshot.ModelMetadata ?? FindSavedModel(snapshot.ProviderId, snapshot.ModelId) ?? _statisticsModel;
                var agent = new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, snapshot.Id, AskUserAsync);
                if (snapshot.AgentState is { } state) await agent.RestoreSessionAsync(state, _shutdown).ConfigureAwait(false);
                ValidateImages(snapshot);
                foreach (var turn in snapshot.Turns.Where(turn => turn.Status == "in_progress"))
                {
                    turn.Status = "interrupted";
                    turn.Error = "Запрос не завершён до закрытия приложения. Восстановлен последний сохранённый контекст. Выполненные действия не отменены и не будут повторены автоматически.";
                }
                lock (_gate)
                {
                    _agent?.Dispose();
                    _agent = agent;
                    _snapshot = snapshot;
                    _pending.Clear();
                    _pending.AddRange(snapshot.PendingMessages);
                    // Restored work is visible but never executes without explicit user action.
                    _queuePaused = _pending.Count > 0;
                    _pendingSaveFailed = false;
                    _contextFailed = false;
                    _statisticsModel = model;
                    _statistics = null;
                    _tokensPerSecond = null;
                    if (snapshot.Turns.LastOrDefault(turn => turn.Usage is not null) is { Usage: { } usage } last &&
                        (last.ModelMetadata ?? FindSavedModel(last.ProviderId, last.ModelId)) is { } usageModel)
                    {
                        _statistics = ToStatistics(usage, usageModel);
                        _tokensPerSecond = snapshot.Turns.LastOrDefault(turn => turn.Status == "completed")?.TokensPerSecond;
                    }
                    Emit(new ConversationReplaced(CreateView()));
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception error) { lock (_gate) Emit(new ConversationError($"Не удалось открыть сессию: {error.Message}")); }
            finally { lock (_gate) { _busy = false; PublishState(); } }
        });
        return true;
    }

    private bool BeginRewind(int index)
    {
        if (!_state.CanRewind || _snapshot is null || index < 0 || index >= _snapshot.Turns.Count) return false;
        if (_snapshot.Turns[index].HistoryStart is not { } boundary)
            return Reject("Для этого старого сообщения нет сохранённой границы контекста.");
        var current = CopySnapshot(_snapshot);
        _busy = true;
        PublishState();
        _operation = Task.Run(async () =>
        {
            JsonElement? original = null;
            bool committed = false;
            try
            {
                var next = CopySnapshot(current);
                var selected = next.Turns[index];
                ValidateImages(next);
                _agent ??= new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, current.Id, AskUserAsync);
                if (current.AgentState is { } state) await _agent.RestoreSessionAsync(state).ConfigureAwait(false);
                original = await _agent.SerializeSessionAsync().ConfigureAwait(false);
                _agent.Rewind(boundary);
                next.AgentState = await _agent.SerializeSessionAsync().ConfigureAwait(false);
                await _agent.RestoreSessionAsync(original.Value).ConfigureAwait(false);
                next.Turns.RemoveRange(index, next.Turns.Count - index);
                next.DraftText = selected.Text;
                next.DraftImages = selected.Images;
                next.ProviderId = selected.ProviderId;
                next.ModelId = selected.ModelId;
                next.ModelMetadata = selected.ModelMetadata;
                next.ReasoningEffort = selected.ReasoningEffort;
                next.SchemaVersion = 3;
                next.UpdatedAt = DateTimeOffset.UtcNow;
                await _sessions.SaveAsync(next).ConfigureAwait(false);
                await _agent.RestoreSessionAsync(next.AgentState.Value).ConfigureAwait(false);
                committed = true;
                lock (_gate)
                {
                    _snapshot = next;
                    _statistics = null;
                    _tokensPerSecond = null;
                    Emit(new ConversationReplaced(CreateView()));
                }
            }
            catch (Exception error)
            {
                if (!committed && original is { } state && _agent is not null)
                {
                    try { await _agent.RestoreSessionAsync(state).ConfigureAwait(false); }
                    catch { lock (_gate) _contextFailed = true; }
                }
                lock (_gate) Emit(new ConversationError($"Не удалось вернуть сообщение: {error.Message}"));
            }
            finally { lock (_gate) { _busy = false; PublishState(); } }
        });
        return true;
    }

    private bool Answer(AnswerQuestion answer)
    {
        if (!_questions.TryGetValue(answer.InteractionId, out var completion) || completion.Task.IsCompleted) return false;
        if (answer.Answers is null) { Stop(); return completion.TrySetCanceled(); }
        return completion.TrySetResult(answer.Answers.Select(values => values.ToArray()).ToArray());
    }

    private bool ResolvePermission(ResolveReadPermission permission) =>
        Enum.IsDefined(permission.Decision) && _permissions.TryGetValue(permission.InteractionId, out var completion) &&
        completion.TrySetResult(permission.Decision);

    private bool Fail(FailInteraction failure)
    {
        var error = new InvalidOperationException(failure.Error);
        if (_questions.TryGetValue(failure.InteractionId, out var question)) return question.TrySetException(error);
        if (_permissions.TryGetValue(failure.InteractionId, out var permission)) return permission.TrySetException(error);
        return false;
    }

    private async Task<string[][]> AskUserAsync(AskQuestion[] questions, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<string[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        lock (_gate)
        {
            _questions.Add(id, completion);
            Emit(new QuestionRequested(id, questions.Select(question => question with { Options = question.Options.ToArray() }).ToImmutableArray()));
            PublishState();
        }
        try { return await completion.Task.ConfigureAwait(false); }
        finally
        {
            lock (_gate) { _questions.Remove(id); Emit(new InteractionFinished(id)); PublishState(); }
        }
    }

    private async Task<ExternalReadDecision> ConfirmExternalReadAsync(ExternalReadRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<ExternalReadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        lock (_gate) { _permissions.Add(id, completion); Emit(new ReadPermissionRequested(id, request)); PublishState(); }
        try { return await completion.Task.ConfigureAwait(false); }
        finally { lock (_gate) { _permissions.Remove(id); Emit(new InteractionFinished(id)); PublishState(); } }
    }

    private async Task<string?> GenerateSessionTitleAsync(ChatMessage message, ModelInfo model, ProviderProfile provider, CancellationToken generationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var title = await _agent!.GenerateTitleAsync(message, model, provider, timeout.Token).ConfigureAwait(false);
            lock (_gate) { _snapshot!.Title = title; PublishState(); }
            return title;
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested) { return null; }
        catch (Exception error)
        {
            var reason = error is OperationCanceledException ? "превышено время ожидания" : error.Message;
            lock (_gate) Emit(new ConversationError($"Не удалось сгенерировать заголовок сессии: {reason}. Сохранено название из первого сообщения."));
            return null;
        }
    }

    private async Task SendAsync(ChatMessage message, ModelInfo model, string reasoningEffort, ProviderProfile provider,
        SessionSnapshot snapshot, SessionTurn turn, CancellationToken generationToken)
    {
        var checkpoint = snapshot.AgentState;
        Task<string?> titleTask = Task.FromResult<string?>(null);
        bool completed = false;
        void OnToolCall(ToolCallUpdate update)
        {
            lock (_gate)
            {
                turn.Events.Add(new ResponseEvent { Kind = "tool", ToolCall = update });
                Emit(new ToolCallChanged(update));
            }
        }
        void OnBeforeModelRequest()
        {
            if (Interlocked.Exchange(ref _boundaryStopRequested, 0) == 0) return;
            lock (_gate) _stoppedAtBoundary = true;
            throw new OperationCanceledException("Переход к уточнению после инструментов.");
        }
        try
        {
            lock (_gate) snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            // Persist the user's turn before any external request or tool execution.
            await SaveSnapshotAsync(snapshot, generationToken).ConfigureAwait(false);
            Task persistence;
            lock (_gate) persistence = SchedulePendingSave(snapshot.Id, snapshot.PendingMessages);
            await persistence.ConfigureAwait(false);
            lock (_gate) if (_pendingSaveFailed) throw new IOException("Очередь сообщений не сохранена.");
            if (_agent is null)
            {
                var agent = new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, snapshot.Id, AskUserAsync);
                if (checkpoint is { } savedState) await agent.RestoreSessionAsync(savedState, generationToken).ConfigureAwait(false);
                _agent = agent;
            }
            if (snapshot.Turns.Count == 1) titleTask = GenerateSessionTitleAsync(message, model, provider, generationToken);
            checkpoint ??= await _agent.SerializeSessionAsync(generationToken).ConfigureAwait(false);
            lock (_gate)
            {
                snapshot.AgentState = checkpoint;
                turn.HistoryStart = _agent.HistoryCount;
                snapshot.SchemaVersion = 3;
                snapshot.DraftText = null;
                snapshot.DraftImages.Clear();
            }
            await SaveSnapshotAsync(snapshot, generationToken).ConfigureAwait(false);
            _agent.ToolCallUpdated += OnToolCall;
            _agent.BeforeModelRequest += OnBeforeModelRequest;
            long? firstTokenTimestamp = null, outputTokens = null;
            await foreach (var update in _agent.RunAsync(message, model, reasoningEffort, provider, generationToken).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    if (firstTokenTimestamp is null && (content is TextReasoningContent { Text.Length: > 0 } || content is TextContent { Text.Length: > 0 }))
                        firstTokenTimestamp = Stopwatch.GetTimestamp();
                    lock (_gate)
                    {
                        if (content is UsageContent usage)
                        {
                            var details = usage.Details;
                            outputTokens = details.OutputTokenCount ?? outputTokens;
                            turn.Usage = new SavedUsage(details.InputTokenCount, details.CachedInputTokenCount,
                                details.OutputTokenCount, details.ReasoningTokenCount, details.TotalTokenCount);
                            _statistics = ToStatistics(turn.Usage, model);
                            PublishState();
                        }
                        else if (reasoningEffort != "none" && content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            turn.AppendText("thinking", reasoning.Text);
                            Emit(new ReasoningDelta(reasoning.Text));
                        }
                        else if (content is TextContent answer && !string.IsNullOrEmpty(answer.Text))
                        {
                            turn.AppendText("text", answer.Text);
                            Emit(new TextDelta(answer.Text));
                        }
                    }
                }
            }
            var elapsed = firstTokenTimestamp is { } start ? Stopwatch.GetElapsedTime(start) : TimeSpan.Zero;
            var state = await _agent.SerializeSessionAsync(generationToken).ConfigureAwait(false);
            lock (_gate)
            {
                snapshot.AgentState = state;
                completed = true;
                turn.Status = "completed";
                turn.TokensPerSecond = outputTokens is { } tokens && elapsed > TimeSpan.Zero ? tokens / elapsed.TotalSeconds : null;
                _tokensPerSecond = turn.TokensPerSecond;
                PublishState();
            }
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested || _stoppedAtBoundary)
        {
            lock (_gate)
            {
                turn.Status = "interrupted";
                turn.Error = (_stoppedAtBoundary ? "Работа продолжится с уточнением после завершённых инструментов." :
                    _shutdown.IsCancellationRequested ? "Запрос отменён при закрытии приложения." : "Генерация остановлена пользователем.") +
                    " Полученная история сохранена; выполненные действия не отменены.";
            }
        }
        catch (Exception error)
        {
            lock (_gate) { turn.Status = "failed"; turn.Error = error.Message + "\nПолученная история сохранена. Уже выполненные действия не отменены."; }
        }
        finally
        {
            if (_agent is not null) { _agent.ToolCallUpdated -= OnToolCall; _agent.BeforeModelRequest -= OnBeforeModelRequest; }
            if (!completed)
            {
                try
                {
                    if (_agent is not null)
                    {
                        _agent.PreserveInterruptedHistory();
                        var state = await _agent.SerializeSessionAsync().ConfigureAwait(false);
                        lock (_gate) snapshot.AgentState = state;
                    }
                }
                catch (Exception error)
                {
                    lock (_gate)
                    {
                        _contextFailed = true;
                        Emit(new ConversationError($"Не удалось сохранить контекст агента: {error.Message}. Отправка заблокирована; события диалога сохраняются."));
                    }
                }
            }
            lock (_gate)
            {
                var pending = turn.Events.Where(item => item.ToolCall is not null).GroupBy(item => item.ToolCall!.CallId)
                    .Select(group => group.Last().ToolCall!).Where(call => call.Status == ToolCallStatus.Running).ToArray();
                foreach (var call in pending)
                {
                    var interrupted = call with { Status = ToolCallStatus.Cancelled, Result = "Вызов прерван; результат не получен." };
                    turn.Events.Add(new ResponseEvent { Kind = "tool", ToolCall = interrupted });
                    Emit(new ToolCallChanged(interrupted));
                }
            }
            await titleTask.ConfigureAwait(false);
            lock (_gate) snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                // Final save is not cancelled by shutdown; FinishAsync awaits it.
                await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
                lock (_gate) _saveFailed = false;
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _saveFailed = true;
                    Emit(new ConversationError($"Сессия не сохранена: {error.Message}. /sessions повторит сохранение."));
                }
                if (_shutdown.IsCancellationRequested) Console.Error.WriteLine($"Сессия не сохранена: {error.Message}");
            }
        }
    }

    private Task SaveSnapshotAsync(SessionSnapshot snapshot, CancellationToken token = default)
    {
        SessionSnapshot copy;
        lock (_gate) copy = CopySnapshot(snapshot);
        return _sessions.SaveAsync(copy, token);
    }

    private ModelInfo? FindSavedModel(string providerId, string modelId)
    {
        var configured = _providers.Profiles.FirstOrDefault(profile => profile.Id == providerId)?.Models
            .FirstOrDefault(model => model.Id == modelId);
        if (configured is not null) return configured;
        if (providerId == ProviderProfile.LegacyId && ModelCatalog.Models.TryGetValue(modelId, out var legacy)) return legacy;
        return null;
    }

    private static RequestStatistics ToStatistics(SavedUsage usage, ModelInfo model) =>
        new(model, usage.Input, usage.Output, usage.Total, usage.Cached, usage.Reasoning);

    private static void ValidateImages(SessionSnapshot snapshot)
    {
        foreach (var images in snapshot.Turns.Select(turn => turn.Images).Append(snapshot.DraftImages))
        {
            ImageAttachments.ValidateTotal(images.Select(image => (ReadOnlyMemory<byte>)image.Data));
            foreach (var image in images) image.Decode();
        }
    }

    private ConversationView CreateView() => new(_snapshot?.Id, _snapshot?.ModelId, _snapshot?.ReasoningEffort,
        _snapshot?.Turns.Select(turn => new TurnView(turn.Text, turn.Images.Select(image => image.Decode()).ToImmutableArray(),
            turn.Events.Select(item => new TranscriptItem(item.Kind, item.Text, item.ToolCall)).ToImmutableArray(),
            turn.Status, turn.Error)).ToImmutableArray() ?? [],
        _snapshot?.DraftText ?? "", _snapshot?.DraftImages.Select(image => image.Decode()).ToImmutableArray() ?? [], _snapshot?.ProviderId);

    // Mutable DTOs never escape Core. Image buffers are privately owned and never modified after acceptance.
    private static SessionSnapshot CopySnapshot(SessionSnapshot source) => new()
    {
        SchemaVersion = source.SchemaVersion, FrameworkVersion = source.FrameworkVersion, Id = source.Id,
        WorkspaceRoot = source.WorkspaceRoot, Title = source.Title, CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
        ModelMetadata = source.ModelMetadata, ProviderId = source.ProviderId, ModelId = source.ModelId, ReasoningEffort = source.ReasoningEffort, AgentState = source.AgentState,
        DraftText = source.DraftText, DraftImages = source.DraftImages.ToList(), PendingMessages = source.PendingMessages.ToArray(),
        Turns = source.Turns.Select(turn => new SessionTurn
        {
            HistoryStart = turn.HistoryStart, Text = turn.Text, Images = turn.Images.ToList(), ModelMetadata = turn.ModelMetadata, ProviderId = turn.ProviderId, ModelId = turn.ModelId,
            ReasoningEffort = turn.ReasoningEffort, Status = turn.Status, Error = turn.Error, Usage = turn.Usage,
            TokensPerSecond = turn.TokensPerSecond,
            Events = turn.Events.Select(item => new ResponseEvent { Kind = item.Kind, Text = item.Text, ToolCall = item.ToolCall }).ToList()
        }).ToList()
    };
}

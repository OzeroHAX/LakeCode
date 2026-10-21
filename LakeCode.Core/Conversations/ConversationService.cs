using LakeCode.Core.Providers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Delegation;
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
public sealed partial class ConversationService
{
    private readonly object _gate = new();
    private readonly ProviderRegistry _providers;
    private readonly Settings.AgentSettingsStorage? _settings;
    private ExecutionApproval.ClassifierConfiguration _activeClassifier = new(new(), null, null);
    private readonly SessionStorage _sessions = new();
    private readonly string _workspaceRoot;
    private readonly CancellationToken _shutdown;
    private AgentLoop? _agent;
    private SessionTurn? _activeTurn;
    private readonly Dictionary<string, (SessionTurn Turn, Guid RequestId)> _backgroundOwners = new(StringComparer.Ordinal);
    private (ProviderProfile Provider, ModelInfo Model, string Effort, Settings.ModelSelection? SubagentModel,
        Settings.ModelConfiguration? SubagentConfiguration, AgentMode AgentMode, ExecutionApproval.ClassifierConfiguration Classifier,
        Settings.ImageGenerationConfiguration ImageGeneration)? _lastRequest;
    private readonly SemaphoreSlim _interactionGate = new(1, 1);
    private bool _finishing;
    private bool _allowAllExternalReads;
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
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _executionApprovals = [];
    private ConversationState _state;
    private readonly List<SideQuestionExchange> _sideExchanges = [];
    private CancellationTokenSource? _sideCancellation;
    private Task _sideOperation = Task.CompletedTask;
    private Guid? _sideRequestId;
    private long _sideRevision;
    private const int SideHistoryCharacters = 128_000;

    public event Action<ConversationState>? StateChanged;
    public event Action<ConversationEventEnvelope>? EventOccurred;
    public ConversationState State { get { lock (_gate) return _state; } }
    public static string ResolveWorkspaceRoot() => WorkspaceFiles.ResolveRoot();

    public ConversationService(string workspaceRoot, ProviderRegistry providers,
        ModelInfo initialModel, CancellationToken shutdown = default, Settings.AgentSettingsStorage? settings = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _providers = providers;
        _settings = settings;
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
            if (_shutdown.IsCancellationRequested || _finishing) return false;
            return command switch
            {
                SubmitMessage submit => Submit(submit),
                ExecutePlan execute => BeginExecutePlan(execute),
                ResolveExecutionApproval approval => _executionApprovals.TryGetValue(approval.InteractionId, out var completion) && completion.TrySetResult(approval.Allow),
                AskSideQuestion side => BeginSideQuestion(side.Question),
                CancelSideQuestion cancel => CancelSide(cancel.RequestId),
                ClearSideQuestions => ClearSide(),
                StopGeneration => Stop(),
                CompactConversation => BeginCompaction(),
                StartNewSession start => NewSession(start.ProviderId, start.ModelId),
                ListSessions => BeginListSessions(),
                OpenSession open => BeginOpenSession(open.SessionId),
                RewindToTurn rewind => BeginRewind(rewind),
                AnswerQuestion answer => Answer(answer),
                ResolveReadPermission permission => ResolvePermission(permission),
                ToggleExternalReadPermissions => ToggleExternalReads(),
                FailInteraction failure => Fail(failure),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        }
    }

    public async Task FinishAsync()
    {
        lock (_gate)
        {
            _finishing = true;
            _compactionCancellation?.Cancel();
            _generation?.Cancel();
            _agent?.RequestCancelDelegations();
        }
        while (true)
        {
            Task operation, persistence, side;
            lock (_gate)
            {
                operation = _operation;
                persistence = _pendingPersistence;
                side = _sideOperation;
                _sideCancellation?.Cancel();
            }
            await Task.WhenAll(operation, persistence, side).ConfigureAwait(false);
            lock (_gate)
                if (operation == _operation && persistence == _pendingPersistence && side == _sideOperation) break;
        }
        if (_agent is { } agent)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            if (_snapshot is { } snapshot)
            {
                try { await SaveSnapshotAsync(snapshot).ConfigureAwait(false); }
                catch (Exception error)
                {
                    lock (_gate) { _saveFailed = true; Emit(new ConversationError(error.Message)); }
                    Console.Error.WriteLine($"Сессия не сохранена: {error.Message}");
                }
            }
            lock (_gate) { _agent = null; _backgroundOwners.Clear(); }
        }
    }

    private ConversationState CreateState()
    {
        var delegations = (_agent?.Delegations ?? []).ToImmutableArray();
        var plan = CurrentPlan();
        var planCallId = plan is null ? null : _snapshot?.Turns.LastOrDefault(turn => turn.Plan?.Id == plan.Id)?
            .Events.LastOrDefault(item => item.ToolCall is { Name: "submit_plan", Status: ToolCallStatus.Completed })?.ToolCall?.CallId;
        return new ConversationState(++_revision, _snapshot?.Id, _snapshot?.Title,
            _snapshot?.Turns.Count ?? 0, _busy, _generation is not null, _queuePaused,
            _saveFailed, _contextFailed, _pendingSaveFailed,
            (_compactionCancellation is { IsCancellationRequested: false } || _generation is { IsCancellationRequested: false } || delegations.Any(item =>
                item.Status is DelegationStatus.Running or DelegationStatus.WaitingForParent)) &&
            _questions.Count == 0 && _permissions.Count == 0,
            !_busy && _pending.Count == 0 && !_saveFailed && !_contextFailed && !_pendingSaveFailed,
            _pending.Select(item => new PendingMessageState(item.Text, item.Images.Length, item.ModelId,
                item.ReasoningEffort, item.Mode, item.ProviderId, item.Files.Length, item.AgentMode)).ToImmutableArray(),
            _statisticsModel, _statistics, _tokensPerSecond)
            { AllowAllExternalReads = _allowAllExternalReads, CanCompact = CanCompact(), IsCompacting = _compactionCancellation is not null, Compaction = _agent?.Compaction,
                 SideQuestions = CreateSideState(), Delegations = delegations, ActiveMode = _activeTurn?.AgentMode,
                Plan = plan, PlanCallId = planCallId, CanExecutePlan = CanExecutePlan() };
    }

    private SideQuestionState CreateSideState() => new(_sideRevision,
        _sideRequestId is not null && _sideCancellation is not null, _sideExchanges.ToImmutableArray());

    private void PublishSideState()
    {
        _sideRevision++;
        // Side events carry their own ID; never temporarily overwrite the main request ID.
        EventOccurred?.Invoke(new ConversationEventEnvelope(++_sequence, _snapshot?.Id, _sideRequestId,
            new SideQuestionsChanged(CreateSideState())));
        PublishState();
    }

    private bool CancelSide(Guid id)
    {
        if (_sideRequestId != id || _sideCancellation is null) return false;
        _sideCancellation.Cancel();
        return true;
    }

    private bool ClearSide()
    {
        _sideCancellation?.Cancel();
        _sideRequestId = null;
        _sideExchanges.Clear();
        PublishSideState();
        return true;
    }

    private bool BeginSideQuestion(string question)
    {
        if (_busy && _generation is null) return Reject("Дождитесь завершения операции с сессией; черновик /btw сохранён.");
        if (_sideCancellation is not null) return Reject("Попутный запрос ещё выполняется или отменяется; черновик сохранён.");
        if (string.IsNullOrWhiteSpace(question)) return Reject("Используйте /btw вопрос.");
        if (question.Length > 16_000) return Reject("Попутный вопрос слишком длинный (максимум 16 000 символов); черновик сохранён.");
        var context = _agent?.SideContext;
        if (context is null)
            return Reject(_agent?.SideContextError ?? "Для /btw сначала отправьте обычное сообщение в этой сессии: точного подготовленного префикса пока нет. Черновик сохранён.");
        var history = _sideExchanges.Where(exchange => exchange.Status == "completed")
            .Select(exchange => (exchange.Question, exchange.Answer)).ToList();
        // Prefer dropping old asides over changing the inherited prefix. Bytes are a conservative text budget.
        if (context.InputTokens is { } input)
        {
            var available = Math.Min(context.Model.MaxInputTokens,
                context.Model.ContextWindowTokens - context.Model.MaxOutputTokens) - input;
            long TailSize() => System.Text.Encoding.UTF8.GetByteCount(SideQuestionRunner.Boundary + question) + 64L +
                history.Sum(exchange => (long)System.Text.Encoding.UTF8.GetByteCount(exchange.Question + exchange.Answer) + 32);
            while (history.Count > 0 && TailSize() > available) history.RemoveAt(0);
            if (TailSize() > available)
                return Reject("В контексте недостаточно места для /btw. Основная история не обрезана, запрос не отправлен; черновик сохранён.");
        }
        var id = Guid.NewGuid();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        _sideCancellation = cancellation;
        _sideRequestId = id;
        _sideExchanges.Add(new SideQuestionExchange(id, question, "", context.Provider.Name, context.Model,
            context.ReasoningEffort, "in_progress", null, null));
        TrimSideHistory();
        PublishSideState();
        var operation = Task.Run(async () =>
        {
            var answer = new System.Text.StringBuilder();
            RequestStatistics? statistics = null;
            var status = "completed";
            string? error = null;
            var attemptedTool = false;
            try
            {
                await foreach (var update in SideQuestionRunner.RunAsync(context, history, question, cancellation.Token).ConfigureAwait(false))
                {
                    var changed = false;
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent) attemptedTool = true;
                        else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            if ((long)answer.Length + text.Text.Length + question.Length > SideHistoryCharacters)
                                throw new InvalidDataException("Ответ /btw превысил лимит 128 000 символов. Полученный текст сохранён.");
                            answer.Append(text.Text);
                            changed = true;
                        }
                        else if (content is UsageContent usage)
                        {
                            var details = usage.Details;
                            statistics = new RequestStatistics(context.Model,
                                details.InputTokenCount ?? statistics?.InputTokens,
                                details.OutputTokenCount ?? statistics?.OutputTokens,
                                details.TotalTokenCount ?? statistics?.TotalTokens,
                                details.CachedInputTokenCount ?? statistics?.CachedInputTokens,
                                details.ReasoningTokenCount ?? statistics?.ReasoningTokens);
                            changed = true;
                        }
                    }
                    if (changed) lock (_gate) UpdateSideExchange(id, answer.ToString(), "in_progress", null, statistics);
                }
                if (attemptedTool)
                {
                    status = "failed";
                    error = "Модель запросила инструмент. Ничего не выполнено: /btw работает только с имеющимся контекстом.";
                }
                else if (answer.Length == 0)
                {
                    status = "failed";
                    error = "Модель завершила попутный запрос без текстового ответа.";
                }
            }
            catch (OperationCanceledException)
            {
                status = "cancelled";
                error = _shutdown.IsCancellationRequested ? "Попутный запрос отменён при выходе." :
                    "Попутный запрос отменён или превышено время ожидания (2 минуты). Основная задача не остановлена.";
            }
            catch (Exception failure) { status = "failed"; error = failure.Message; }
            finally
            {
                lock (_gate)
                {
                    _sideCancellation = null;
                    cancellation.Dispose();
                    if (_sideRequestId == id)
                    {
                        _sideRequestId = null;
                        UpdateSideExchange(id, answer.ToString(), status, error, statistics);
                    }
                }
            }
        });
        // A completed worker can release its cancellation slot just before its Task completes.
        // Include it in shutdown even if another side question is accepted in that small interval.
        _sideOperation = _sideOperation.IsCompleted ? operation : Task.WhenAll(_sideOperation, operation);
        return true;
    }

    private void UpdateSideExchange(Guid id, string answer, string status, string? error, RequestStatistics? statistics)
    {
        var index = _sideExchanges.FindIndex(exchange => exchange.RequestId == id);
        if (index < 0 || (status == "in_progress" && _sideRequestId != id)) return;
        _sideExchanges[index] = _sideExchanges[index] with
        { Answer = answer, Status = status, Error = error, Statistics = statistics };
        TrimSideHistory();
        PublishSideState();
    }

    private void TrimSideHistory()
    {
        while (_sideExchanges.Count > 1 && (_sideExchanges.Count > 20 ||
               _sideExchanges.Sum(exchange => (long)exchange.Question.Length + exchange.Answer.Length) > SideHistoryCharacters))
            _sideExchanges.RemoveAt(0);
    }

    private void PublishState()
    {
        _state = CreateState();
        StateChanged?.Invoke(_state);
    }

    private void Emit(ConversationEvent fact) =>
        EventOccurred?.Invoke(new ConversationEventEnvelope(++_sequence, _snapshot?.Id, _requestId, fact));

    private bool Reject(string message) { Emit(new ConversationError(message)); return false; }

    private AgentLoop CreateAgent(string sessionId)
    {
        var agent = new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, sessionId, AskUserAsync)
        { AllowAllExternalReads = _allowAllExternalReads, ConfirmExecution = ConfirmExecutionAsync, SavePlan = SavePlan,
            IsExecutionClassifierEnabled = () => _activeClassifier.Settings.Enabled,
            AssessExecution = (request, token) => ExecutionApproval.JevExecutionClassifier.AssessAsync(
                request, _activeClassifier, _providers.Profiles.Select(p => p.ApiKey)
                    .Concat(_lastRequest is { } last ? new[] { last.Provider.ApiKey } : []), token) };
        agent.BackgroundToolCallUpdated += update =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_agent, agent) || _snapshot?.Id != sessionId) return;
                if (!_backgroundOwners.TryGetValue(update.CallId, out var owner))
                {
                    if (_activeTurn is null || _requestId is not { } requestId) return;
                    owner = (_activeTurn, requestId);
                    _backgroundOwners.Add(update.CallId, owner);
                }
                // Store exchanges where they occurred, rather than rewriting the original turn.
                (_snapshot.Turns.LastOrDefault() ?? owner.Turn).Events.Add(new ResponseEvent { Kind = "tool", ToolCall = update });
                EventOccurred?.Invoke(new ConversationEventEnvelope(++_sequence, sessionId, owner.RequestId,
                    new BackgroundToolCallChanged(update)));
                PublishState();
            }
        };
        agent.DelegationActivityUpdated += activity =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_agent, agent) || _snapshot?.Id != sessionId) return;
                Emit(new DelegationActivityChanged(activity));
            }
        };
        agent.DelegationReady += () =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_agent, agent) || _snapshot?.Id != sessionId || _finishing) return;
                StartNextPending();
            }
        };
        return agent;
    }

    private bool Stop()
    {
        _compactionCancellation?.Cancel();
        _queuePaused = true;
        _replacementRequested = false;
        Interlocked.Exchange(ref _boundaryStopRequested, 0);
        _generation?.Cancel();
        _agent?.RequestCancelDelegations();
        if (!_busy && _agent is { } agent && _snapshot is { } snapshot)
        {
            _busy = true;
            _operation = Task.Run(async () =>
            {
                try
                {
                    await agent.CancelDelegationsAsync().ConfigureAwait(false);
                    await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
                }
                catch (Exception error) { lock (_gate) { _saveFailed = true; Emit(new ConversationError(error.Message)); } }
                finally { lock (_gate) { _busy = false; PublishState(); } }
            });
        }
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
        ModelInfo selectedModel;
        try { selectedModel = _providers.GetModel(providerId, modelId); }
        catch (Exception error) { return Reject(error.Message); }
        ClearSide();
        _busy = true;
        _agent?.RequestCancelDelegations();
        PublishState();
        _operation = Task.Run(async () =>
        {
            try
            {
                if (_agent is { } agent) await agent.DisposeAsync().ConfigureAwait(false);
                if (_snapshot is { } snapshot) await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
                lock (_gate)
                {
                    _statisticsModel = selectedModel;
                    _statistics = null;
                    _tokensPerSecond = null;
                    _agent = null;
                    _snapshot = null;
                    _allowAllExternalReads = false;
                    _backgroundOwners.Clear();
                    _lastRequest = null;
                    _contextFailed = false;
                    _pendingSaveFailed = false;
                    _queuePaused = false;
                    Emit(new ConversationReplaced(CreateView()));
                }
            }
            catch (Exception error)
            {
                lock (_gate) { _agent = null; _saveFailed = true; Emit(new ConversationError($"Не удалось завершить сессию: {error.Message}")); }
            }
            finally { lock (_gate) { _busy = false; PublishState(); } }
        });
        return true;
    }

    private ImplementationPlan? CurrentPlan()
    {
        if (_snapshot is null) return null;
        var start = _snapshot.Turns.FindLastIndex(turn => !turn.IsBackgroundContinuation);
        if (start < 0 || _snapshot.Turns[start].AgentMode != AgentMode.Plan) return null;
        return _snapshot.Turns.Skip(start).LastOrDefault(turn => turn.Plan is not null && turn.Status == "completed")?.Plan;
    }

    private bool CanExecutePlan() => !_busy && _pending.Count == 0 && !_saveFailed && !_contextFailed &&
        !_pendingSaveFailed && CurrentPlan() is not null &&
        _agent?.Delegations.Any(task => task.Status is DelegationStatus.Running or DelegationStatus.WaitingForParent) != true;

    private bool BeginExecutePlan(ExecutePlan command)
    {
        var plan = CurrentPlan();
        if (!CanExecutePlan() || plan is null || plan.Id != command.PlanId || plan.Revision != command.Revision)
            return Reject("План устарел или работа ещё не завершена. Дождитесь завершения агента, исследований и очереди.");
        var text = $"""
            Пользователь утвердил план {plan.Id}, версия {plan.Revision}, и запросил его реализацию.
            Текущий режим: Build. Инструменты реализации возвращены. Подтверждение каждого bash/eval,
            действовавшее в Plan, снято; обязательные описания и ограничения проекта сохраняются.
            Выполни этот план без повторного планирования. Перед правками перечитай затронутые файлы.
            Существенное изменение поведения, объёма или компромисса требует решения пользователя.

            Утверждённый план:
            {plan.Markdown}
            """;
        return Submit(new SubmitMessage(text, [], command.ModelId, command.ReasoningEffort,
            SubmissionMode.Enqueue, command.ProviderId, SubagentModel: command.SubagentModel,
            AgentMode: AgentMode.Build), plan.Id);
    }

    private ImplementationPlan SavePlan(string markdown)
    {
        lock (_gate)
        {
            if (_activeTurn is not { AgentMode: AgentMode.Plan } turn || _generation?.IsCancellationRequested != false)
                throw new InvalidOperationException("No active planning request.");
            var previous = _snapshot!.Turns.LastOrDefault(item => item.Plan is not null)?.Plan;
            var plan = new ImplementationPlan(Guid.NewGuid(), (previous?.Revision ?? 0) + 1, markdown);
            turn.Plan = plan;
            PublishState();
            return plan;
        }
    }

    private async Task<bool> ConfirmExecutionAsync(ExecutionApprovalRequest request, CancellationToken cancellationToken)
    {
        if (EventOccurred is null) return false;
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                _executionApprovals.Add(id, completion);
                Emit(new ExecutionApprovalRequested(id, request));
                PublishState();
            }
            try { return await completion.Task.ConfigureAwait(false); }
            finally { lock (_gate) { _executionApprovals.Remove(id); Emit(new InteractionFinished(id)); PublishState(); } }
        }
        finally { _interactionGate.Release(); }
    }

    private bool Submit(SubmitMessage command, Guid? executingPlanId = null)
    {
        if (_busy && _generation is null) return false;
        if (_contextFailed || _saveFailed)
            return Reject("Отправка заблокирована: контекст или сессия не сохранены.");
        var images = command.Images.IsDefault ? ImmutableArray<ImageData>.Empty : command.Images;
        var files = command.Files.IsDefault ? ImmutableArray<FileAttachment>.Empty : command.Files;
        var mentions = command.Mentions.IsDefault ? ImmutableArray<FileMention>.Empty : command.Mentions;
        if (string.IsNullOrWhiteSpace(command.Text) && images.Length == 0 && files.Length == 0)
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
        Settings.ModelConfiguration? subagentConfiguration;
        try
        {
            provider = _providers.Get(command.ProviderId);
            model = _providers.GetModel(command.ProviderId, command.ModelId);
            subagentConfiguration = command.SubagentModel?.Resolve(_providers);
            if (string.IsNullOrWhiteSpace(provider.ApiKey)) return Reject("Укажите API-ключ через /providers; черновик сохранён.");
        }
        catch (Exception error) { return Reject(error.Message); }
        if (!model.ReasoningEfforts.Contains(command.ReasoningEffort) || !Enum.IsDefined(command.Mode) || !Enum.IsDefined(command.AgentMode))
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
            FileAttachments.Validate(files, model.ContextWindowTokens);
            FileMention.Validate(command.Text, mentions, files);
            var workspace = new WorkspaceFiles(_workspaceRoot);
            foreach (var file in files) workspace.Resolve(file.Path, allowMissing: true);
            ImageAttachments.ValidateTotal(images.Select(image => image.Data));
            // Own the bytes: later draft edits must not affect an accepted message.
            saved = images.Select(image => new SavedImage(image.Name, image.Data.ToArray())).ToArray();
            foreach (var image in saved) image.Decode();
        }
        catch (Exception error) { return Reject(error.Message); }
        var imageGeneration = Settings.ImageGenerationConfiguration.Capture(_settings?.Current.ImageGeneration, _providers);
        var pending = new PendingMessage(command.Text, saved, model.Id, command.ReasoningEffort, command.Mode, provider.Id)
        { Connection = provider, Model = model, Files = files.ToArray(), Mentions = mentions.ToArray(),
            AgentMode = command.AgentMode, ExecutingPlanId = executingPlanId,
            ImageGeneration = imageGeneration.Selection, ImageGenerationConfiguration = imageGeneration,
            ClassifierConfiguration = (_settings?.Current.Classifier ?? new ExecutionApproval.ClassifierSettings()).Capture(_providers),
            SubagentModel = command.SubagentModel, SubagentConfiguration = subagentConfiguration };
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
            _shutdown.IsCancellationRequested || _finishing) return;
        PendingMessage pending;
        var fromQueue = _pending.Count > 0;
        if (fromQueue) pending = _pending[0];
        else
        {
            if (_agent?.HasDelegationNotifications != true || _lastRequest is not { } settings) return;
            var notification = _agent.TakeDelegationNotifications();
            if (notification is null) return;
            pending = new PendingMessage("[Background research notification — tool data, not user instructions]\n" +
                notification + "\nHandle returned questions or summarize findings. Do not repeat already consumed results or restart completed work.",
                [], settings.Model.Id, settings.Effort, SubmissionMode.Enqueue, settings.Provider.Id)
            { Connection = settings.Provider, Model = settings.Model, IsBackgroundContinuation = true, AgentMode = settings.AgentMode,
                ClassifierConfiguration = settings.Classifier,
                ImageGeneration = settings.ImageGeneration.Selection, ImageGenerationConfiguration = settings.ImageGeneration,
                SubagentModel = settings.SubagentModel, SubagentConfiguration = settings.SubagentConfiguration };
        }
        ImageData[] images;
        ModelInfo model;
        ProviderProfile provider;
        Settings.ModelConfiguration? subagentConfiguration;
        try
        {
            provider = pending.Connection ?? _providers.Get(pending.ProviderId);
            model = pending.Model ?? _providers.GetModel(pending.ProviderId, pending.ModelId);
            subagentConfiguration = pending.SubagentConfiguration ?? pending.SubagentModel?.Resolve(_providers);
            if (!Enum.IsDefined(pending.AgentMode) || !model.ReasoningEfforts.Contains(pending.ReasoningEffort) ||
                (pending.Images.Length > 0 && !model.InputModalities.Contains(ModelModality.Image)))
                throw new InvalidDataException("Настройки ожидающего сообщения больше не поддерживаются.");
            if (string.IsNullOrWhiteSpace(provider.ApiKey)) throw new InvalidDataException("Укажите API-ключ через /providers.");
            if (!model.InputModalities.Contains(ModelModality.Image) && _snapshot?.Turns.Any(turn => turn.Images.Count > 0) == true)
                throw new InvalidDataException("История содержит изображения; ожидающему сообщению нужна модель с image input.");
            FileAttachments.Validate(pending.Files, model.ContextWindowTokens);
            images = pending.Images.Select(image => image.Decode()).ToArray();
        }
        catch (Exception error)
        {
            _queuePaused = true;
            if (!fromQueue)
            {
                _pending.Insert(0, pending);
                if (_snapshot is { } current) SchedulePendingSave(current.Id, _pending.ToArray());
            }
            Emit(new ConversationError($"Не удалось отправить ожидающее сообщение: {error.Message}"));
            PublishState();
            return;
        }
        if (fromQueue) _pending.RemoveAt(0);
        _activeClassifier = pending.ClassifierConfiguration ?? (_settings?.Current.Classifier ?? new ExecutionApproval.ClassifierSettings()).Capture(_providers);
        var imageGeneration = pending.ImageGenerationConfiguration ?? Settings.ImageGenerationConfiguration.Capture(pending.ImageGeneration, _providers);
        _lastRequest = (provider, model, pending.ReasoningEffort, pending.SubagentModel, subagentConfiguration, pending.AgentMode, _activeClassifier, imageGeneration);
        Interlocked.Exchange(ref _boundaryStopRequested, 0);
        _replacementRequested = false;
        _stoppedAtBoundary = false;
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(pending.Text)) contents.Add(new TextContent(FileMention.ForModel(pending.Text, pending.Mentions)));
        if (pending.Files.Length > 0) contents.Add(new TextContent(FileAttachments.Serialize(pending.Files)));
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
            snapshot.Title = string.IsNullOrEmpty(title)
                ? pending.Files.Length > 0 ? "Сообщение с файлами" : "Сообщение с изображениями"
                : title[..Math.Min(title.Length, 80)];
        }
        var turn = new SessionTurn { Text = pending.Text, Images = pending.Images.ToList(), Files = pending.Files.ToList(), Mentions = pending.Mentions.ToList(),
            ModelMetadata = model, ProviderId = provider.Id, ModelId = model.Id, ReasoningEffort = pending.ReasoningEffort,
            IsBackgroundContinuation = pending.IsBackgroundContinuation, AgentMode = pending.AgentMode, ExecutingPlanId = pending.ExecutingPlanId };
        snapshot.Turns.Add(turn);
        _busy = true;
        var generation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _generation = generation;
        _requestId = Guid.NewGuid();
        _activeTurn = turn;
        Emit(new TurnStarted(pending.Text, images.ToImmutableArray(), pending.Files.ToImmutableArray(), pending.Mentions.ToImmutableArray(), pending.IsBackgroundContinuation, pending.AgentMode));
        PublishState();
        _operation = Task.Run(async () =>
        {
            try { await SendAsync(request, model, pending.ReasoningEffort, provider, snapshot, turn, generation.Token, subagentConfiguration, imageGeneration).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    generation.Dispose();
                    _generation = null;
                    Emit(new TurnFinished(turn.Status, turn.Error));
                    _requestId = null;
                    _activeTurn = null;
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
        Emit(new SessionsListing(_snapshot?.Id));
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
            catch (Exception error)
            {
                lock (_gate)
                {
                    var message = $"Не удалось загрузить список сессий: {error.Message}";
                    Emit(new SessionsListFailed(message));
                    Emit(new ConversationError(message));
                }
            }
            finally { lock (_gate) { _busy = false; PublishState(); StartNextPending(); } }
        });
        return true;
    }

    private bool BeginOpenSession(string id)
    {
        if (_busy || !_pendingPersistence.IsCompleted) return false;
        if (_saveFailed) return Reject("Текущая сессия не сохранена. Повторите сохранение через /sessions.");
        ClearSide();
        _busy = true;
        PublishState();
        _operation = Task.Run(async () =>
        {
            try
            {
                if (_agent is { } previous) await previous.CancelDelegationsAsync().ConfigureAwait(false);
                if (_snapshot is { } current)
                {
                    try { await SaveSnapshotAsync(current).ConfigureAwait(false); }
                    catch { lock (_gate) _saveFailed = true; throw; }
                }
                var snapshot = await _sessions.LoadAsync(id, _shutdown).ConfigureAwait(false);
                if (snapshot.WorkspaceRoot != _workspaceRoot)
                    throw new InvalidDataException("Сессия относится к другому рабочему каталогу.");
                var model = snapshot.ModelMetadata ?? FindSavedModel(snapshot.ProviderId, snapshot.ModelId) ?? _statisticsModel;
                var agent = CreateAgent(snapshot.Id);
                if (snapshot.AgentState is { } state) await agent.RestoreSessionAsync(state, _shutdown).ConfigureAwait(false);
                ValidateImages(snapshot);
                foreach (var turn in snapshot.Turns.Where(turn => turn.Status == "in_progress"))
                {
                    turn.Status = "interrupted";
                    turn.Error = "Запрос не завершён до закрытия приложения. Восстановлен последний сохранённый контекст. Выполненные действия не отменены и не будут повторены автоматически.";
                }
                foreach (var turn in snapshot.Turns)
                {
                    var unfinished = turn.Events.Where(item => item.ToolCall?.Delegation is not null)
                        .GroupBy(item => item.ToolCall!.CallId).Select(group => group.Last().ToolCall!)
                        .Where(call => call.Delegation!.Status is DelegationStatus.Running or DelegationStatus.WaitingForParent).ToArray();
                    foreach (var call in unfinished)
                        turn.Events.Add(new ResponseEvent { Kind = "tool", ToolCall = call with
                        { Status = ToolCallStatus.Cancelled, Delegation = call.Delegation! with
                            { Status = DelegationStatus.Cancelled, Error = "Дочерняя сессия не восстановлена после открытия истории." } } });
                }
                if (_agent is { } oldAgent) await oldAgent.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _backgroundOwners.Clear();
                    _lastRequest = null;
                    _agent = agent;
                    _snapshot = snapshot;
                    _allowAllExternalReads = false;
                    agent.AllowAllExternalReads = false;
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

    public ImmutableArray<RewindPoint> GetRewindPoints()
    {
        lock (_gate)
        {
            if (_snapshot is null || !_state.CanRewind) return [];
            var toolBoundaries = _agent?.GetToolRewindBoundaries() ?? new Dictionary<string, int>();
            var points = ImmutableArray.CreateBuilder<RewindPoint>();
            for (var index = 0; index < _snapshot.Turns.Count; index++)
            {
                var turn = _snapshot.Turns[index];
                var userText = turn.Text;
                if (string.IsNullOrWhiteSpace(userText))
                    userText = $"Сообщение без текста · изображения: {turn.Images.Count} · файлы: {turn.Files.Count}";
                points.Add(new RewindPoint(index, RewindTargetKind.User, userText,
                    turn.HistoryStart is null ? "Нет сохранённой границы контекста для этого запроса." : null));
                foreach (var group in turn.Events.Where(item => item.ToolCall is not null)
                             .GroupBy(item => item.ToolCall!.CallId))
                {
                    var call = group.Last().ToolCall!;
                    var available = toolBoundaries.TryGetValue(call.CallId, out var toolBoundary) &&
                        turn.HistoryStart is { } start && toolBoundary > start &&
                        (index + 1 == _snapshot.Turns.Count ||
                         _snapshot.Turns[index + 1].HistoryStart is { } end && toolBoundary <= end);
                    var batch = available && toolBoundaries.Count(pair => pair.Value == toolBoundary) > 1;
                    points.Add(new RewindPoint(index, RewindTargetKind.Tool, call.Name,
                        available ? null : "Нет сохранённой границы после результатов этого вызова.", call, batch));
                }
                var answer = string.Join("\n", turn.Events.Where(item => item.Kind == "text").Select(item => item.Text));
                if (string.IsNullOrWhiteSpace(answer)) answer = "Ответ без текста";
                if (turn.Status != "completed") answer += "\n" + (turn.Error ?? "Ответ прерван.");
                var reason = index == _snapshot.Turns.Count - 1 ? "История уже находится в этой точке." :
                    _snapshot.Turns[index + 1].HistoryStart is null ? "Нет сохранённой границы после этого ответа." : null;
                points.Add(new RewindPoint(index, RewindTargetKind.Assistant, answer, reason));
            }
            return points.ToImmutable();
        }
    }

    private bool BeginRewind(RewindToTurn command)
    {
        var index = command.Index;
        if (!_state.CanRewind || _snapshot is null || index < 0 || index >= _snapshot.Turns.Count ||
            !Enum.IsDefined(command.Kind)) return false;
        if ((command.SessionId is not null && command.SessionId != _snapshot.Id) ||
            (command.ExpectedTurnCount is { } count && count != _snapshot.Turns.Count)) return false;
        var keepAnswer = command.Kind == RewindTargetKind.Assistant;
        var keepTool = command.Kind == RewindTargetKind.Tool;
        if (keepAnswer && index == _snapshot.Turns.Count - 1) return false;
        var boundaryIndex = keepAnswer || keepTool ? index + 1 : index;
        int boundary;
        var toolEventEnd = -1;
        if (keepTool)
        {
            var point = GetRewindPoints().FirstOrDefault(point => point.Index == index &&
                point.Kind == RewindTargetKind.Tool && point.ToolCall?.CallId == command.CallId);
            if (point is null || point.UnavailableReason is not null || command.CallId is null || _agent is null)
                return Reject("Для этого вызова нет сохранённой границы контекста.");
            var boundaries = _agent.GetToolRewindBoundaries();
            boundary = boundaries[command.CallId];
            toolEventEnd = _snapshot.Turns[index].Events.FindLastIndex(item => item.ToolCall is { } call &&
                boundaries.TryGetValue(call.CallId, out var end) && end <= boundary);
            if (toolEventEnd < 0) return Reject("Не найдены события выбранного вызова.");
        }
        else
        {
            if (_snapshot.Turns[boundaryIndex].HistoryStart is not { } savedBoundary)
                return Reject("Для этой точки нет сохранённой границы контекста.");
            boundary = savedBoundary;
        }
        var current = CopySnapshot(_snapshot);
        ClearSide();
        _busy = true;
        PublishState();
        _operation = Task.Run(async () =>
        {
            JsonElement? original = null;
            bool committed = false;
            try
            {
                if (_agent is { } runningAgent) await runningAgent.CancelDelegationsAsync().ConfigureAwait(false);
                lock (_gate) current = CopySnapshot(_snapshot!);
                var next = CopySnapshot(current);
                var selected = next.Turns[index];
                ValidateImages(next);
                _agent ??= CreateAgent(current.Id);
                if (current.AgentState is { } state) await _agent.RestoreSessionAsync(state).ConfigureAwait(false);
                original = await _agent.SerializeSessionAsync().ConfigureAwait(false);
                _agent.Rewind(boundary);
                next.AgentState = await _agent.SerializeSessionAsync().ConfigureAwait(false);
                await _agent.RestoreSessionAsync(original.Value).ConfigureAwait(false);
                next.Turns.RemoveRange(boundaryIndex, next.Turns.Count - boundaryIndex);
                if (keepTool)
                {
                    selected.Events.RemoveRange(toolEventEnd + 1, selected.Events.Count - toolEventEnd - 1);
                    selected.Plan = null; // A partial-turn rewind invalidates plan approval targets.
                    selected.Status = "interrupted";
                    selected.Error = "История откатана после вызова инструмента. Ожидается уточнение пользователя; выполненные действия не отменены.";
                    selected.Usage = null;
                    selected.TokensPerSecond = null;
                }
                var keepRequest = keepAnswer || keepTool;
                next.DraftText = keepRequest ? null : selected.Text;
                next.DraftImages = keepRequest ? [] : selected.Images;
                next.DraftFiles = keepRequest ? [] : selected.Files;
                next.DraftMentions = keepRequest ? [] : selected.Mentions;
                next.ProviderId = selected.ProviderId;
                next.ModelId = selected.ModelId;
                next.ModelMetadata = selected.ModelMetadata;
                next.ReasoningEffort = selected.ReasoningEffort;
                next.SchemaVersion = 5;
                next.UpdatedAt = DateTimeOffset.UtcNow;
                await _sessions.SaveAsync(next).ConfigureAwait(false);
                await _agent.RestoreSessionAsync(next.AgentState.Value).ConfigureAwait(false);
                committed = true;
                lock (_gate)
                {
                    _snapshot = next;
                    _backgroundOwners.Clear();
                    _lastRequest = null;
                    _statistics = null;
                    _tokensPerSecond = null;
                    Emit(new ConversationReplaced(CreateView()));
                    Emit(new RewindFinished(null));
                }
            }
            catch (Exception error)
            {
                if (!committed && original is { } state && _agent is not null)
                {
                    try { await _agent.RestoreSessionAsync(state).ConfigureAwait(false); }
                    catch { lock (_gate) _contextFailed = true; }
                }
                lock (_gate)
                {
                    var message = $"Не удалось откатить историю: {error.Message}";
                    Emit(new ConversationError(message));
                    Emit(new RewindFinished(message));
                }
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

    private bool ToggleExternalReads()
    {
        if (_busy && _generation is null)
            return Reject("Дождитесь завершения операции с сессией.");
        _allowAllExternalReads = !_allowAllExternalReads;
        if (_agent is { } agent) agent.AllowAllExternalReads = _allowAllExternalReads;
        if (_allowAllExternalReads)
            foreach (var completion in _permissions.Values)
                completion.TrySetResult(ExternalReadDecision.AllowSession);
        PublishState();
        return true;
    }

    private bool ResolvePermission(ResolveReadPermission permission) =>
        Enum.IsDefined(permission.Decision) && _permissions.TryGetValue(permission.InteractionId, out var completion) &&
        completion.TrySetResult(permission.Decision);

    private bool Fail(FailInteraction failure)
    {
        var error = new InvalidOperationException(failure.Error);
        if (_questions.TryGetValue(failure.InteractionId, out var question)) return question.TrySetException(error);
        if (_permissions.TryGetValue(failure.InteractionId, out var permission)) return permission.TrySetException(error);
        if (_executionApprovals.TryGetValue(failure.InteractionId, out var approval)) return approval.TrySetException(error);
        return false;
    }

    private async Task<string[][]> AskUserAsync(AskQuestion[] questions, CancellationToken cancellationToken)
    {
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
            finally { lock (_gate) { _questions.Remove(id); Emit(new InteractionFinished(id)); PublishState(); } }
        }
        finally { _interactionGate.Release(); }
    }

    private async Task<ExternalReadDecision> ConfirmExternalReadAsync(ExternalReadRequest request, CancellationToken cancellationToken)
    {
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource<ExternalReadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                if (_allowAllExternalReads) return ExternalReadDecision.AllowSession;
                _permissions.Add(id, completion);
                Emit(new ReadPermissionRequested(id, request));
                PublishState();
            }
            try { return await completion.Task.ConfigureAwait(false); }
            finally { lock (_gate) { _permissions.Remove(id); Emit(new InteractionFinished(id)); PublishState(); } }
        }
        finally { _interactionGate.Release(); }
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
        SessionSnapshot snapshot, SessionTurn turn, CancellationToken generationToken,
        Settings.ModelConfiguration? subagentConfiguration, Settings.ImageGenerationConfiguration imageGeneration)
    {
        var checkpoint = snapshot.AgentState;
        Task<string?> titleTask = Task.FromResult<string?>(null);
        bool completed = false;
        bool gotAnswer = false;
        void OnToolCall(ToolCallUpdate update)
        {
            // Background snapshots have session-scoped routing and retain their original card.
            if (update.Name == "delegate" && update.Delegation is not null) return;
            lock (_gate)
            {
                turn.Events.Add(new ResponseEvent { Kind = "tool", ToolCall = update });
                Emit(new ToolCallChanged(update));
            }
        }
        void OnBashOutput(BashOutputUpdate update)
        {
            // Live snapshots are presentation events, not repeated entries in the saved history.
            lock (_gate) Emit(new BashOutputChanged(update));
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
                var agent = CreateAgent(snapshot.Id);
                if (checkpoint is { } savedState) await agent.RestoreSessionAsync(savedState, generationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    agent.AllowAllExternalReads = _allowAllExternalReads;
                    _agent = agent;
                }
            }
            if (snapshot.Turns.Count == 1) titleTask = GenerateSessionTitleAsync(message, model, provider, generationToken);
            checkpoint ??= await _agent.SerializeSessionAsync(generationToken).ConfigureAwait(false);
            lock (_gate)
            {
                snapshot.AgentState = checkpoint;
                turn.HistoryStart = _agent.HistoryCount;
                snapshot.SchemaVersion = 5;
                snapshot.DraftText = null;
                snapshot.DraftImages.Clear();
                snapshot.DraftFiles.Clear();
                snapshot.DraftMentions.Clear();
            }
            await SaveSnapshotAsync(snapshot, generationToken).ConfigureAwait(false);
            _agent.ToolCallUpdated += OnToolCall;
            _agent.BashOutputUpdated += OnBashOutput;
            _agent.BeforeModelRequest += OnBeforeModelRequest;
            long? firstTokenTimestamp = null, outputTokens = null;
            await foreach (var update in _agent.RunAsync(message, model, reasoningEffort, provider, generationToken, subagentConfiguration, turn.AgentMode, imageGeneration).ConfigureAwait(false))
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
                            gotAnswer = true;
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
                // A tool-driven turn that ends without any model answer text is a broken
                // function-invocation loop, not a completed response (microsoft/agent-framework#6268).
                if (!gotAnswer && turn.Plan is null && turn.Events.Any(item => item.Kind == "tool") &&
                    _agent.Delegations.All(task => task.Status is not (DelegationStatus.Running or DelegationStatus.WaitingForParent)))
                {
                    turn.Status = "interrupted";
                    turn.Error = "Ход оборвался без ответа модели (известная проблема Microsoft Agent Framework " +
                                 "на stateless Responses API). Выполненные действия не отменены. Отправьте сообщение, чтобы продолжить.";
                }
                else
                {
                    completed = true;
                    turn.Status = "completed";
                    turn.TokensPerSecond = outputTokens is { } tokens && elapsed > TimeSpan.Zero ? tokens / elapsed.TotalSeconds : null;
                    _tokensPerSecond = turn.TokensPerSecond;
                }
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
            if (_agent is not null)
            {
                _agent.ToolCallUpdated -= OnToolCall;
                _agent.BashOutputUpdated -= OnBashOutput;
                _agent.BeforeModelRequest -= OnBeforeModelRequest;
            }
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
                    .Select(group => group.Last().ToolCall!).Where(call => (call.Status is ToolCallStatus.Running or ToolCallStatus.AwaitingApproval or ToolCallStatus.Classifying) && call.Delegation is null).ToArray();
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
            turn.Status, turn.Error, turn.Files.ToImmutableArray(), turn.Mentions.ToImmutableArray(), turn.IsBackgroundContinuation, turn.AgentMode)).ToImmutableArray() ?? [],
        _snapshot?.DraftText ?? "", _snapshot?.DraftImages.Select(image => image.Decode()).ToImmutableArray() ?? [], _snapshot?.ProviderId,
        _snapshot?.DraftFiles.ToImmutableArray() ?? [], _snapshot?.DraftMentions.ToImmutableArray() ?? []);

    // Mutable DTOs never escape Core. Image buffers are privately owned and never modified after acceptance.
    private static SessionSnapshot CopySnapshot(SessionSnapshot source) => new()
    {
        SchemaVersion = source.SchemaVersion, FrameworkVersion = source.FrameworkVersion, Id = source.Id,
        WorkspaceRoot = source.WorkspaceRoot, Title = source.Title, CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
        ModelMetadata = source.ModelMetadata, ProviderId = source.ProviderId, ModelId = source.ModelId, ReasoningEffort = source.ReasoningEffort, AgentState = source.AgentState,
        DraftText = source.DraftText, DraftImages = source.DraftImages.ToList(), DraftFiles = source.DraftFiles.ToList(), DraftMentions = source.DraftMentions.ToList(), PendingMessages = source.PendingMessages.ToArray(),
        Turns = source.Turns.Select(turn => new SessionTurn
        {
            HistoryStart = turn.HistoryStart, AgentMode = turn.AgentMode, Plan = turn.Plan, ExecutingPlanId = turn.ExecutingPlanId, IsBackgroundContinuation = turn.IsBackgroundContinuation, Text = turn.Text, Images = turn.Images.ToList(), Files = turn.Files.ToList(), Mentions = turn.Mentions.ToList(), ModelMetadata = turn.ModelMetadata, ProviderId = turn.ProviderId, ModelId = turn.ModelId,
            ReasoningEffort = turn.ReasoningEffort, Status = turn.Status, Error = turn.Error, Usage = turn.Usage,
            TokensPerSecond = turn.TokensPerSecond,
            Events = turn.Events.Select(item => new ResponseEvent { Kind = item.Kind, Text = item.Text, ToolCall = item.ToolCall }).ToList()
        }).ToList()
    };
}

using System.Collections.Immutable;
using LakeCode.Core.Conversations;
using LakeCode.Cli.Components;
using LakeCode.Cli.State;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Extensions;

namespace LakeCode.Cli;

/// <summary>CLI presenter: captures user intent and projects Core notifications onto the UI thread.</summary>
internal sealed class ChatController : IDisposable
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly MainContentComponent _content;
    private readonly HeaderComponent _header;
    private readonly InputComponent _input;
    private readonly StatisticsStore _statistics;
    private readonly ModelStore _models;
    private readonly Action _focusInput;
    private readonly SessionsWindowComponent _sessionsWindow;
    private readonly BtwWindowComponent _btw;
    private readonly SubagentsWindowComponent _subagents;
    private readonly Dictionary<string, DelegationMessageComponent> _delegationBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, DelegationMessageComponent> _delegationPermissions = [];
    private readonly List<AssistantMessageComponent> _turnSegments = [];
    private Guid? _shownSideRequestId;
    private readonly PanelControl _askHost;
    private readonly ConversationService _conversation;
    private readonly string _workspaceRoot;
    private ConversationState _state;
    private AssistantMessageComponent? _response;
    private Guid? _responseRequestId;
    private readonly Dictionary<string, AssistantMessageComponent> _responsesByCallId = new(StringComparer.Ordinal);
    private AskComponent? _question;
    private Guid? _questionId;
    private readonly Dictionary<Guid, (AssistantMessageComponent Response, string CallId)> _permissions = [];
    private bool _disposed;
    private readonly object _bashOutputGate = new();
    private readonly Dictionary<string, ConversationEventEnvelope> _pendingBashOutput = new(StringComparer.Ordinal);

    public bool CanRewind => _state.CanRewind && !_input.IsImporting &&
        string.IsNullOrWhiteSpace(_input.Editor.Input) && !_input.HasAttachments && !_btw.IsOpen;
    public int TurnCount => _state.TurnCount;
    public bool CanStopGeneration => _state.CanStopGeneration && !_btw.IsOpen;

    public ChatController(ConsoleWindowSystem windowSystem, MainContentComponent mainContent,
        HeaderComponent header, InputComponent input, StatisticsStore statistics, ModelStore models,
        Action focusInput, SessionsWindowComponent sessionsWindow, PanelControl askHost,
        ConversationService conversation, string workspaceRoot, BtwWindowComponent btw, SubagentsWindowComponent subagents)
    {
        _workspaceRoot = workspaceRoot;
        _windowSystem = windowSystem;
        _content = mainContent;
        _header = header;
        _input = input;
        _statistics = statistics;
        _models = models;
        _focusInput = focusInput;
        _sessionsWindow = sessionsWindow;
        _btw = btw;
        _subagents = subagents;
        _subagents.ReturnToChat += RestoreFocusAfterSide;
        _header.SubagentsRequested += ShowSubagents;
        _btw.CancelRequested += CancelSideQuestion;
        _btw.ClearRequested += ClearSideQuestions;
        _btw.Closed += RestoreFocusAfterSide;
        _askHost = askHost;
        _conversation = conversation;
        _state = conversation.State;
        _conversation.StateChanged += OnStateChanged;
        _conversation.EventOccurred += OnEvent;
        _input.Submitted += SendMessage;
        ApplyState(_state);
    }

    public void StopGeneration() => _conversation.Execute(new StopGeneration());
    public void StartNewSession() => _conversation.Execute(new StartNewSession(_models.SelectedModel.Id, _models.SelectedProviderId));
    public void ShowSessions()
    {
        if (_input.IsImporting) return;
        if (_conversation.Execute(new ListSessions())) _input.Editor.Input = string.Empty;
    }
    public event Action<string?>? RewindCompleted;
    public (string? SessionId, int TurnCount, ImmutableArray<RewindPoint> Points) GetRewindPoints() =>
        (_state.SessionId, _state.TurnCount, _conversation.GetRewindPoints());

    public bool RewindTo(RewindPoint point, string? sessionId, int turnCount) =>
        CanRewind && _conversation.Execute(new RewindToTurn(point.Index, point.Kind, sessionId, turnCount, point.ToolCall?.CallId));
    public Task FinishAsync() => _conversation.FinishAsync();

    public void ShowOrAskSideQuestion(string question)
    {
        if (_input.IsImporting || _input.HasAttachments || _input.GetFilePaths().Length > 0)
        {
            _input.ShowError("/btw принимает только текст без новых вложений. Черновик сохранён; дождитесь чтения или удалите вложения.");
            return;
        }
        if (string.IsNullOrWhiteSpace(question))
        {
            _btw.Apply(_conversation.State.SideQuestions);
            if (_input.GetParentWindow() is { } window && _btw.Show(window))
                _input.Editor.Input = string.Empty;
            else _input.ShowError("Пока нет попутных ответов. Используйте /btw вопрос.");
            return;
        }
        if (_conversation.Execute(new AskSideQuestion(question))) _input.ClearDraft();
    }

    private void ShowSubagents()
    {
        if (_input.GetParentWindow() is { } parent) _subagents.Show(parent);
    }

    private void CancelSideQuestion(Guid id) => _conversation.Execute(new CancelSideQuestion(id));
    private void ClearSideQuestions() => _conversation.Execute(new ClearSideQuestions());
    private void RestoreFocusAfterSide()
    {
        if (!_disposed && _question is null && _permissions.Count == 0 && _delegationPermissions.Count == 0) _focusInput();
    }

    private void SendMessage(SubmissionMode mode)
    {
        if (_input.IsImporting)
        {
            _input.ShowError("Дождитесь окончания чтения вложений или буфера обмена.");
            return;
        }
        var images = _input.GetAttachments().Select(image => new LakeCode.Core.Attachments.ImageData(
            image.Name, image.Data, image.MediaType, image.Width, image.Height)).ToImmutableArray();
        var text = _input.Editor.Input;
        var mentions = _input.Editor.Mentions;
        var model = _models.SelectedModel;
        var effort = _models.SelectedReasoningEffort;
        var providerId = _models.SelectedProviderId;
        var paths = _input.GetFilePaths();
        if (paths.Length == 0)
        {
            if (_conversation.Execute(new SubmitMessage(text, images, model.Id, effort, mode, providerId)))
                _input.ClearDraft();
            return;
        }
        var revision = _input.DraftRevision;
        var operation = _input.BeginFileCapture();
        _ = CaptureAndSendAsync();

        async Task CaptureAndSendAsync()
        {
            ImmutableArray<LakeCode.Core.Attachments.FileAttachment> files = [];
            string? error = null;
            try
            {
                files = await Task.Run(() => LakeCode.Core.Attachments.FileAttachments.CaptureAsync(
                    _workspaceRoot, paths, model.ContextWindowTokens, operation.Token), operation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { error = failure.Message; }
            _windowSystem.EnqueueOnUIThread(() =>
            {
                try
                {
                    if (_disposed || !_input.EndFileCapture(operation)) return;
                    if (operation.IsCancellationRequested) return;
                    if (error is not null) { _input.ShowError(error); return; }
                    if (_input.DraftRevision != revision)
                    {
                        _input.ShowError("Черновик изменился во время чтения файлов. Отправьте его повторно.");
                        return;
                    }
                    if (_conversation.Execute(new SubmitMessage(text, images, model.Id, effort, mode, providerId, files, mentions)))
                        _input.ClearDraft();
                }
                finally { operation.Dispose(); }
            });
        }
    }

    private void OnStateChanged(ConversationState state) => Enqueue(() => ApplyState(state));
    private void OnEvent(ConversationEventEnvelope envelope)
    {
        if (envelope.Event is not BashOutputChanged bash)
        {
            Enqueue(() => ApplyEvent(envelope));
            return;
        }
        lock (_bashOutputGate)
        {
            var queued = _pendingBashOutput.ContainsKey(bash.Update.CallId);
            _pendingBashOutput[bash.Update.CallId] = envelope;
            if (queued) return;
        }
        // One pending UI notification per command, even if rendering temporarily falls behind.
        Enqueue(() =>
        {
            ConversationEventEnvelope latest;
            lock (_bashOutputGate)
            {
                if (!_pendingBashOutput.Remove(bash.Update.CallId, out latest!)) return;
            }
            ApplyEvent(latest);
        });
    }
    private void Enqueue(Action action) => _windowSystem.EnqueueOnUIThread(() =>
    {
        if (!_disposed) action();
    });

    private void ApplyState(ConversationState state)
    {
        if (state.Revision < _state.Revision) return;
        var wasExclusive = _state.IsBusy && !_state.IsGenerating;
        _state = state;
        _btw.Apply(state.SideQuestions);
        _header.SetSessionTitle(state.Title);
        _header.SetDelegations(_subagents.Tasks);
        _input.SetSending(state.IsBusy && !state.IsGenerating);
        _statistics.Apply(state);
        _input.ShowPending(state.PendingMessages.Select(item =>
        {
            var text = string.Join(" ", item.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > 100) text = text[..100] + "…";
            if (text.Length == 0) text = $"Изображения: {item.ImageCount} · Файлы: {item.FileCount}";
            var label = item.Mode switch
            {
                SubmissionMode.Clarify => "После инструментов",
                SubmissionMode.Interrupt => "После остановки",
                _ => "В очереди"
            };
            return $"{label}: {text}";
        }).ToArray(), state.QueuePaused);
        if (wasExclusive && !state.IsBusy && !_btw.IsOpen) _focusInput();
    }

    private void ApplyEvent(ConversationEventEnvelope envelope)
    {
        try
        {
            switch (envelope.Event)
            {
                case ConversationReplaced replaced:
                    ReplaceConversation(replaced.Conversation);
                    break;
                case RewindFinished rewindFinished:
                    RewindCompleted?.Invoke(rewindFinished.Error);
                    break;
                case SessionsListing listing:
                    _sessionsWindow.ShowLoading(_windowSystem, listing.ActiveId,
                        id => { if (!_input.IsImporting) _conversation.Execute(new OpenSession(id)); });
                    break;
                case SessionsListed listed:
                    _sessionsWindow.Update(listed.Sessions, listed.Errors, listed.ActiveId);
                    break;
                case SessionsListFailed failed:
                    _sessionsWindow.ShowError(failed.Error);
                    break;
                case TurnStarted started:
                    if (started.IsBackgroundContinuation) _content.AddControl(CreateBackgroundNotice());
                    else _content.AddUserMessage(started.Text, started.Images.Select(ImageInput.CreatePreview).ToArray(), started.Files, started.Mentions);
                    _turnSegments.Clear();
                    _response = new AssistantMessageComponent(_windowSystem);
                    _turnSegments.Add(_response);
                    _responseRequestId = envelope.RequestId;
                    _content.AddControl(_response);
                    break;
                case ReasoningDelta reasoning when IsActive(envelope):
                    _response!.AppendReasoning(reasoning.Text);
                    break;
                case TextDelta text when IsActive(envelope):
                    _response!.AppendResponse(text.Text);
                    break;
                case ToolCallChanged tool when IsActive(envelope):
                    if (tool.Call.Delegation is not null) { ShowDelegation(tool.Call); break; }
                    if (!_responsesByCallId.TryGetValue(tool.Call.CallId, out var toolOwner))
                        _responsesByCallId[tool.Call.CallId] = toolOwner = _response!;
                    toolOwner.UpdateToolCall(tool.Call);
                    break;
                case BackgroundToolCallChanged background:
                    if (envelope.SessionId == _state.SessionId) ShowDelegation(background.Call);
                    break;
                case DelegationActivityChanged activity:
                    if (envelope.SessionId == _state.SessionId) _subagents.Append(activity.Activity);
                    break;
                case BashOutputChanged bash when IsActive(envelope):
                    if (_responsesByCallId.TryGetValue(bash.Update.CallId, out var bashOwner))
                        bashOwner.UpdateBashOutput(bash.Update);
                    break;
                case TurnFinished finished when IsActive(envelope):
                    foreach (var segment in _turnSegments) segment.FinishThinking();
                    if (finished.Status == "completed") _response!.Complete();
                    else _response!.ShowError(finished.Error ?? "Запрос прерван.");
                    _response = null;
                    _responseRequestId = null;
                    break;
                case SideQuestionsChanged side:
                    _btw.Apply(side.State);
                    if (side.State.Exchanges.IsEmpty) _shownSideRequestId = null;
                    else if (side.State.IsGenerating && _shownSideRequestId != side.State.Exchanges[^1].RequestId)
                    {
                        _shownSideRequestId = side.State.Exchanges[^1].RequestId;
                        if (_input.GetParentWindow() is { } parent) _btw.Show(parent);
                    }
                    break;
                case ConversationError error:
                    _input.ShowError(error.Message);
                    break;
                case QuestionRequested question:
                    ShowQuestion(question);
                    break;
                case ReadPermissionRequested permission when _delegationBlocks.TryGetValue(permission.Request.CallId, out var block):
                    var permissionWindow = _input.GetParentWindow() ?? throw new InvalidOperationException("No active window for permission request.");
                    _delegationPermissions.Add(permission.InteractionId, block);
                    _subagents.SetInteractionActive(true);
                    _windowSystem.SetActiveWindow(permissionWindow);
                    block.ShowReadPermission(permission.Request,
                        decision => _conversation.Execute(new ResolveReadPermission(permission.InteractionId, decision)), permissionWindow);
                    _content.ScrollChildIntoView(block);
                    break;
                case ReadPermissionRequested permission when _responsesByCallId.TryGetValue(permission.Request.CallId, out _):
                    var response = _responsesByCallId[permission.Request.CallId];
                    var window = response.GetParentWindow() ?? throw new InvalidOperationException("No active window for permission request.");
                    _permissions.Add(permission.InteractionId, (response, permission.Request.CallId));
                    _subagents.SetInteractionActive(true);
                    _windowSystem.SetActiveWindow(window);
                    response.ShowReadPermission(permission.Request,
                        decision => _conversation.Execute(new ResolveReadPermission(permission.InteractionId, decision)), window);
                    _content.ScrollChildIntoView(response);
                    break;
                case ReadPermissionRequested permission:
                    _conversation.Execute(new FailInteraction(permission.InteractionId, "No active response for permission request."));
                    break;
                case InteractionFinished interaction:
                    HideInteraction(interaction.InteractionId);
                    break;
            }
        }
        catch (Exception error)
        {
            if (envelope.Event is QuestionRequested question)
                _conversation.Execute(new FailInteraction(question.InteractionId, error.Message));
            else if (envelope.Event is ReadPermissionRequested permission)
                _conversation.Execute(new FailInteraction(permission.InteractionId, error.Message));
            _input.ShowError($"Не удалось обновить экран: {error.Message}");
        }
    }

    private DelegationMessageComponent CreateDelegationBlock(LakeCode.Core.Agent.Delegation.DelegationSnapshot task,
        string kind, string text) => new(task, kind, text, () =>
        {
            if (_input.GetParentWindow() is { } parent) _subagents.Show(parent, task.TaskId);
        });

    private void ShowDelegation(LakeCode.Core.Agent.ToolCallUpdate call)
    {
        if (call.Delegation is not { } task) return;
        var exchange = _subagents.Apply(task);
        _header.SetDelegations(_subagents.Tasks);
        if (exchange is not { } item) return;
        _response?.SuspendForDelegation();
        var block = CreateDelegationBlock(task, item.Kind, item.Text);
        _delegationBlocks[call.CallId] = block;
        _content.AddControl(block);
        if (_response is not null)
        {
            _response = new AssistantMessageComponent(_windowSystem) { Visible = false };
            _response.SuspendForDelegation();
            _turnSegments.Add(_response);
            _content.AddControl(_response);
        }
    }

    private bool IsActive(ConversationEventEnvelope envelope) =>
        _response is not null && _responseRequestId == envelope.RequestId;

    private void ReplaceConversation(ConversationView view)
    {
        // Construct first, so failed preview/rendering doesn't clear the previous transcript.
        var controls = new List<IWindowControl>();
        var exchanges = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<LakeCode.Core.Agent.Delegation.DelegationSnapshot>();
        foreach (var turn in view.Turns)
        {
            if (turn.IsBackgroundContinuation) controls.Add(CreateBackgroundNotice());
            else controls.Add(new UserMessageComponent(turn.Text, turn.Images.Select(ImageInput.CreatePreview).ToArray(), turn.Files, turn.Mentions));
            var response = new AssistantMessageComponent(_windowSystem);
            controls.Add(response);
            var segments = new List<AssistantMessageComponent> { response };
            var owners = new Dictionary<string, AssistantMessageComponent>(StringComparer.Ordinal);
            foreach (var item in turn.Items)
            {
                if (item.Kind == "thinking") response.AppendReasoning(item.Text!);
                else if (item.Kind == "text") response.AppendResponse(item.Text!);
                else if (item.ToolCall?.Delegation is { } task)
                {
                    tasks.Add(task);
                    if (SubagentsWindowComponent.ProjectExchange(task, exchanges) is not { } exchange) continue;
                    response.SuspendForDelegation();
                    controls.Add(CreateDelegationBlock(task, exchange.Kind, exchange.Text));
                    response = new AssistantMessageComponent(_windowSystem) { Visible = false };
                    response.SuspendForDelegation();
                    segments.Add(response);
                    controls.Add(response);
                }
                else if (item.ToolCall is { } call)
                {
                    if (!owners.TryGetValue(call.CallId, out var owner)) owners.Add(call.CallId, owner = response);
                    owner.UpdateToolCall(call);
                }
            }
            foreach (var segment in segments) segment.FinishThinking();
            if (turn.Status == "completed") response.Complete();
            else response.ShowError(turn.Error ?? "Запрос не завершён.");
        }
        var draftImages = view.DraftImages.Select(ImageInput.CreatePreview).ToArray();
        _subagents.Reset();
        foreach (var task in tasks.GroupBy(task => task.TaskId).Select(group => group.Last()))
            _subagents.Apply(task.Status is LakeCode.Core.Agent.Delegation.DelegationStatus.Running or
                LakeCode.Core.Agent.Delegation.DelegationStatus.WaitingForParent
                ? task with { Status = LakeCode.Core.Agent.Delegation.DelegationStatus.Cancelled,
                    Error = "Сабагент не восстановлен после открытия сессии или отката истории." }
                : task);
        _header.SetDelegations(_subagents.Tasks);
        _delegationBlocks.Clear();
        _delegationPermissions.Clear();
        _turnSegments.Clear();
        _responsesByCallId.Clear();
        ((IControlHost)_content).ClearControls();
        foreach (var control in controls) _content.AddControl(control);
        _response = null;
        _responseRequestId = null;
        if (!string.IsNullOrEmpty(view.ModelId))
        {
            try
            {
                _models.SelectModel(view.ProviderId!, view.ModelId);
                _models.SelectReasoningEffort(view.ReasoningEffort!);
            }
            catch (Exception error) { _input.ShowError($"История открыта, но подключение недоступно: {error.Message}"); }
        }
        _input.ClearDraft();
        _input.SetDraft(view.DraftText, draftImages);
        _input.Editor.Restore(view.DraftText, view.DraftMentions);
        foreach (var file in view.DraftFiles.Where(file => !view.DraftMentions.Any(mention => mention.Path == file.Path)))
            _input.AddFile(file.Path, _input.Editor.Input.Length, 0);
        _content.ScrollToBottom();
        _focusInput();
    }

    private static MarkupControl CreateBackgroundNotice() => new(new List<string>
    { "[grey70]Уведомление фоновых исследований[/]" })
    { HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment.Stretch, Wrap = true };

    private void ShowQuestion(QuestionRequested request)
    {
        var window = _input.GetParentWindow() ?? throw new InvalidOperationException("No active window for question.");
        _questionId = request.InteractionId;
        _subagents.SetInteractionActive(true);
        _windowSystem.SetActiveWindow(window);
        _question = new AskComponent(request.Questions.ToArray(), answers =>
        {
            HideInteraction(request.InteractionId);
            _conversation.Execute(new AnswerQuestion(request.InteractionId, answers));
        });
        _input.Visible = false;
        _askHost.AddControl(_question);
        _askHost.Visible = true;
        _question.Focus(window);
    }

    private void HideInteraction(Guid id)
    {
        if (_delegationPermissions.Remove(id, out var delegation)) delegation.CancelReadPermission();
        if (_permissions.Remove(id, out var permission)) permission.Response.CancelReadPermission(permission.CallId);
        if (_questionId != id)
        {
            _subagents.SetInteractionActive(_questionId is not null || _permissions.Count > 0 || _delegationPermissions.Count > 0);
            return;
        }
        if (_question is { } question)
        {
            question.Cancel();
            ((IControlHost)_askHost).RemoveControl(question);
            question.Dispose();
        }
        _question = null;
        _questionId = null;
        _subagents.SetInteractionActive(_permissions.Count > 0 || _delegationPermissions.Count > 0);
        _askHost.Visible = false;
        _input.Visible = true;
        _focusInput();
    }

    public void Dispose()
    {
        _disposed = true;
        _input.Submitted -= SendMessage;
        _conversation.StateChanged -= OnStateChanged;
        _conversation.EventOccurred -= OnEvent;
        _btw.CancelRequested -= CancelSideQuestion;
        _btw.ClearRequested -= ClearSideQuestions;
        _btw.Closed -= RestoreFocusAfterSide;
        _header.SubagentsRequested -= ShowSubagents;
        _subagents.ReturnToChat -= RestoreFocusAfterSide;
        if (_questionId is { } id) HideInteraction(id);
        foreach (var permissionId in _permissions.Keys.Concat(_delegationPermissions.Keys).ToArray()) HideInteraction(permissionId);
        _delegationBlocks.Clear();
        _turnSegments.Clear();
        _responsesByCallId.Clear();
    }
}

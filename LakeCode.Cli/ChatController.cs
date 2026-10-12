using System.Diagnostics;
using LakeCode.Cli.Agent;
using LakeCode.Cli.Components;
using LakeCode.Cli.State;
using LakeCode.Cli.Sessions;
using Microsoft.Extensions.AI;
using SharpConsoleUI;
using SharpConsoleUI.Extensions;

namespace LakeCode.Cli;

internal sealed class ChatController
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly MainContentComponent _mainContent;
    private readonly HeaderComponent _header;
    private readonly InputComponent _input;
    private readonly CancellationToken _cancellationToken;
    private readonly Action _focusInput;
    private readonly StatisticsStore _statistics;
    private readonly ModelStore _models;
    private readonly string _workspaceRoot;
    private AgentLoop? _agent;
    private AssistantMessageComponent? _activeResponse;
    private bool _sending;
    private bool _saveFailed;
    private readonly SessionStorage _sessions;
    private readonly SessionsWindowComponent _sessionsWindow;
    private SessionSnapshot? _snapshot;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _generation;
    private bool _awaitingReadPermission;

    public bool CanStopGeneration => _generation is { IsCancellationRequested: false } && !_awaitingReadPermission;

    public void StopGeneration() => _generation?.Cancel();

    public ChatController(
        ConsoleWindowSystem windowSystem,
        MainContentComponent mainContent,
        HeaderComponent header,
        InputComponent input,
        StatisticsStore statistics,
        ModelStore models,
        Action focusInput,
        string workspaceRoot,
        SessionStorage sessions,
        SessionsWindowComponent sessionsWindow,
        CancellationToken cancellationToken)
    {
        _windowSystem = windowSystem;
        _mainContent = mainContent;
        _header = header;
        _input = input;
        _cancellationToken = cancellationToken;
        _focusInput = focusInput;
        _statistics = statistics;
        _models = models;
        _workspaceRoot = workspaceRoot;
        _sessions = sessions;
        _sessionsWindow = sessionsWindow;
        _input.Editor.Entered += (_, _) => SendMessage();
    }

    public void StartNewSession()
    {
        if (_sending)
            return;
        if (_saveFailed)
        {
            _input.ShowError("Текущая сессия не сохранена. Откройте /sessions для повторной попытки сохранения перед переключением.");
            return;
        }

        _agent = null;
        _snapshot = null;
        _header.SetSessionTitle(null);
        ((SharpConsoleUI.Controls.IControlHost)_mainContent).ClearControls();
        _statistics.Reset(_models.SelectedModel);
        _input.ClearDraft();
        _focusInput();
    }

    public Task FinishAsync() => _operation;

    public void ShowSessions()
    {
        if (_sending || _input.IsImporting) return;
        _input.Editor.Input = string.Empty;
        SetBusy(true);
        _operation = Task.Run(async () =>
        {
            try
            {
                if (_saveFailed && _snapshot is { } unsaved)
                {
                    await _sessions.SaveAsync(unsaved, _cancellationToken);
                    _saveFailed = false;
                }
                var result = await _sessions.ListAsync(_workspaceRoot, _cancellationToken);
                _windowSystem.EnqueueOnUIThread(() =>
                {
                    SetBusy(false);
                    _sessionsWindow.Show(_windowSystem, result.Sessions, result.Errors, _snapshot?.Id, OpenSession);
                });
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                _windowSystem.EnqueueOnUIThread(() => _input.ShowError($"Не удалось загрузить список сессий: {error.Message}"));
                _windowSystem.EnqueueOnUIThread(() => SetBusy(false));
            }
        });
    }

    private void OpenSession(string id)
    {
        if (_sending || _input.IsImporting) return;
        SetBusy(true);
        _operation = Task.Run(async () =>
        {
            try
            {
                var snapshot = await _sessions.LoadAsync(id, _cancellationToken);
                if (snapshot.WorkspaceRoot != _workspaceRoot)
                    throw new InvalidDataException("Сессия относится к другому рабочему каталогу.");
                if (!_models.Models.TryGetValue(snapshot.ModelId, out var model) ||
                    !model.ReasoningEfforts.Contains(snapshot.ReasoningEffort))
                    throw new InvalidDataException("Сохранённая модель или уровень рассуждения больше не доступны.");
                var agent = new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, snapshot.Id);
                if (snapshot.AgentState is { } state)
                    await agent.RestoreSessionAsync(state, _cancellationToken);
                var images = snapshot.Turns.Select(turn =>
                {
                    if (turn.Images.Count > ImageInput.MaxAttachments ||
                        turn.Images.Sum(image => (long)image.Data.Length) > ImageInput.MaxTotalBytes)
                        throw new InvalidDataException("Вложения сохранённого сообщения превышают лимиты.");
                    return turn.Images.Select(image => image.Decode()).ToArray();
                }).ToArray();
                _windowSystem.EnqueueOnUIThread(() =>
                {
                    try
                    {
                        // Prepare controls before replacing the current conversation.
                        var controls = new List<SharpConsoleUI.Controls.IWindowControl>();
                        for (var index = 0; index < snapshot.Turns.Count; index++)
                        {
                            var turn = snapshot.Turns[index];
                            controls.Add(new UserMessageComponent(turn.Text, images[index]));
                            var response = new AssistantMessageComponent(_windowSystem);
                            foreach (var item in turn.Events)
                            {
                                if (item.Kind == "thinking") response.AppendReasoning(item.Text!);
                                else if (item.Kind == "text") response.AppendResponse(item.Text!);
                                else if (item.Kind == "tool" && item.ToolCall is { } call) response.UpdateToolCall(call);
                            }
                            if (turn.Status == "completed") response.Complete();
                            else
                            {
                                if (turn.Status == "in_progress")
                                {
                                    turn.Status = "interrupted";
                                    turn.Error = "Запрос прерван. Контекст восстановлен с последнего завершённого ответа. Выполненные действия не отменены и не будут повторены автоматически.";
                                }
                                response.ShowError(turn.Error ?? "Запрос не завершён.");
                            }
                            controls.Add(response);
                        }
                        ((SharpConsoleUI.Controls.IControlHost)_mainContent).ClearControls();
                        foreach (var control in controls) _mainContent.AddControl(control);
                        _agent = agent;
                        _snapshot = snapshot;
                        _header.SetSessionTitle(snapshot.Title);
                        _models.SelectModel(snapshot.ModelId);
                        _models.SelectReasoningEffort(snapshot.ReasoningEffort);
                        _statistics.Reset(model);
                        var last = snapshot.Turns.LastOrDefault(turn => turn.Usage is not null);
                        if (last?.Usage is { } usage && _models.Models.TryGetValue(last.ModelId, out var usageModel))
                            _statistics.Restore(new RequestStatistics(usageModel, usage.Input, usage.Output,
                                usage.Total, usage.Cached, usage.Reasoning),
                                snapshot.Turns.LastOrDefault(turn => turn.Status == "completed")?.TokensPerSecond);
                        _input.ClearDraft();
                        _mainContent.ScrollToBottom();
                    }
                    catch (Exception error) { _input.ShowError($"Не удалось открыть сессию: {error.Message}"); }
                    finally { SetBusy(false); }
                });
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                _windowSystem.EnqueueOnUIThread(() => _input.ShowError($"Не удалось открыть сессию: {error.Message}"));
                _windowSystem.EnqueueOnUIThread(() => SetBusy(false));
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _sending = busy;
        _input.SetSending(busy);
        if (!busy) _focusInput();
    }

    private void SendMessage()
    {
        if (_sending) return;
        if (_input.IsImporting)
        {
            _input.ShowError("Дождитесь окончания чтения изображения или буфера обмена.");
            return;
        }
        var model = _models.SelectedModel;
        if (_input.HasAttachments && !model.InputModalities.Contains(ModelModality.Image))
        {
            _input.ShowError($"Модель {model.Name} не поддерживает изображения. Выберите другую модель или удалите вложения; черновик сохранён.");
            return;
        }
        var message = _input.Editor.Input;
        if (string.IsNullOrWhiteSpace(message) && !_input.HasAttachments)
        {
            return;
        }
        var attachments = _input.GetAttachments();
        var contents = new List<AIContent>(attachments.Length + 1);
        if (!string.IsNullOrWhiteSpace(message)) contents.Add(new TextContent(message));
        foreach (var attachment in attachments)
            contents.Add(new DataContent(attachment.Data, attachment.MediaType));
        var request = new ChatMessage(ChatRole.User, contents);

        _mainContent.AddUserMessage(message, attachments);
        _input.ClearDraft();
        _sending = true;
        _statistics.SetGenerating(true);
        _input.SetSending(true);
        var response = new AssistantMessageComponent(_windowSystem);
        _activeResponse = response;
        _mainContent.AddControl(response);
        var reasoningEffort = _models.SelectedReasoningEffort;
        _snapshot ??= new SessionSnapshot { WorkspaceRoot = _workspaceRoot };
        _snapshot.ModelId = model.Id;
        _snapshot.ReasoningEffort = reasoningEffort;
        if (_snapshot.Turns.Count == 0)
        {
            var title = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            _snapshot.Title = string.IsNullOrEmpty(title) ? "Сообщение с изображениями" : title[..Math.Min(title.Length, 80)];
            _header.SetSessionTitle(_snapshot.Title);
        }
        var turn = new SessionTurn
        {
            Text = message, Images = attachments.Select(image => new SavedImage(image.Name, image.Data.ToArray())).ToList(),
            ModelId = model.Id, ReasoningEffort = reasoningEffort
        };
        _snapshot.Turns.Add(turn);
        var generation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        _generation = generation;
        _operation = Task.Run(() => SendAsync(request, response, model, reasoningEffort, _snapshot, turn, generation.Token));
    }

    private async Task<ExternalReadDecision> ConfirmExternalReadAsync(
        ExternalReadRequest request, CancellationToken cancellationToken)
    {
        var response = _activeResponse ?? throw new InvalidOperationException("No active response for permission request.");
        var completion = new TaskCompletionSource<ExternalReadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _windowSystem.EnqueueOnUIThread(() =>
        {
            if (completion.Task.IsCompleted) return;
            try
            {
                var window = response.GetParentWindow()
                    ?? throw new InvalidOperationException("No active window for permission request.");
                _awaitingReadPermission = true;
                response.ShowReadPermission(request, decision =>
                {
                    _awaitingReadPermission = false;
                    completion.TrySetResult(decision);
                }, window);
                _mainContent.ScrollToBottom();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        try
        {
            return await completion.Task;
        }
        finally
        {
            _windowSystem.EnqueueOnUIThread(() =>
            {
                _awaitingReadPermission = false;
                response.CancelReadPermission(request.CallId);
            });
        }
    }

    private async Task<string?> GenerateSessionTitleAsync(ChatMessage message, ModelInfo model, CancellationToken generationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var title = await _agent!.GenerateTitleAsync(message, model, timeout.Token);
            _windowSystem.EnqueueOnUIThread(() => _header.SetSessionTitle(title));
            return title;
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            var reason = error is OperationCanceledException ? "превышено время ожидания" : error.Message;
            _windowSystem.EnqueueOnUIThread(() =>
                _input.ShowError($"Не удалось сгенерировать заголовок сессии: {reason}. Сохранено название из первого сообщения."));
            return null;
        }
    }

    private async Task SendAsync(
        ChatMessage message, AssistantMessageComponent response, ModelInfo model, string reasoningEffort,
        SessionSnapshot snapshot, SessionTurn turn, CancellationToken generationToken)
    {
        var checkpoint = snapshot.AgentState;
        Task<string?> titleTask = Task.FromResult<string?>(null);
        bool completed = false;
        void OnToolCall(ToolCallUpdate update)
        {
            turn.Events.Add(new ResponseEvent { Kind = "tool", ToolCall = update });
            _windowSystem.EnqueueOnUIThread(() => response.UpdateToolCall(update));
        }
        try
        {
            snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            // Persist the user's message before making any external request or executing tools.
            await _sessions.SaveAsync(snapshot, generationToken);
            if (_agent is null)
            {
                var agent = new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync, snapshot.Id);
                if (checkpoint is { } savedState)
                    await agent.RestoreSessionAsync(savedState, generationToken);
                _agent = agent;
            }
            if (snapshot.Turns.Count == 1)
                titleTask = GenerateSessionTitleAsync(message, model, generationToken);
            checkpoint ??= await _agent.SerializeSessionAsync(generationToken);
            snapshot.AgentState = checkpoint;
            _agent.ToolCallUpdated += OnToolCall;
            long? firstTokenTimestamp = null;
            long? outputTokens = null;
            await foreach (var update in _agent.RunAsync(message, model, reasoningEffort, generationToken))
            {
                foreach (var content in update.Contents)
                {
                    if (firstTokenTimestamp is null &&
                        (content is TextReasoningContent { Text.Length: > 0 } ||
                         content is TextContent { Text.Length: > 0 }))
                        firstTokenTimestamp = Stopwatch.GetTimestamp();
                    if (content is UsageContent usage)
                    {
                        var details = usage.Details;
                        outputTokens = details.OutputTokenCount ?? outputTokens;
                        turn.Usage = new SavedUsage(details.InputTokenCount, details.CachedInputTokenCount,
                            details.OutputTokenCount, details.ReasoningTokenCount, details.TotalTokenCount);
                        _windowSystem.EnqueueOnUIThread(() => _statistics.Update(details, model));
                    }
                    else if (reasoningEffort != "none" && content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        var text = reasoning.Text;
                        turn.AppendText("thinking", text);
                        _windowSystem.EnqueueOnUIThread(() => response.AppendReasoning(text));
                    }
                    else if (content is TextContent answer && !string.IsNullOrEmpty(answer.Text))
                    {
                        var text = answer.Text;
                        turn.AppendText("text", text);
                        _windowSystem.EnqueueOnUIThread(() => response.AppendResponse(text));
                    }
                }
            }

            var elapsed = firstTokenTimestamp is { } start
                ? Stopwatch.GetElapsedTime(start)
                : TimeSpan.Zero;
            snapshot.AgentState = await _agent.SerializeSessionAsync(generationToken);
            completed = true;
            turn.Status = "completed";
            turn.TokensPerSecond = outputTokens is { } tokens && elapsed > TimeSpan.Zero
                ? tokens / elapsed.TotalSeconds : null;
            _windowSystem.EnqueueOnUIThread(() =>
            {
                _statistics.CompleteRequest(outputTokens, elapsed);
                response.Complete();
            });
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            turn.Status = "interrupted";
            turn.Error = (_cancellationToken.IsCancellationRequested
                ? "Запрос отменён при закрытии приложения."
                : "Генерация остановлена пользователем.") +
                " Контекст восстановлен с последнего завершённого ответа; выполненные действия не отменены.";
            _windowSystem.EnqueueOnUIThread(() => response.ShowError(turn.Error));
        }
        catch (Exception error)
        {
            turn.Status = "failed";
            turn.Error = error.Message + "\nКонтекст возвращён к последнему завершённому ответу. Уже выполненные действия не отменены.";
            _windowSystem.EnqueueOnUIThread(() => response.ShowError(turn.Error));
        }
        finally
        {
            if (_agent is not null)
                _agent.ToolCallUpdated -= OnToolCall;
            if (!completed)
            {
                snapshot.AgentState = checkpoint;
                try
                {
                    if (_agent is not null && checkpoint is { } state)
                        await _agent.RestoreSessionAsync(state);
                    else _agent = null;
                }
                catch (Exception error)
                {
                    _agent = null;
                    _windowSystem.EnqueueOnUIThread(() => _input.ShowError($"Не удалось восстановить контекст после ошибки: {error.Message}. Откройте сессию через /sessions."));
                }
            }
            // Await the isolated request before saving; it never races snapshot serialization.
            if (await titleTask is { } title)
                snapshot.Title = title;
            snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                // Shutdown cancellation must not discard the final snapshot; Program waits for this task.
                await _sessions.SaveAsync(snapshot);
                _saveFailed = false;
            }
            catch (Exception error)
            {
                _saveFailed = true;
                _windowSystem.EnqueueOnUIThread(() => _input.ShowError($"Сессия не сохранена: {error.Message}. /sessions повторит сохранение."));
                if (_cancellationToken.IsCancellationRequested)
                    Console.Error.WriteLine($"Сессия не сохранена: {error.Message}");
            }
            _windowSystem.EnqueueOnUIThread(() =>
            {
                _generation?.Dispose();
                _generation = null;
                _statistics.SetGenerating(false);
                _activeResponse = null;
                response.FinishThinking();
            });
            if (!_cancellationToken.IsCancellationRequested)
            {
                _windowSystem.EnqueueOnUIThread(() =>
                {
                    _sending = false;
                    _input.SetSending(false);
                    _focusInput();
                });
            }
        }
    }
}

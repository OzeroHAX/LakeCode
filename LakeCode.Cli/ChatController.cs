using System.Diagnostics;
using LakeCode.Cli.Agent;
using LakeCode.Cli.Components;
using LakeCode.Cli.State;
using Microsoft.Extensions.AI;
using SharpConsoleUI;
using SharpConsoleUI.Extensions;

namespace LakeCode.Cli;

internal sealed class ChatController
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly MainContentComponent _mainContent;
    private readonly InputComponent _input;
    private readonly CancellationToken _cancellationToken;
    private readonly Action _focusInput;
    private readonly StatisticsStore _statistics;
    private readonly ModelStore _models;
    private readonly string _workspaceRoot;
    private AgentLoop? _agent;
    private AssistantMessageComponent? _activeResponse;
    private bool _sending;

    public ChatController(
        ConsoleWindowSystem windowSystem,
        MainContentComponent mainContent,
        InputComponent input,
        StatisticsStore statistics,
        ModelStore models,
        Action focusInput,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _windowSystem = windowSystem;
        _mainContent = mainContent;
        _input = input;
        _cancellationToken = cancellationToken;
        _focusInput = focusInput;
        _statistics = statistics;
        _models = models;
        _workspaceRoot = workspaceRoot;
        _input.Editor.Entered += (_, _) => SendMessage();
    }

    public void StartNewSession()
    {
        if (_sending)
            return;

        _agent = null;
        ((SharpConsoleUI.Controls.IControlHost)_mainContent).ClearControls();
        _statistics.Reset(_models.SelectedModel);
        _input.Editor.Input = string.Empty;
        _focusInput();
    }

    private void SendMessage()
    {
        var message = _input.Editor.Input;
        if (_sending || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _mainContent.AddUserMessage(message);
        _input.Editor.Input = string.Empty;
        _sending = true;
        _statistics.SetGenerating(true);
        _input.Editor.IsEnabled = false;
        var response = new AssistantMessageComponent(_windowSystem);
        _activeResponse = response;
        _mainContent.AddControl(response);
        var model = _models.SelectedModel;
        var reasoningEffort = _models.SelectedReasoningEffort;
        _ = Task.Run(() => SendAsync(message, response, model, reasoningEffort));
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
                response.ShowReadPermission(request, decision => completion.TrySetResult(decision), window);
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
            _windowSystem.EnqueueOnUIThread(() => response.CancelReadPermission(request.CallId));
        }
    }

    private async Task SendAsync(
        string message, AssistantMessageComponent response, ModelInfo model, string reasoningEffort)
    {
        void OnToolCall(ToolCallUpdate update) =>
            _windowSystem.EnqueueOnUIThread(() => response.UpdateToolCall(update));
        try
        {
            _agent ??= new AgentLoop(_workspaceRoot, ConfirmExternalReadAsync);
            _agent.ToolCallUpdated += OnToolCall;
            long? firstTokenTimestamp = null;
            long? outputTokens = null;
            await foreach (var update in _agent.RunAsync(message, model, reasoningEffort, _cancellationToken))
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
                        _windowSystem.EnqueueOnUIThread(() => _statistics.Update(details, model));
                    }
                    else if (reasoningEffort != "none" && content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        var text = reasoning.Text;
                        _windowSystem.EnqueueOnUIThread(() => response.AppendReasoning(text));
                    }
                    else if (content is TextContent answer && !string.IsNullOrEmpty(answer.Text))
                    {
                        var text = answer.Text;
                        _windowSystem.EnqueueOnUIThread(() => response.AppendResponse(text));
                    }
                }
            }

            var elapsed = firstTokenTimestamp is { } start
                ? Stopwatch.GetElapsedTime(start)
                : TimeSpan.Zero;
            _windowSystem.EnqueueOnUIThread(() =>
            {
                _statistics.CompleteRequest(outputTokens, elapsed);
                response.Complete();
            });
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            // Closing the application cancels the in-flight request.
        }
        catch (Exception error)
        {
            _windowSystem.EnqueueOnUIThread(() => response.ShowError(error.Message));
        }
        finally
        {
            if (_agent is not null)
                _agent.ToolCallUpdated -= OnToolCall;
            _windowSystem.EnqueueOnUIThread(() =>
            {
                _statistics.SetGenerating(false);
                _activeResponse = null;
                response.FinishThinking();
            });
            if (!_cancellationToken.IsCancellationRequested)
            {
                _windowSystem.EnqueueOnUIThread(() =>
                {
                    _sending = false;
                    _input.Editor.IsEnabled = true;
                    _focusInput();
                });
            }
        }
    }
}

using LakeCode.Core.Settings;

namespace LakeCode.Core.Conversations;

public sealed partial class ConversationService
{
    private CancellationTokenSource? _compactionCancellation;

    private bool CanCompact() => !_busy && !_finishing && !_shutdown.IsCancellationRequested &&
        _snapshot is { Turns.Count: > 0 } && _pending.Count == 0 && _pendingPersistence.IsCompleted &&
        !_saveFailed && !_contextFailed && !_pendingSaveFailed && _sideCancellation is null &&
        _agent?.HasDelegationNotifications != true &&
        _agent?.Delegations.Any(task => task.Status is Agent.Delegation.DelegationStatus.Running or Agent.Delegation.DelegationStatus.WaitingForParent) != true;

    private bool BeginCompaction()
    {
        if (!CanCompact()) return Reject("/compact доступен в сохранённой свободной сессии: дождитесь агента, /btw, исследований и очереди.");
        ModelConfiguration configuration;
        try
        {
            var selection = _settings?.Current.CompactionModel ?? throw new InvalidOperationException(
                "Выберите отдельную модель в /models → Сжатие контекста. История будет отправлена этому подключению.");
            configuration = selection.Resolve(_providers, "сжатия контекста");
        }
        catch (Exception error) { return Reject(error.Message); }
        var snapshot = CopySnapshot(_snapshot!);
        var userTurnIndex = snapshot.Turns.FindLastIndex(turn => !turn.IsBackgroundContinuation);
        if (userTurnIndex < 0 || snapshot.Turns[userTurnIndex].HistoryStart is not { } userStart)
            return Reject("Нет сохранённой границы последнего пользовательского хода.");
        var protectedPlan = CurrentPlan()?.Markdown;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _compactionCancellation = cancellation;
        _busy = true;
        PublishState();
        Emit(new CompactionStarted(configuration.Provider.Name + " · " + configuration.Model.Name));
        _operation = Task.Run(async () =>
        {
            try
            {
                if (_agent is null)
                {
                    var agent = CreateAgent(snapshot.Id);
                    try
                    {
                        if (snapshot.AgentState is { } saved) await agent.RestoreSessionAsync(saved, cancellation.Token).ConfigureAwait(false);
                        _agent = agent;
                    }
                    catch { await agent.DisposeAsync().ConfigureAwait(false); throw; }
                }
                var userEnd = userTurnIndex + 1 < snapshot.Turns.Count
                    ? snapshot.Turns[userTurnIndex + 1].HistoryStart ?? throw new InvalidDataException("Нет границы следующего хода.")
                    : _agent.HistoryCount;
                if (userStart < 0 || userStart >= userEnd || userEnd > _agent.HistoryCount)
                    throw new InvalidDataException("Некорректные границы пользовательского хода.");
                var statuses = string.Join("\n", snapshot.Turns.Select((turn, index) =>
                    $"Turn {index}: historyStart={turn.HistoryStart}, background={turn.IsBackgroundContinuation}, mode={turn.AgentMode}, status={turn.Status}, error={turn.Error}"));
                var candidate = await _agent.PrepareCompactionAsync(userStart, userEnd,
                    snapshot.Turns[^1].Status == "completed", protectedPlan, statuses, configuration, cancellation.Token).ConfigureAwait(false);
                var next = CopySnapshot(snapshot);
                next.SchemaVersion = 5;
                next.AgentState = await _agent.SerializeWithCompactionAsync(candidate, cancellation.Token).ConfigureAwait(false);
                next.UpdatedAt = DateTimeOffset.UtcNow;
                cancellation.Token.ThrowIfCancellationRequested();
                // No await after the authoritative commit before activating the same validated projection.
                var warning = await _sessions.SaveCompactedAsync(next, cancellation.Token).ConfigureAwait(false);
                _agent.CommitCompaction(candidate);
                lock (_gate)
                {
                    _snapshot = next;
                    ClearSide();
                    Emit(new CompactionFinished(_agent.Compaction, null));
                    if (warning is not null) Emit(new ConversationError(warning));
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate) Emit(new CompactionFinished(null, "Сжатие отменено или превышено время ожидания. Предыдущий контекст сохранён; уже потраченные API-токены не возвращаются."));
            }
            catch (Exception error)
            {
                lock (_gate) Emit(new CompactionFinished(null, "Сжатие не применено: " + error.Message));
            }
            finally
            {
                lock (_gate)
                {
                    _compactionCancellation = null;
                    cancellation.Dispose();
                    _busy = false;
                    PublishState();
                }
            }
        });
        return true;
    }
}

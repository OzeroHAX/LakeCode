using LakeCode.Core.Agent.Compaction;
using LakeCode.Core.Agent.Session;
using LakeCode.Core.Agent.Session.History;
using LakeCode.Core.Conversations.Projection;
using LakeCode.Core.Sessions;
using LakeCode.Core.Settings;

namespace LakeCode.Core.Conversations.Compaction;

/// <summary>Builds, persists and activates a checkpoint; scheduling and cancellation belong to the service.</summary>
internal sealed class ConversationCompaction(SessionStorage storage)
{
    internal sealed record Request(SessionSnapshot Snapshot, ModelConfiguration Model, string? Plan,
        int UserTurnIndex, int UserStart);
    internal sealed record Result(SessionSnapshot Snapshot, CompactionView? View, string? Warning);

    public static Request Prepare(SessionSnapshot snapshot, ModelConfiguration model, string? plan)
    {
        var index = snapshot.Turns.FindLastIndex(turn => !turn.IsBackgroundContinuation);
        if (index < 0 || snapshot.Turns[index].HistoryStart is not { } start)
            throw new InvalidDataException("Нет сохранённой границы последнего пользовательского хода.");
        return new Request(snapshot, model, plan, index, start);
    }

    public async Task<Result> ExecuteAsync(Request request, AgentHistory history, AgentSessionState session,
        CancellationToken cancellationToken)
    {
        var snapshot = request.Snapshot;
        var userEnd = request.UserTurnIndex + 1 < snapshot.Turns.Count
            ? snapshot.Turns[request.UserTurnIndex + 1].HistoryStart ?? throw new InvalidDataException("Нет границы следующего хода.")
            : history.Count;
        if (request.UserStart < 0 || request.UserStart >= userEnd || userEnd > history.Count)
            throw new InvalidDataException("Некорректные границы пользовательского хода.");
        var statuses = string.Join("\n", snapshot.Turns.Select((turn, index) =>
            $"Turn {index}: historyStart={turn.HistoryStart}, background={turn.IsBackgroundContinuation}, mode={turn.AgentMode}, status={turn.Status}, error={turn.Error}"));
        var candidate = await history.PrepareCompactionAsync(request.UserStart, userEnd,
            snapshot.Turns[^1].Status == "completed", request.Plan, statuses, request.Model, cancellationToken).ConfigureAwait(false);
        var next = ConversationSnapshotMapper.Copy(snapshot);
        next.SchemaVersion = 5;
        next.AgentState = await session.SerializeWithCompactionAsync(candidate, cancellationToken).ConfigureAwait(false);
        next.UpdatedAt = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        // Never introduce an await between successful durable saving and activating this projection.
        var warning = await storage.SaveCompactedAsync(next, cancellationToken).ConfigureAwait(false);
        history.CommitCompaction(candidate);
        return new Result(next, history.Projection.View, warning);
    }
}

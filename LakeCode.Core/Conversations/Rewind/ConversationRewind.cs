using System.Collections.Immutable;
using System.Text.Json;
using LakeCode.Core.Agent.Session;
using LakeCode.Core.Conversations.Projection;
using LakeCode.Core.Sessions;

namespace LakeCode.Core.Conversations.Rewind;

/// <summary>Rewind planning and transaction. Restore/rewind capabilities preserve service-owned resource resets.</summary>
internal sealed class ConversationRewind(SessionStorage storage)
{
    internal sealed record Plan(int Index, int BoundaryIndex, int Boundary, int ToolEventEnd, bool KeepAnswer, bool KeepTool);
    internal sealed record Preparation(Plan? Plan, string? Error);
    internal sealed class Failure(Exception cause, bool contextFailed) : Exception(cause.Message, cause)
    {
        public bool ContextFailed { get; } = contextFailed;
    }

    public static ImmutableArray<RewindPoint> GetPoints(SessionSnapshot snapshot, IReadOnlyDictionary<string, int> toolBoundaries)
    {
        var points = ImmutableArray.CreateBuilder<RewindPoint>();
        for (var index = 0; index < snapshot.Turns.Count; index++)
        {
            var turn = snapshot.Turns[index];
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
                    (index + 1 == snapshot.Turns.Count ||
                     snapshot.Turns[index + 1].HistoryStart is { } end && toolBoundary <= end);
                var batch = available && toolBoundaries.Count(pair => pair.Value == toolBoundary) > 1;
                points.Add(new RewindPoint(index, RewindTargetKind.Tool, call.Name,
                    available ? null : "Нет сохранённой границы после результатов этого вызова.", call, batch));
            }
            var answer = string.Join("\n", turn.Events.Where(item => item.Kind == "text").Select(item => item.Text));
            if (string.IsNullOrWhiteSpace(answer)) answer = "Ответ без текста";
            if (turn.Status != "completed") answer += "\n" + (turn.Error ?? "Ответ прерван.");
            var reason = index == snapshot.Turns.Count - 1 ? "История уже находится в этой точке." :
                snapshot.Turns[index + 1].HistoryStart is null ? "Нет сохранённой границы после этого ответа." : null;
            points.Add(new RewindPoint(index, RewindTargetKind.Assistant, answer, reason));
        }
        return points.ToImmutable();
    }

    public static Preparation Prepare(SessionSnapshot snapshot, RewindToTurn command,
        IReadOnlyDictionary<string, int> toolBoundaries, bool hasAgent)
    {
        var index = command.Index;
        if (index < 0 || index >= snapshot.Turns.Count ||
            !Enum.IsDefined(command.Kind)) return new Preparation(null, null);
        if ((command.SessionId is not null && command.SessionId != snapshot.Id) ||
            (command.ExpectedTurnCount is { } count && count != snapshot.Turns.Count)) return new Preparation(null, null);
        var keepAnswer = command.Kind == RewindTargetKind.Assistant;
        var keepTool = command.Kind == RewindTargetKind.Tool;
        if (keepAnswer && index == snapshot.Turns.Count - 1) return new Preparation(null, null);
        var boundaryIndex = keepAnswer || keepTool ? index + 1 : index;
        int boundary;
        var toolEventEnd = -1;
        if (keepTool)
        {
            var point = GetPoints(snapshot, toolBoundaries).FirstOrDefault(point => point.Index == index &&
                point.Kind == RewindTargetKind.Tool && point.ToolCall?.CallId == command.CallId);
            if (point is null || point.UnavailableReason is not null || command.CallId is null || !hasAgent)
                return new Preparation(null, "Для этого вызова нет сохранённой границы контекста.");
            var boundaries = toolBoundaries;
            boundary = boundaries[command.CallId];
            toolEventEnd = snapshot.Turns[index].Events.FindLastIndex(item => item.ToolCall is { } call &&
                boundaries.TryGetValue(call.CallId, out var end) && end <= boundary);
            if (toolEventEnd < 0) return new Preparation(null, "Не найдены события выбранного вызова.");
        }
        else
        {
            if (snapshot.Turns[boundaryIndex].HistoryStart is not { } savedBoundary)
                return new Preparation(null, "Для этой точки нет сохранённой границы контекста.");
            boundary = savedBoundary;
        }
        return new Preparation(new Plan(index, boundaryIndex, boundary, toolEventEnd, keepAnswer, keepTool), null);
    }

    public async Task<SessionSnapshot> ExecuteAsync(Plan plan, SessionSnapshot current, AgentSessionState session,
        Func<JsonElement, ValueTask> restoreSession, Action<int> rewindHistory)
    {
        JsonElement? original = null;
        try
        {
            var next = ConversationSnapshotMapper.Copy(current);
            var selected = next.Turns[plan.Index];
            if (current.AgentState is { } state) await restoreSession(state).ConfigureAwait(false);
            original = await session.SerializeAsync().ConfigureAwait(false);
            rewindHistory(plan.Boundary);
            next.AgentState = await session.SerializeAsync().ConfigureAwait(false);
            await restoreSession(original.Value).ConfigureAwait(false);
            next.Turns.RemoveRange(plan.BoundaryIndex, next.Turns.Count - plan.BoundaryIndex);
            if (plan.KeepTool)
            {
                selected.Events.RemoveRange(plan.ToolEventEnd + 1, selected.Events.Count - plan.ToolEventEnd - 1);
                selected.Plan = null; // A partial-turn rewind invalidates plan approval targets.
                selected.Status = "interrupted";
                selected.Error = "История откатана после вызова инструмента. Ожидается уточнение пользователя; выполненные действия не отменены.";
                selected.Usage = null;
                selected.TokensPerSecond = null;
            }
            var keepRequest = plan.KeepAnswer || plan.KeepTool;
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
            await storage.SaveAsync(next).ConfigureAwait(false);
            await restoreSession(next.AgentState.Value).ConfigureAwait(false);
            return next;
        }
        catch (Exception error)
        {
            var contextFailed = false;
            if (original is { } state)
            {
                try { await restoreSession(state).ConfigureAwait(false); }
                catch { contextFailed = true; }
            }
            throw new Failure(error, contextFailed);
        }
    }
}

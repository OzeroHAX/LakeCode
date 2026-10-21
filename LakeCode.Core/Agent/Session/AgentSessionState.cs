using System.Text.Json;
using LakeCode.Core.Agent.Compaction;
using LakeCode.Core.Agent.Session.History;
using Microsoft.Agents.AI;

namespace LakeCode.Core.Agent.Session;

/// <summary>Owns one MAF session and preserves the persisted LakeCode envelope.</summary>
internal sealed class AgentSessionState(AIAgent agent, AgentHistory history)
{
    public AgentSession? Current { get; private set; }
    public string? HistoryOrigin { get; set; }

    public async ValueTask<AgentSession> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
        Current ??= await agent.CreateSessionAsync(cancellationToken: cancellationToken);

    /// <summary>Call only between runs, after the response stream has been fully consumed.</summary>
    public ValueTask<JsonElement> SerializeAsync(CancellationToken cancellationToken = default) =>
        SerializeWithCompactionAsync(history.Projection.State, cancellationToken);

    internal async ValueTask<JsonElement> SerializeWithCompactionAsync(CompactionState compaction, CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateAsync(cancellationToken);
        HistoryProjection.ValidateState(compaction, history.Messages);
        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        return JsonSerializer.SerializeToElement(new { LakeCodeHistoryOrigin = HistoryOrigin, LakeCodeCompaction = compaction, FrameworkState = state });
    }

    public async ValueTask RestoreAsync(JsonElement state, CancellationToken cancellationToken = default)
    {
        history.Capture.ClearSideContext();
        history.Capture.Reset();
        var compaction = state.TryGetProperty("LakeCodeCompaction", out var savedCompaction)
            ? savedCompaction.Deserialize<CompactionState>() ?? throw new InvalidDataException("Повреждён checkpoint summary.")
            : CompactionState.Empty;
        if (state.TryGetProperty("FrameworkState", out var frameworkState))
        {
            HistoryOrigin = state.GetProperty("LakeCodeHistoryOrigin").GetString();
            state = frameworkState;
        }
        else HistoryOrigin = null;
        Current = await agent.DeserializeSessionAsync(state, cancellationToken: cancellationToken);
        history.Projection.SetState(compaction);
    }
}

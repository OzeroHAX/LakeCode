using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Attachments;
using LakeCode.Core.Sessions;

namespace LakeCode.Core.Conversations.Projection;

/// <summary>Copies privately owned snapshots and projects them to immutable conversation views.</summary>
internal static class ConversationSnapshotMapper
{
    public static RequestStatistics ToStatistics(SavedUsage usage, ModelInfo model) =>
        new(model, usage.Input, usage.Output, usage.Total, usage.Cached, usage.Reasoning);

    public static void ValidateImages(SessionSnapshot snapshot)
    {
        foreach (var images in snapshot.Turns.Select(turn => turn.Images).Append(snapshot.DraftImages))
        {
            ImageAttachments.ValidateTotal(images.Select(image => (ReadOnlyMemory<byte>)image.Data));
            foreach (var image in images) image.Decode();
        }
    }

    public static ConversationView ToView(SessionSnapshot? snapshot) => new(snapshot?.Id, snapshot?.ModelId, snapshot?.ReasoningEffort,
        snapshot?.Turns.Select(turn => new TurnView(turn.Text, turn.Images.Select(image => image.Decode()).ToImmutableArray(),
            turn.Events.Select(item => new TranscriptItem(item.Kind, item.Text, item.ToolCall, item.StartsNewBlock)).ToImmutableArray(),
            turn.Status, turn.Error, turn.Files.ToImmutableArray(), turn.Mentions.ToImmutableArray(), turn.IsBackgroundContinuation, turn.AgentMode)).ToImmutableArray() ?? [],
        snapshot?.DraftText ?? "", snapshot?.DraftImages.Select(image => image.Decode()).ToImmutableArray() ?? [], snapshot?.ProviderId,
        snapshot?.DraftFiles.ToImmutableArray() ?? [], snapshot?.DraftMentions.ToImmutableArray() ?? []);

    // Mutable DTOs never escape Core. Image buffers are privately owned and never modified after acceptance.
    public static SessionSnapshot Copy(SessionSnapshot source) => new()
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

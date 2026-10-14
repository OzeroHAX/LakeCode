using LakeCode.Core.Conversations;
using LakeCode.Core.Providers;
using LakeCode.Core.Agent;
using System.Text.Json.Serialization;

namespace LakeCode.Core.Sessions;

internal sealed record PendingMessage(
    string Text, SavedImage[] Images, string ModelId, string ReasoningEffort, SubmissionMode Mode,
    string ProviderId = ProviderProfile.LegacyId)
{
    public LakeCode.Core.Attachments.FileAttachment[] Files { get; init; } = [];
    public LakeCode.Core.Attachments.FileMention[] Mentions { get; init; } = [];
    public bool IsBackgroundContinuation { get; init; }
    [JsonIgnore] public ProviderProfile? Connection { get; init; }
    [JsonIgnore] public ModelInfo? Model { get; init; }
}

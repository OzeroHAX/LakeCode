using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Attachments;

namespace LakeCode.Core.Conversations;

/// <summary>User intent. Execute returns acceptance, not completion of generation.</summary>
public abstract record ConversationCommand;
public sealed record SubmitMessage(string Text, ImmutableArray<ImageData> Images, string ModelId,
    string ReasoningEffort, SubmissionMode Mode, string ProviderId = ProviderProfile.LegacyId,
    ImmutableArray<FileAttachment> Files = default, ImmutableArray<FileMention> Mentions = default) : ConversationCommand;
public sealed record StopGeneration : ConversationCommand;
public sealed record AskSideQuestion(string Question) : ConversationCommand;
public sealed record CancelSideQuestion(Guid RequestId) : ConversationCommand;
public sealed record ClearSideQuestions : ConversationCommand;
public sealed record StartNewSession(string ModelId, string ProviderId = ProviderProfile.LegacyId) : ConversationCommand;
public sealed record ListSessions : ConversationCommand;
public sealed record OpenSession(string SessionId) : ConversationCommand;
public sealed record RewindToTurn(int Index) : ConversationCommand;
public sealed record AnswerQuestion(Guid InteractionId, string[][]? Answers) : ConversationCommand;
public sealed record ResolveReadPermission(Guid InteractionId, ExternalReadDecision Decision) : ConversationCommand;
public sealed record FailInteraction(Guid InteractionId, string Error) : ConversationCommand;

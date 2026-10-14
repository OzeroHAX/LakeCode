using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Tools.Ask;
using LakeCode.Core.Attachments;
using LakeCode.Core.Sessions;

namespace LakeCode.Core.Conversations;

/// <summary>Facts, not commands or a replacement for current state. Events are ordered per service.</summary>
public abstract record ConversationEvent;
public sealed record ConversationReplaced(ConversationView Conversation) : ConversationEvent;
public sealed record RewindFinished(string? Error) : ConversationEvent;
public sealed record SessionsListed(ImmutableArray<SessionSummary> Sessions, ImmutableArray<string> Errors,
    string? ActiveId) : ConversationEvent;
public sealed record TurnStarted(string Text, ImmutableArray<ImageData> Images,
    ImmutableArray<FileAttachment> Files, ImmutableArray<FileMention> Mentions) : ConversationEvent;
public sealed record ReasoningDelta(string Text) : ConversationEvent;
public sealed record TextDelta(string Text) : ConversationEvent;
public sealed record ToolCallChanged(ToolCallUpdate Call) : ConversationEvent;
public sealed record BashOutputChanged(BashOutputUpdate Update) : ConversationEvent;
public sealed record TurnFinished(string Status, string? Error) : ConversationEvent;
public sealed record ConversationError(string Message) : ConversationEvent;
public sealed record SideQuestionsChanged(SideQuestionState State) : ConversationEvent;
public sealed record QuestionRequested(Guid InteractionId, ImmutableArray<AskQuestion> Questions) : ConversationEvent;
public sealed record ReadPermissionRequested(Guid InteractionId, ExternalReadRequest Request) : ConversationEvent;
public sealed record InteractionFinished(Guid InteractionId) : ConversationEvent;
public sealed record ConversationEventEnvelope(long Sequence, string? SessionId, Guid? RequestId,
    ConversationEvent Event);

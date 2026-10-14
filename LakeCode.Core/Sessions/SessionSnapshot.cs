using System.Text.Json;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Attachments;

namespace LakeCode.Core.Sessions;

internal sealed class SessionSnapshot
{
    public int SchemaVersion { get; set; } = 3;
    public string FrameworkVersion { get; set; } = "1.24.0";
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string WorkspaceRoot { get; set; } = "";
    public string Title { get; set; } = "Новая сессия";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ModelInfo? ModelMetadata { get; set; }
    public string ProviderId { get; set; } = ProviderProfile.LegacyId;
    public string ModelId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "medium";
    // Includes received history from completed, failed and interrupted runs.
    public JsonElement? AgentState { get; set; }
    public string? DraftText { get; set; }
    public List<SavedImage> DraftImages { get; set; } = [];
    public List<FileAttachment> DraftFiles { get; set; } = [];
    public List<FileMention> DraftMentions { get; set; } = [];
    public List<SessionTurn> Turns { get; set; } = [];
    // Owned by ConversationService; snapshots for persistence are captured under its state lock.
    public PendingMessage[] PendingMessages { get; set; } = [];
}

internal sealed class SessionTurn
{
    public int? HistoryStart { get; set; }
    public bool IsBackgroundContinuation { get; set; }
    public string Text { get; set; } = "";
    public List<SavedImage> Images { get; set; } = [];
    public List<FileAttachment> Files { get; set; } = [];
    public List<FileMention> Mentions { get; set; } = [];
    public ModelInfo? ModelMetadata { get; set; }
    public string ProviderId { get; set; } = ProviderProfile.LegacyId;
    public string ModelId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public string Status { get; set; } = "in_progress";
    public string? Error { get; set; }
    public List<ResponseEvent> Events { get; set; } = [];
    public SavedUsage? Usage { get; set; }
    public double? TokensPerSecond { get; set; }

    public void AppendText(string kind, string text)
    {
        if (Events.LastOrDefault() is { } previous && previous.Kind == kind)
            previous.Text += text;
        else
            Events.Add(new ResponseEvent { Kind = kind, Text = text });
    }
}

internal sealed record SavedImage(string Name, byte[] Data)
{
    public ImageData Decode()
    {
        if (Data.Length > ImageAttachments.MaxImageBytes)
            throw new InvalidDataException("Сохранённое изображение превышает 8 MiB.");
        return ImageAttachments.Decode(Data, Name);
    }
}

internal sealed class ResponseEvent
{
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public ToolCallUpdate? ToolCall { get; set; }
}

internal sealed record SavedUsage(long? Input, long? Cached, long? Output, long? Reasoning, long? Total);
public sealed record SessionSummary(string Id, string Title, DateTimeOffset UpdatedAt, int Turns);

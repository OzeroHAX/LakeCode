using System.Text.Json;
using LakeCode.Cli.Agent;
using LakeCode.Cli.Components;

namespace LakeCode.Cli.Sessions;

internal sealed class SessionSnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public string FrameworkVersion { get; set; } = "1.24.0";
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string WorkspaceRoot { get; set; } = "";
    public string Title { get; set; } = "Новая сессия";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ModelId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "medium";
    // Includes received history from completed, failed and interrupted runs.
    public JsonElement? AgentState { get; set; }
    public string? DraftText { get; set; }
    public List<SavedImage> DraftImages { get; set; } = [];
    public List<SessionTurn> Turns { get; set; } = [];
}

internal sealed class SessionTurn
{
    public int? HistoryStart { get; set; }
    public string Text { get; set; } = "";
    public List<SavedImage> Images { get; set; } = [];
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
    public ImageAttachment Decode()
    {
        if (Data.Length > ImageInput.MaxImageBytes)
            throw new InvalidDataException("Сохранённое изображение превышает 8 MiB.");
        return ImageInput.Decode(Data, Name);
    }
}

internal sealed class ResponseEvent
{
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public ToolCallUpdate? ToolCall { get; set; }
}

internal sealed record SavedUsage(long? Input, long? Cached, long? Output, long? Reasoning, long? Total);
internal sealed record SessionSummary(string Id, string Title, DateTimeOffset UpdatedAt, int Turns);

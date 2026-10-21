using LakeCode.Core.Agent;
using SharpConsoleUI;

namespace LakeCode.Cli.Components;

/// <summary>Shared palette and status wording for the bash and eval tool cards.</summary>
internal static class ToolCardStyle
{
    public static readonly Color Surface = new(26, 26, 26);
    public static readonly Color Border = new(85, 85, 85);
    public static readonly Color Focused = new(45, 45, 45);
    /// <summary>Tool description: the only accent colour in the card.</summary>
    public static readonly Color Description = new(155, 195, 205);
    public static readonly Color Command = new(235, 235, 235);
    /// <summary>Matches the syntax-highlight theme background so a code strip has no seam.</summary>
    public static readonly Color CommandSurface = new(38, 40, 46);
    /// <summary>Prompt and path; deliberately dimmer than the command itself.</summary>
    public static readonly Color Prompt = new(120, 160, 175);
    public static readonly Color Output = new(170, 170, 170);
    public static readonly Color Muted = new(125, 125, 125);
    public static readonly Color Warning = new(215, 175, 110);
    public static readonly Color Error = new(235, 145, 145);

    public static (string Label, string Color) Status(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Classifying => ("проверка Jev", "cyan"),
        ToolCallStatus.Running => ("выполняется", "cyan"),
        ToolCallStatus.AwaitingApproval => ("ожидает подтверждения", "yellow"),
        ToolCallStatus.Completed => ("завершено", "green"),
        ToolCallStatus.Failed => ("ошибка", "red"),
        ToolCallStatus.Cancelled => ("отменено", "yellow"),
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string Duration(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }
}

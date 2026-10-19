using LakeCode.Core.Agent.Compaction;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

/// <summary>Derived context card, not a user/assistant turn and not an execution approval.</summary>
internal sealed class CompactionComponent : CollapsiblePanel
{
    public string CheckpointId { get; }

    public CompactionComponent(CompactionView view)
    {
        CheckpointId = view.Id;
        Title = $"Контекст сжат · текст UTF-8: {view.BeforeBytes:N0} → {view.AfterBytes:N0} байт · архив сохранён";
        HorizontalAlignment = HorizontalAlignment.Stretch;
        IsExpanded = false;
        Margin = new Margin(0, 1, 0, 1);
        var body = new MarkupControl([]) { Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.SetMarkdown($"Модель: {view.ModelId}\n\n" +
            $"Usage summary: input {view.InputTokens?.ToString() ?? "неизвестно"}, output {view.OutputTokens?.ToString() ?? "неизвестно"}, total {view.TotalTokens?.ToString() ?? "неизвестно"}.\n\n" +
            "Размер — текстовое представление, не число токенов и не размер всей сессии. " +
            "Смысловая корректность LLM-проверяющим не проверялась. Полная история и rewind сохранены.\n\n" + view.Markdown);
        var viewport = new ScrollablePanelControl
        {
            BorderStyle = BorderStyle.None, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Padding(2, 1, 2, 1), ShowScrollbar = true
        };
        viewport.AddControl(body);
        AddControl(viewport);
    }
}

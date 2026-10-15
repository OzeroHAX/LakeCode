using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace LakeCode.Cli.Components;

internal sealed class HeaderComponent : PanelControl
{
    private readonly MarkupControl _label;
    private readonly string _title;
    private readonly string _subtitle;
    private readonly ButtonControl _subagents;
    public event Action? SubagentsRequested;

    public HeaderComponent(string title, string subtitle)
    {
        BorderStyle = BorderStyle.Rounded;
        BorderColor = new (85, 85, 85);
        BackgroundColor = new SharpConsoleUI.Color(26, 26, 26);
        ForegroundColor = new SharpConsoleUI.Color(185, 185, 185);
        Padding = new(1, 0, 1, 0);
        Margin = new (1, 0, 1, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _title = title;
        _subtitle = subtitle;
        _label = new MarkupControl(new List<string>())
        {
            Height = 1,
            BackgroundColor = BackgroundColor,
            ForegroundColor = ForegroundColor,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _subagents = new ButtonControl
        {
            Text = "Сабагенты", Visible = false, HorizontalAlignment = HorizontalAlignment.Right,
            BackgroundColor = BackgroundColor, ForegroundColor = new SharpConsoleUI.Color(155, 195, 205),
            FocusedBackgroundColor = new SharpConsoleUI.Color(35, 65, 75)
        };
        _subagents.Click += (_, _) => SubagentsRequested?.Invoke();
        var row = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var titleColumn = new ColumnContainer(row) { FlexFactor = 1 };
        titleColumn.AddContent(_label);
        var tasksColumn = new ColumnContainer(row) { FlexFactor = 0 };
        tasksColumn.AddContent(_subagents);
        row.AddColumn(titleColumn);
        row.AddColumn(tasksColumn);

        AddControl(row);
        SetSessionTitle(null);
    }

    public void SetDelegations(IReadOnlyCollection<LakeCode.Core.Agent.Delegation.DelegationSnapshot> tasks)
    {
        _subagents.Visible = tasks.Count > 0;
        var running = tasks.Count(task => task.Status == LakeCode.Core.Agent.Delegation.DelegationStatus.Running);
        var waiting = tasks.Count(task => task.Status == LakeCode.Core.Agent.Delegation.DelegationStatus.WaitingForParent);
        var failed = tasks.Count(task => task.Status == LakeCode.Core.Agent.Delegation.DelegationStatus.Failed);
        _subagents.Text = $"Сабагенты · {running} работают" +
            (waiting == 0 ? "" : $" · {waiting} ждут") + (failed == 0 ? "" : $" · ошибок: {failed}");
    }

    public void SetSessionTitle(string? sessionTitle)
    {
        _label.Text = $"[bold cyan]{Markup.Escape(_title)}[/]  [grey70]{Markup.Escape(_subtitle)}[/]" +
            (string.IsNullOrWhiteSpace(sessionTitle)
                ? ""
                : $"  [grey50]│[/]  {Markup.Escape(sessionTitle)}");
    }
}

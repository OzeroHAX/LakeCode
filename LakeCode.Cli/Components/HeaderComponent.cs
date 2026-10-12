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

    public HeaderComponent(string title, string subtitle)
    {
        BorderStyle = BorderStyle.Rounded;
        BorderColor = new (80, 130, 145);
        Padding = new(1, 0, 1, 0);
        Margin = new (1, 0, 1, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _title = title;
        _subtitle = subtitle;
        _label = new MarkupControl(new List<string>())
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddControl(_label);
        SetSessionTitle(null);
    }

    public void SetSessionTitle(string? sessionTitle)
    {
        _label.Text = $"[bold cyan]{Markup.Escape(_title)}[/]  [grey70]{Markup.Escape(_subtitle)}[/]" +
            (string.IsNullOrWhiteSpace(sessionTitle)
                ? ""
                : $"  [grey50]│[/]  {Markup.Escape(sessionTitle)}");
    }
}

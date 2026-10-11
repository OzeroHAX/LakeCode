using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace LakeCode.Cli.Components;

internal sealed class HeaderComponent : PanelControl
{
    public HeaderComponent(string title, string subtitle)
    {
        BorderStyle = BorderStyle.Rounded;
        BorderColor = new (80, 130, 145);
        Padding = new(1, 0, 1, 0);
        Margin = new (1, 0, 1, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        AddControl(new MarkupControl(new List<string>
        {
            $"[bold cyan]{Markup.Escape(title)}[/]  [grey70]{Markup.Escape(subtitle)}[/]"
        })
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
    }
}

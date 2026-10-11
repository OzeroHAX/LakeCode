using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed class UserMessageComponent : ScrollablePanelControl
{
    public UserMessageComponent(string message)
    {
        BorderStyle = BorderStyle.None;
        ShowScrollbar = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Padding = new Padding(2, 1, 2, 1);
        Margin = new Margin(1, 0, 1, 1);
        BackgroundColor = new Color(26, 26, 26);

        var body = new MarkupControl(new List<string>())
        {
            Wrap = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        body.SetMarkdown(message);
        AddControl(body);
    }
}

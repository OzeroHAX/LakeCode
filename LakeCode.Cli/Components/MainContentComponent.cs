using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed class MainContentComponent : ScrollablePanelControl
{
    public MainContentComponent()
    {
        ShowScrollbar = true;
        ScrollbarColor = new Color(50, 50, 50);
        ScrollbarThumbColor = new Color(105, 105, 105);
        AutoScroll = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Fill;
        Margin = new Margin(1, 0, 1, 0);
    }

    public void AddUserMessage(string message, IReadOnlyList<ImageAttachment> attachments,
        IReadOnlyList<LakeCode.Core.Attachments.FileAttachment>? files = null,
        IReadOnlyList<LakeCode.Core.Attachments.FileMention>? mentions = null)
    {
        AddControl(new UserMessageComponent(message, attachments, files, mentions));
    }
}

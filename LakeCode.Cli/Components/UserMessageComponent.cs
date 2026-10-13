using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed class UserMessageComponent : ScrollablePanelControl
{
    public UserMessageComponent(string message, IReadOnlyList<ImageAttachment> attachments,
        IReadOnlyList<LakeCode.Core.Attachments.FileAttachment>? files = null,
        IReadOnlyList<LakeCode.Core.Attachments.FileMention>? mentions = null)
    {
        BorderStyle = BorderStyle.None;
        ShowScrollbar = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Padding = new Padding(2, 1, 2, 1);
        Margin = new Margin(1, 0, 1, 1);
        BackgroundColor = new Color(26, 26, 26);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var body = new MarkupControl(new List<string>())
            {
                Wrap = true,
                EnableSelection = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (mentions is { Count: > 0 })
            {
                var display = new System.Text.StringBuilder();
                var offset = 0;
                foreach (var mention in mentions.OrderBy(item => item.Start))
                {
                    display.Append(SharpConsoleUI.Parsing.MarkupParser.Escape(message[offset..mention.Start]));
                    display.Append("[white on #23414b]").Append(SharpConsoleUI.Parsing.MarkupParser.Escape(message.Substring(mention.Start, mention.Length))).Append("[/]");
                    offset = mention.Start + mention.Length;
                }
                display.Append(SharpConsoleUI.Parsing.MarkupParser.Escape(message[offset..]));
                body.Text = display.ToString();
            }
            else body.SetMarkdown(message);
            AddControl(body);
        }
        foreach (var attachment in attachments)
            AddControl(new ImageAttachmentComponent(attachment));
        foreach (var file in files ?? [])
            AddControl(new FileAttachmentComponent(file.Path, file));
    }
}

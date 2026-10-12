using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Imaging;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class ImageAttachmentComponent : CollapsiblePanel
{
    public ImageAttachment Attachment { get; }

    public ImageAttachmentComponent(ImageAttachment attachment, Action? remove = null)
    {
        Attachment = attachment;
        Title = $"{MarkupParser.Escape(attachment.Name)} · {attachment.Width}×{attachment.Height} · {attachment.Data.Length / 1024.0:0.#} KiB";
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        IsExpanded = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MaxContentHeight = 12;
        ForegroundColor = new Color(155, 195, 205);
        FocusedBackgroundColor = new Color(35, 65, 75);
        AddControl(new ImageControl
        {
            Source = attachment.Preview,
            ScaleMode = ImageScaleMode.Fit,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        if (remove is not null)
        {
            var delete = new ButtonControl
            {
                Text = "Удалить изображение",
                ForegroundColor = new Color(235, 145, 145),
                BackgroundColor = Color.Transparent,
                FocusedBackgroundColor = new Color(65, 35, 35)
            };
            delete.Click += (_, _) => remove();
            AddControl(delete);
        }
    }
}

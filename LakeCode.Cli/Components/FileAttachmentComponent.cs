using LakeCode.Core.Attachments;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class FileAttachmentComponent : CollapsiblePanel
{
    public FileAttachmentComponent(string path, FileAttachment? attachment = null, Action? remove = null)
    {
        var mode = attachment is null ? "Содержимое или ссылка — при отправке"
            : attachment.Content is null ? "Ссылка" : "Снимок содержимого";
        Title = $"{MarkupParser.Escape(path)} · {mode}" +
            (attachment is null ? "" : $" · ≈{attachment.EstimatedTokens} токенов");
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        IsExpanded = false;
        MaxContentHeight = 10;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ForegroundColor = new Color(155, 195, 205);
        FocusedBackgroundColor = new Color(35, 65, 75);
        AddControl(new MarkupControl(new List<string>
        {
            MarkupParser.Escape(attachment?.Content ?? attachment?.ReferenceReason ??
                "До ≈4 000 токенов на файл; общий бюджет — до ≈12 000 и 10% окна модели. Большие файлы отправляются ссылками.")
        }) { Wrap = true, EnableSelection = true });
        if (remove is not null)
        {
            var button = new ButtonControl
            {
                Text = "Удалить файл", ForegroundColor = new Color(235, 145, 145),
                BackgroundColor = Color.Transparent, FocusedBackgroundColor = new Color(65, 35, 35)
            };
            button.Click += (_, _) => remove();
            AddControl(button);
        }
    }
}

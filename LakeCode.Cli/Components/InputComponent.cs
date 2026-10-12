using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class InputComponent : PanelControl
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly CancellationToken _shutdown;
    private readonly ScrollablePanelControl _attachmentsPanel = new();
    private readonly List<ImageAttachmentComponent> _attachments = [];
    private readonly MarkupControl _status = new(new List<string>());
    private CancellationTokenSource? _operation;
    private Window? _window;
    private bool _disposed;

    public bool HasAttachments => _attachments.Count != 0;
    public bool IsImporting => _operation is not null;
    public PromptControl Editor { get; }

    public ImageAttachment[] GetAttachments() => _attachments.Select(item => item.Attachment).ToArray();

    public InputComponent(ConsoleWindowSystem windowSystem, CancellationToken shutdown)
    {
        _windowSystem = windowSystem;
        _shutdown = shutdown;
        BorderStyle = BorderStyle.Rounded;
        BorderColor = new Color(80, 130, 145);
        Padding = new Padding(1, 0, 1, 0);
        Margin = new Margin(1, 1, 1, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        Editor = new PromptControl
        {
            Prompt = "[cyan]>[/] ",
            Placeholder = "Опишите задачу… (Enter — отправить, Shift+Enter — новая строка)",
            UnfocusOnEnter = false,
            Multiline = true,
            EnterBehavior = EnterBehavior.Submit,
            MinRows = 3,
            MaxRows = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            InputBackgroundColor = Color.Transparent,
            InputFocusedBackgroundColor = Color.Transparent
        };

        _attachmentsPanel.BorderStyle = BorderStyle.None;
        _attachmentsPanel.Padding = new Padding(0, 0, 0, 0);
        _attachmentsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _attachmentsPanel.ShowScrollbar = true;
        _attachmentsPanel.Visible = false;
        AddControl(_attachmentsPanel);
        AddControl(Editor);
        _status.Wrap = true;
        _status.HorizontalAlignment = HorizontalAlignment.Stretch;
        _status.Visible = false;
        AddControl(_status);
    }

    public void Attach(Window window)
    {
        _window = window;
        window.PreviewKeyPressed += OnPreviewKeyPressed;
    }

    private void OnPreviewKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || !Editor.HasFocus || !Editor.IsEnabled ||
            e.KeyInfo.Modifiers != ConsoleModifiers.Control) return;
        if (e.KeyInfo.Key == ConsoleKey.V)
        {
            e.Handled = true;
            StartImport(token => ImageInput.ReadClipboardAsync(token));
        }
    }

    private void StartImport(Func<CancellationToken, Task<ClipboardInput>> read)
    {
        if (_disposed || IsImporting) return;
        var operation = BeginOperation();
        _ = ImportAsync(operation, read);
    }

    private CancellationTokenSource BeginOperation()
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _operation = operation;
        _status.Text = "[cyan]Чтение буфера или изображения…[/]";
        _status.Visible = true;
        return operation;
    }

    private async Task ImportAsync(CancellationTokenSource operation, Func<CancellationToken, Task<ClipboardInput>> read)
    {
        try
        {
            var result = await Task.Run(() => read(operation.Token), operation.Token);
            CompleteImport(operation, result, null);
        }
        catch (OperationCanceledException) { CompleteImport(operation, null, null); }
        catch (Exception error) { CompleteImport(operation, null, error.Message); }
    }

    private void CompleteImport(CancellationTokenSource operation, ClipboardInput? result, string? error)
    {
        _windowSystem.EnqueueOnUIThread(() =>
        {
            try
            {
                if (_disposed || _operation != operation) return;
                _operation = null;
                _status.Visible = false;
                if (operation.IsCancellationRequested) return;
                if (error is not null) ShowError(error);
                else if (result?.Image is { } image) AddAttachment(image);
                else if (result?.Text is { } text) Editor.Paste(text);
                _window?.FocusControl(Editor);
            }
            finally { operation.Dispose(); }
        });
    }

    private void AddAttachment(ImageAttachment image)
    {
        if (_attachments.Count >= ImageInput.MaxAttachments)
        {
            ShowError("Можно прикрепить не больше 4 изображений. Удалите лишнее и повторите вставку.");
            return;
        }
        if (_attachments.Sum(item => item.Attachment.Data.Length) + image.Data.Length > ImageInput.MaxTotalBytes)
        {
            ShowError("Общий размер изображений превышает 24 MiB. Удалите лишнее и повторите вставку.");
            return;
        }
        ImageAttachmentComponent? component = null;
        component = new ImageAttachmentComponent(image, () =>
        {
            _window?.FocusControl(Editor);
            ((IControlHost)_attachmentsPanel).RemoveControl(component!);
            _attachments.Remove(component!);
            component!.Dispose();
            UpdateAttachmentHeight();
            _status.Visible = false;
        });
        component.ExpandedChanged += (_, _) => UpdateAttachmentHeight();
        _attachments.Add(component);
        _attachmentsPanel.AddControl(component);
        UpdateAttachmentHeight();
        _status.Visible = false;
    }

    private void UpdateAttachmentHeight()
    {
        _attachmentsPanel.Visible = HasAttachments;
        _attachmentsPanel.Height = Math.Min(14, _attachments.Sum(item => item.IsExpanded ? 12 : 2));
    }

    public void ShowError(string message)
    {
        _status.Text = $"[red]{MarkupParser.Escape(message)}[/]";
        _status.Visible = true;
    }

    public void SetSending(bool sending)
    {
        Editor.IsEnabled = !sending;
    }

    public void ClearDraft()
    {
        _operation?.Cancel();
        _operation = null;
        _window?.FocusControl(Editor);
        ((IControlHost)_attachmentsPanel).ClearControls();
        foreach (var attachment in _attachments) attachment.Dispose();
        _attachments.Clear();
        UpdateAttachmentHeight();
        Editor.Input = string.Empty;
        _status.Visible = false;
    }

    protected override void OnDisposing()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window is not null) _window.PreviewKeyPressed -= OnPreviewKeyPressed;
        _operation?.Cancel();
        _operation = null;
        base.OnDisposing();
    }
}

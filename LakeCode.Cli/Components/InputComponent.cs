using LakeCode.Core.Conversations;
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
    public string[] GetFilePaths() => Editor.FilePaths;
    public long DraftRevision { get; private set; }
    private readonly MarkupControl _status = new(new List<string>());
    private CancellationTokenSource? _operation;
    private Window? _window;
    private bool _disposed;

    public bool HasAttachments => _attachments.Count != 0 || Editor.Mentions.Length != 0;
    public bool IsImporting => _operation is not null;
    public InlinePromptControl Editor { get; }
    public event Action<SubmissionMode>? Submitted;
    private readonly MarkupControl _pending = new(new List<string>());

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

        Editor = new InlinePromptControl
        {
            Prompt = "[cyan]>[/] ",
            Placeholder = OperatingSystem.IsWindows()
                ? "Опишите задачу… (Enter — отправить, Shift+Enter — новая строка)"
                : "Опишите задачу… (Enter — отправить, Alt+Enter / Ctrl+L — новая строка)",
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
        _pending.Wrap = true;
        _pending.Visible = false;
        AddControl(_pending);
        AddControl(new MarkupControl(new List<string>
        {
            "[grey70]Enter — уточнить · F6 — в очередь · F7 — прервать и отправить[/]"
        }) { Wrap = true });
        Editor.Entered += (_, _) => Submitted?.Invoke(SubmissionMode.Clarify);
        Editor.InputChanged += (_, _) => DraftRevision++;
    }

    public void Attach(Window window)
    {
        _window = window;
        window.PreviewKeyPressed += OnPreviewKeyPressed;
    }

    private void OnPreviewKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || !Editor.HasFocus || !Editor.IsEnabled) return;
        if (e.KeyInfo.Modifiers == 0 && e.KeyInfo.Key is ConsoleKey.F6 or ConsoleKey.F7)
        {
            e.Handled = true;
            if (Editor.Input.StartsWith('/')) return; // Slash commands are never agent messages.
            Submitted?.Invoke(e.KeyInfo.Key == ConsoleKey.F6 ? SubmissionMode.Enqueue : SubmissionMode.Interrupt);
            return;
        }
        if (e.KeyInfo.Modifiers != ConsoleModifiers.Control) return;
        if (e.KeyInfo.Key == ConsoleKey.V)
        {
            e.Handled = true;
            StartImport(token => ImageInput.ReadClipboardAsync(token));
        }
    }

    public CancellationTokenSource BeginFileCapture()
    {
        var operation = BeginOperation();
        _status.Text = "[cyan]Чтение файлов…[/]";
        return operation;
    }

    public bool EndFileCapture(CancellationTokenSource operation)
    {
        if (_disposed || _operation != operation) return false;
        _operation = null;
        _status.Visible = false;
        return true;
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
            DraftRevision++;
            component!.Dispose();
            UpdateAttachmentHeight();
            _status.Visible = false;
        });
        component.ExpandedChanged += (_, _) => UpdateAttachmentHeight();
        _attachments.Add(component);
        DraftRevision++;
        _attachmentsPanel.AddControl(component);
        UpdateAttachmentHeight();
        _status.Visible = false;
    }

    public bool AddFile(string path, int start, int length)
    {
        if (Editor.FilePaths.Length >= LakeCode.Core.Attachments.FileAttachments.MaxFiles && !Editor.FilePaths.Contains(path))
        {
            ShowError("Можно прикрепить не больше 16 файлов.");
            return false;
        }
        Editor.InsertFile(start, length, path);
        return true;
    }

    private void UpdateAttachmentHeight()
    {
        _attachmentsPanel.Visible = _attachments.Count != 0;
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

    public void ShowPending(IReadOnlyList<string> descriptions, bool paused)
    {
        _pending.Text = string.Join("\n", descriptions.Select(text =>
            $"[cyan]{MarkupParser.Escape(text)}[/]"));
        if (paused && descriptions.Count > 0)
            _pending.Text += "\n[yellow]Очередь приостановлена. F6 с пустым полем — продолжить.[/]";
        _pending.Visible = descriptions.Count > 0;
    }

    public void SetDraft(string text, IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count > ImageInput.MaxAttachments || images.Sum(image => (long)image.Data.Length) > ImageInput.MaxTotalBytes)
            throw new InvalidDataException("Вложения черновика превышают лимиты.");
        ClearDraft();
        Editor.Input = text;
        foreach (var image in images) AddAttachment(image);
    }

    public void ClearDraft()
    {
        _operation?.Cancel();
        _operation = null;
        _window?.FocusControl(Editor);
        ((IControlHost)_attachmentsPanel).ClearControls();
        foreach (var attachment in _attachments) attachment.Dispose();
        _attachments.Clear();
        DraftRevision++;
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

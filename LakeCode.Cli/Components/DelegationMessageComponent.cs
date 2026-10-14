using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Delegation;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class DelegationMessageComponent : CollapsiblePanel
{
    private readonly ScrollablePanelControl _viewport;
    private ExternalReadPermissionComponent? _permission;

    public DelegationMessageComponent(DelegationSnapshot task, string kind, string text, Action? observe = null)
    {
        Title = MarkupParser.Escape($"Сабагент · {Short(task.Task, 48)} · {kind} · {Short(text, 65)}");
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(3, 0, 3, 1);
        IsExpanded = false;
        MaxContentHeight = 12;
        ForegroundColor = task.Status == DelegationStatus.Failed ? new Color(235, 145, 145) : new Color(155, 195, 205);
        FocusedBackgroundColor = new Color(35, 65, 75);
        _viewport = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ShowScrollbar = true,
            Padding = new Padding(2, 1, 2, 1)
        };
        var body = new MarkupControl([])
        { Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (kind == "Ответ") body.SetMarkdown(text);
        else body.Text = MarkupParser.Escape(text);
        _viewport.AddControl(body);
        if (observe is not null)
        {
            var button = new ButtonControl { Text = "Открыть работу сабагента", BackgroundColor = Color.Transparent,
                ForegroundColor = new Color(155, 195, 205), FocusedBackgroundColor = new Color(35, 65, 75) };
            button.Click += (_, _) => observe();
            _viewport.AddControl(button);
        }
        AddControl(_viewport);
    }

    public static string Short(string text, int limit)
    {
        var line = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= limit ? line : line[..(limit - 1)] + "…";
    }

    public static string StatusLabel(DelegationStatus status) => status switch
    {
        DelegationStatus.Running => "Работает",
        DelegationStatus.WaitingForParent => "Ждёт основного агента",
        DelegationStatus.Completed => "Завершён",
        DelegationStatus.Failed => "Ошибка",
        DelegationStatus.Cancelled => "Отменён",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public void ShowReadPermission(ExternalReadRequest request, Action<ExternalReadDecision> complete, Window window)
    {
        IsExpanded = true;
        MaxContentHeight = int.MaxValue;
        _viewport.Visible = false;
        _permission = new ExternalReadPermissionComponent(request, complete);
        AddControl(_permission);
        _permission.Focus(window);
    }

    public void CancelReadPermission()
    {
        if (_permission is not { } permission) return;
        permission.Cancel();
        ((IControlHost)this).RemoveControl(permission);
        _permission = null;
        _viewport.Visible = true;
        MaxContentHeight = 12;
    }
}

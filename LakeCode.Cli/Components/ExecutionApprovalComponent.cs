using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

/// <summary>One-shot actions embedded in the owning tool card; never duplicates code or description.</summary>
internal sealed class ExecutionApprovalComponent : PanelControl
{
    private readonly ButtonControl _reject;
    private readonly HorizontalGridControl _buttons;
    private readonly MarkupControl _status;
    private bool _finished;

    public ExecutionApprovalComponent(Action<bool> complete, bool mcp = false)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 1, 0, 1);
        _reject = ProviderWindowControls.Button("Отклонить");
        var allow = ProviderWindowControls.Button("Разрешить один раз");
        _buttons = HorizontalGridControl.ButtonRow([_reject, allow]);
        _buttons.HorizontalAlignment = HorizontalAlignment.Left;
        AddControl(_buttons);
        _status = new MarkupControl(new List<string>
        {
            mcp ? "MCP-инструмент не вызван. Разрешение только на этот вызов и показанные аргументы. Внешний сервер — не sandbox."
                : "Команда не запущена. Разрешение только на этот вызов, не на реализацию плана. Это не sandbox.",
            "[grey70]Tab — выбор · Enter — решение · Esc дважды — остановить запрос[/]"
        }) { Wrap = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        AddControl(_status);
        _reject.Click += (_, _) => Finish(false);
        allow.Click += (_, _) => Finish(true);

        void Finish(bool approved)
        {
            if (_finished) return;
            _finished = true;
            _buttons.Visible = false;
            _status.Text = approved ? "[green]Разрешено вручную.[/]" : mcp
                ? "[yellow]Отклонено пользователем · MCP-инструмент не вызван.[/]"
                : "[yellow]Отклонено пользователем · команда не запускалась.[/]";
            complete(approved);
        }
    }

    public void Focus(Window window) => window.FocusControl(_reject);
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _buttons.Visible = false;
        _status.Text = "[grey70]Ожидание отменено. Разрешение не выдано.[/]";
    }
}

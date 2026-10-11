using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed class InputComponent : PanelControl
{
    public PromptControl Editor { get; }

    public InputComponent()
    {
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

        AddControl(Editor);
    }
}

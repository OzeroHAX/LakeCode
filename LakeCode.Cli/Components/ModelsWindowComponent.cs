using LakeCode.Cli.State;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;

namespace LakeCode.Cli.Components;

internal sealed class ModelsWindowComponent(ModelStore store)
{
    public void Show(ConsoleWindowSystem windowSystem)
    {
        var surface = new Color(30, 36, 39);
        var list = new ListControl
        {
            IsSelectable = true,
            BackgroundColor = surface,
            FocusedBackgroundColor = surface,
            ForegroundColor = Color.White,
            HighlightBackgroundColor = new Color(35, 65, 75),
            HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            Margin = new Margin(2, 1, 2, 0)
        };

        foreach (var provider in store.Providers.Profiles.OrderBy(profile => profile.Name, StringComparer.Ordinal))
        foreach (var model in provider.Models.OrderBy(model => model.Id, StringComparer.Ordinal))
        {
            list.AddItem(new ListItem(Markup.Escape($"{provider.Name} · {model.Name}")) { Tag = (provider.Id, model.Id) });
            if (provider.Id == store.SelectedProviderId && model.Id == store.SelectedModel.Id)
                list.SelectedIndex = list.Items.Count - 1;
        }
        if (list.Items.Count == 0) list.AddItem(new ListItem("Нет моделей. Настройте /providers."));

        var hint = new MarkupControl(new List<string>
        {
            "[grey70]Enter выбрать · Esc отмена[/]"
        })
        {
            BackgroundColor = surface,
            Margin = new Margin(2, 1, 2, 0)
        };

        var window = new WindowBuilder(windowSystem)
            .WithTitle("Выбор модели")
            .WithSize(64, Math.Min(20, list.Items.Count + 5))
            .Centered()
            .AsModal()
            .HideTitleButtons()
            .Minimizable(false)
            .Maximizable(false)
            .Resizable(false)
            .Movable(false)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(surface)
            .WithForegroundColor(Color.White)
            .AddControl(list)
            .AddControl(hint)
            .OnKeyPressed((sender, e) =>
            {
                if (e.KeyInfo.Key == ConsoleKey.Escape)
                {
                    windowSystem.CloseWindow((Window)sender!);
                    e.Handled = true;
                }
            })
            .Build();

        list.ItemActivated += (_, _) =>
        {
            if (list.SelectedItem?.Tag is not ValueTuple<string, string> selection)
                return;

            store.SelectModel(selection.Item1, selection.Item2);
            windowSystem.CloseWindow(window);
        };

        windowSystem.AddWindow(window);
        window.FocusControl(list);
    }
}

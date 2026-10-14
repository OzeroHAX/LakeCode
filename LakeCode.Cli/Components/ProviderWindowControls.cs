using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

/// <summary>Shared styling for the provider list and its editors.</summary>
internal static class ProviderWindowControls
{
    internal static readonly Color Surface = new(30, 36, 39);
    internal static readonly Color Highlight = new(35, 65, 75);

    internal static PromptControl Field(string prompt, string value = "") => new()
    {
        Prompt = prompt, Input = value, UnfocusOnEnter = false, HorizontalAlignment = HorizontalAlignment.Stretch,
        InputBackgroundColor = Surface, InputFocusedBackgroundColor = Highlight, InputForegroundColor = Color.White,
        InputFocusedForegroundColor = Color.White, Margin = new Margin(2, 1, 2, 0), MaxLength = 8192,
        HistoryEnabled = false
    };

    internal static MarkupControl Label(string text) => new([text])
    { Wrap = true, BackgroundColor = Surface, Margin = new Margin(2, 1, 2, 0) };

    internal static ButtonControl Button(string text) => new()
    { Text = text, BackgroundColor = Surface, ForegroundColor = Color.White, FocusedBackgroundColor = Highlight, Margin = new Margin(1, 0, 1, 0) };

    internal static DropdownControl Dropdown(string prompt, params string[] items) => new(prompt, items)
    {
        BackgroundColor = Surface, ForegroundColor = Color.White, FocusedBackgroundColor = Highlight,
        FocusedForegroundColor = Color.White, HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
        HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(2, 1, 2, 0)
    };

    internal static ListControl Choices(int count) => new()
    {
        IsSelectable = true, MaxVisibleItems = count, BackgroundColor = Surface, FocusedBackgroundColor = Surface,
        ForegroundColor = Color.White, HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
        HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(2, 1, 2, 0)
    };

    internal static ScrollablePanelControl Form() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
        BorderStyle = BorderStyle.None, BackgroundColor = Surface, ShowScrollbar = true, AutoScroll = true
    };

    internal static HorizontalGridControl Buttons(params ButtonControl[] buttons)
    {
        var row = HorizontalGridControl.ButtonRow(buttons);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        row.Margin = new Margin(1, 1, 1, 1);
        return row;
    }

    internal static WindowBuilder Base(ConsoleWindowSystem system, string title, int width, int height) => new WindowBuilder(system)
        .WithTitle(title).WithSize(width, height).Centered().AsModal().HideTitleButtons()
        .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
        .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
        .WithBackgroundColor(Surface).WithForegroundColor(Color.White);
}

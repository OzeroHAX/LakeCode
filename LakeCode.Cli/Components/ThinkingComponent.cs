using System.Text;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

/// <summary>Public reasoning summary only; manual expansion takes precedence over automatic collapse.</summary>
internal sealed class ThinkingComponent : CollapsiblePanel
{
    private readonly StringBuilder _text = new();
    private readonly MarkupControl _body = new(new List<string>());
    private readonly ScrollablePanelControl _viewport = new();
    private bool _automaticChange;
    private bool _userChangedExpansion;
    private readonly SpinnerTextAnimator _dots;
    private bool _completed;

    public ThinkingComponent(ConsoleWindowSystem windowSystem)
    {
        Title = "Thinking";
        _dots = new SpinnerTextAnimator(
            windowSystem, SpinnerStyle.Dots, frame => Title = $"Thinking{frame}");
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MaxContentHeight = ControlDefaults.ScrollablePanelDefaultUnboundedHeight;
        Margin = new Margin(0, 0, 0, 1);
        ForegroundColor = new Color(155, 155, 155);
        FocusedForegroundColor = new Color(185, 185, 185);
        FocusedBackgroundColor = new Color(45, 45, 45);
        Visible = false;
        _body.Wrap = true;
        _body.HorizontalAlignment = HorizontalAlignment.Stretch;
        _body.Margin = new Margin(0, 0, 0, 0);
        _body.ForegroundColor = new Color(155, 155, 155);
        _viewport.BorderStyle = BorderStyle.None;
        _viewport.HorizontalAlignment = HorizontalAlignment.Stretch;
        _viewport.Padding = new Padding(2, 1, 2, 1);
        _viewport.ShowScrollbar = true;
        _viewport.ScrollbarColor = new Color(50, 50, 50);
        _viewport.ScrollbarThumbColor = new Color(105, 105, 105);
        _viewport.AutoScroll = true;
        _viewport.AddControl(_body);
        AddControl(_viewport);
        ExpandedChanged += (_, _) =>
        {
            if (!_automaticChange)
                _userChangedExpansion = true;
        };
    }

    public void AppendReasoning(string text)
    {
        _text.Append(text);
        _body.SetMarkdown(_text.ToString());
        Visible = true;
        if (!_completed)
            _dots.Start();
        _viewport.ScrollToBottom();
    }

    public void CollapseAutomatically()
    {
        if (_userChangedExpansion)
            return;

        _automaticChange = true;
        try
        {
            IsExpanded = false;
        }
        finally
        {
            _automaticChange = false;
        }
    }

    public void Complete()
    {
        if (_completed)
            return;

        _completed = true;
        _dots.Dispose();
        Title = "Thinking";
        CollapseAutomatically();
    }
}

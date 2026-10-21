using System.Globalization;
using System.Text;
using SharpConsoleUI;
using SharpConsoleUI.Helpers;
using LakeCode.Cli.State;
using SharpConsoleUI.DataBinding;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Spectre.Console;
using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;

namespace LakeCode.Cli.Components;

internal sealed class FooterComponent : PanelControl
{
    private static readonly string IdleFrame = $"[{new SharpConsoleUI.Color(0, 255, 255, 51).ToMarkup()}]⬝⬝⬝⬝⬝⬝⬝⬝[/]";
    private static readonly string[] ScannerFrames = CreateScannerFrames();
    private readonly SpinnerTextAnimator _scanner;
    private readonly MarkupControl _animation = new([IdleFrame]) { Height = 1 };
    private readonly ButtonControl _model = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, Height = 1,
        BackgroundColor = SharpConsoleUI.Color.Transparent, ForegroundColor = SharpConsoleUI.Color.Cyan,
        FocusedBackgroundColor = new SharpConsoleUI.Color(35, 65, 75),
        Margin = new Margin(0, 0, 0, 0)
    };
    private readonly MarkupControl _debug = new(new List<string>())
    { Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly MarkupControl _details = new(new List<string>());

    public event Action? ModelsRequested;
    private readonly MarkupControl _mode = new(new List<string> { "[cyan]build[/]" })
    { HorizontalAlignment = HorizontalAlignment.Right, Height = 1 };

    private readonly MarkupControl _permissions = new(["[grey70]ext-read: ask[/]"])
    { HorizontalAlignment = HorizontalAlignment.Right, Height = 1 };

    public void SetExternalReadPermissions(bool allowAll) =>
        _permissions.Text = allowAll ? "[yellow]ext-read: all[/]" : "[grey70]ext-read: ask[/]";

    public void SetMode(LakeCode.Core.Agent.AgentMode mode) =>
        _mode.Text = mode switch
        {
            LakeCode.Core.Agent.AgentMode.Plan => "[cyan]plan[/]",
            LakeCode.Core.Agent.AgentMode.Research => "[cyan]research[/]",
            _ => "[cyan]build[/]"
        };

    public FooterComponent(ConsoleWindowSystem windowSystem, ModelStore store, StatisticsStore statistics, string directory)
    {
        BorderStyle = BorderStyle.None;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(3, 0, 3, 1);
        var row = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var animationColumn = new ColumnContainer(row) { FlexFactor = 0, Width = 9 };
        animationColumn.AddContent(_animation);
        var modelColumn = new ColumnContainer(row) { FlexFactor = 0 };
        modelColumn.AddContent(_model);
        row.AddColumn(animationColumn);
        row.AddColumn(modelColumn);
        var detailsColumn = new ColumnContainer(row) { FlexFactor = 1 };
        _details.HorizontalAlignment = HorizontalAlignment.Stretch;
        detailsColumn.AddContent(_details);
        var modeColumn = new ColumnContainer(row) { FlexFactor = 0, Width = 9 };
        modeColumn.AddContent(_mode);
        var permissionsColumn = new ColumnContainer(row) { FlexFactor = 0, Width = 15 };
        permissionsColumn.AddContent(_permissions);
        row.AddColumn(detailsColumn);
        row.AddColumn(permissionsColumn);
        var separatorColumn = new ColumnContainer(row) { FlexFactor = 0, Width = 5 };
        separatorColumn.AddContent(new MarkupControl(["  [grey50]│[/]  "]) { Height = 1 });
        row.AddColumn(separatorColumn);
        row.AddColumn(modeColumn);
        AddControl(row);
        AddControl(_debug);
        string RenderModel() => Markup.Escape(store.HasSelection
            ? store.SelectedProviderName + " · " + store.SelectedModel.Name
            : "Нет подключения — /providers");
        string Render() =>
            $"   [grey50]│[/]   [cyan]{Markup.Escape(store.SelectedReasoningEffort)}[/]   [grey50]│[/]   {Markup.Escape(directory)}   [grey50]│[/]   {FormatStatistics(statistics)}   [grey50]│[/]   {statistics.TokensPerSecond?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} tok/s";
        _model.Click += (_, _) => ModelsRequested?.Invoke();

        _scanner = new SpinnerTextAnimator(windowSystem, ScannerFrames, value =>
        {
            _animation.Text = value.Length == 0 ? IdleFrame : value;
        }, 50);
        _scanner.Visible = false;
        _scanner.Start();
        _model.Bind(store, state => state.SelectedProviderName, control => control.Text, _ => RenderModel());
        _model.Bind(store, state => state.SelectedModel, control => control.Text, _ => RenderModel());
        _debug.Bind(statistics, state => state.Current, control => control.Text, _ => FormatRawStatistics(statistics));
        _details.Bind(store, state => state.SelectedReasoningEffort, control => control.Text, _ => Render());
        _details.Bind(statistics, state => state.Model, control => control.Text, _ => Render());
        _details.Bind(statistics, state => state.Current, control => control.Text, _ => Render());
        _details.Bind(statistics, state => state.TokensPerSecond, control => control.Text, _ => Render());
        _details.Bind(statistics, state => state.IsGenerating, control => control.Text, active =>
        {
            _scanner.Visible = active;
            return Render();
        });
    }

    public void ToggleDebug()
    {
        _debug.Visible = !_debug.Visible;
    }

    public void StopAnimation() => _scanner.Dispose();

    // OpenCode's bidirectional scanner: eight blocks, six-step trail and endpoint holds.
    // Reference: https://github.com/anomalyco/opencode/blob/v2/packages/tui/src/ui/spinner.ts
    private static string[] CreateScannerFrames()
    {
        const int width = 8;
        const int holdEnd = 9;
        const int holdStart = 30;
        const int trailLength = 6;
        var frames = new string[width + holdEnd + width - 1 + holdStart];
        var bright = new SharpConsoleUI.Color(0, 255, 255);
        var builder = new StringBuilder();
        for (var frame = 0; frame < frames.Length; frame++)
        {
            var forward = frame < width + holdEnd;
            var holding = forward ? frame >= width : frame >= width + holdEnd + width - 1;
            var progress = forward ? frame : frame - width - holdEnd;
            var holdProgress = forward ? frame - width : progress - (width - 1);
            var position = holding ? (forward ? width - 1 : 0) : (forward ? progress : width - 2 - progress);
            var fade = holding
                ? 1.0 - (double)holdProgress / (forward ? holdEnd : holdStart)
                : (double)progress / (forward ? width - 1 : width - 2);
            builder.Clear();
            for (var column = 0; column < width; column++)
            {
                var distance = forward ? position - column : column - position;
                var index = holding ? distance + holdProgress : distance;
                var active = index >= 0 && index < trailLength;
                var alpha = active
                    ? index == 0 ? 1.0 : index == 1 ? 0.9 : Math.Pow(0.65, index - 1)
                    : 0.2 * fade;
                builder.Append('[').Append(bright.WithAlpha((byte)Math.Round(255 * alpha)).ToMarkup())
                    .Append(']').Append(active ? '■' : '⬝').Append("[/]");
            }
            frames[frame] = builder.ToString();
        }
        return frames;
    }

    private static string FormatStatistics(StatisticsStore store)
    {
        if (store.Current?.InputTokens is not { } input)
            return $"0/{FormatTokens(store.Model.ContextWindowTokens)}";

        return $"{FormatTokens(input)}/{FormatTokens(store.Model.ContextWindowTokens)}";
    }

    private static string FormatRawStatistics(StatisticsStore store)
    {
        static string Value(long? tokens) => tokens?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var usage = store.Current;
        return $"[grey70]usage (MAF): input={Value(usage?.InputTokens)} cached={Value(usage?.CachedInputTokens)} output={Value(usage?.OutputTokens)} reasoning={Value(usage?.ReasoningTokens)} total={Value(usage?.TotalTokens)}[/]";
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
            return (tokens / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (tokens >= 1_000)
            return (tokens / 1_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "K";

        return tokens.ToString(CultureInfo.InvariantCulture);
    }
}

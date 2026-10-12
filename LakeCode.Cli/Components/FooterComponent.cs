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

internal sealed class FooterComponent : MarkupControl
{
    private static readonly string IdleFrame = $"[{new SharpConsoleUI.Color(0, 255, 255, 51).ToMarkup()}]⬝⬝⬝⬝⬝⬝⬝⬝[/]";
    private static readonly string[] ScannerFrames = CreateScannerFrames();
    private readonly SpinnerTextAnimator _scanner;
    public FooterComponent(ConsoleWindowSystem windowSystem, ModelStore store, StatisticsStore statistics, string directory)
        : base(new List<string>())
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(3, 0, 3, 1);
        var frame = IdleFrame;
        string Render() =>
            $"{frame} [cyan]{Markup.Escape(store.SelectedModel.Name)}[/]   [grey50]│[/]   [cyan]{Markup.Escape(store.SelectedReasoningEffort)}[/]   [grey50]│[/]   {Markup.Escape(directory)}   [grey50]│[/]   {FormatStatistics(statistics)}   [grey50]│[/]   {statistics.TokensPerSecond?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} tok/s\n" +
            FormatRawStatistics(statistics);

        _scanner = new SpinnerTextAnimator(windowSystem, ScannerFrames, value =>
        {
            frame = value.Length == 0 ? IdleFrame : value;
            Text = Render();
        }, 50);
        _scanner.Visible = false;
        _scanner.Start();
        this.Bind(store, state => state.SelectedModel, control => control.Text, _ => Render());
        this.Bind(store, state => state.SelectedReasoningEffort, control => control.Text, _ => Render());
        this.Bind(statistics, state => state.Current, control => control.Text, _ => Render());
        this.Bind(statistics, state => state.TokensPerSecond, control => control.Text, _ => Render());
        this.Bind(statistics, state => state.IsGenerating, control => control.Text, active =>
        {
            _scanner.Visible = active;
            return Render();
        });
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

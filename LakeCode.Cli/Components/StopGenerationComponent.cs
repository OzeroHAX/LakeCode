using System.ComponentModel;
using System.Diagnostics;
using LakeCode.Cli.State;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed class StopGenerationComponent : MarkupControl
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly StatisticsStore _statistics;
    private readonly Func<bool> _canStop;
    private readonly Action _stop;
    private Window? _window;
    private CancellationTokenSource? _confirmation;
    private long _started;
    private bool _disposed;

    public StopGenerationComponent(ConsoleWindowSystem windowSystem, StatisticsStore statistics,
        Func<bool> canStop, Action stop) : base(new List<string>())
    {
        _windowSystem = windowSystem;
        _statistics = statistics;
        _canStop = canStop;
        _stop = stop;
        Text = "[yellow]Нажмите Esc ещё раз в течение 3 секунд, чтобы остановить генерацию.[/]";
        Wrap = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(2, 0, 2, 0);
        Visible = false;
        _statistics.PropertyChanged += OnStatisticsChanged;
    }

    public void Attach(Window window)
    {
        _window = window;
        window.PreviewKeyPressed += OnPreviewKeyPressed;
    }

    private void OnPreviewKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Key != ConsoleKey.Escape || e.KeyInfo.Modifiers != 0 || !_canStop())
            return;

        e.Handled = true;
        if (_confirmation is not null && Stopwatch.GetElapsedTime(_started) < TimeSpan.FromSeconds(3))
        {
            ClearConfirmation();
            _stop();
            return;
        }

        ClearConfirmation();
        var confirmation = new CancellationTokenSource();
        _confirmation = confirmation;
        _started = Stopwatch.GetTimestamp();
        Visible = true;
        _ = ExpireAsync(confirmation);
    }

    private async Task ExpireAsync(CancellationTokenSource confirmation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), confirmation.Token);
            _windowSystem.EnqueueOnUIThread(() =>
            {
                if (!_disposed && _confirmation == confirmation) ClearConfirmation();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void OnStatisticsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatisticsStore.IsGenerating) && !_statistics.IsGenerating)
            ClearConfirmation();
    }

    private void ClearConfirmation()
    {
        var confirmation = _confirmation;
        _confirmation = null;
        confirmation?.Cancel();
        confirmation?.Dispose();
        Visible = false;
    }

    protected override void OnDisposing()
    {
        _disposed = true;
        ClearConfirmation();
        _statistics.PropertyChanged -= OnStatisticsChanged;
        if (_window is not null) _window.PreviewKeyPressed -= OnPreviewKeyPressed;
        base.OnDisposing();
    }
}

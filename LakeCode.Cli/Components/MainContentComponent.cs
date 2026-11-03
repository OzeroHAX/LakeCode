using LakeCode.Core.Conversations;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace LakeCode.Cli.Components;

internal sealed record VirtualizedMessageRow(Func<IWindowControl> Factory, int EstimatedHeight = 8);

internal sealed class MainContentComponent : ScrollablePanelControl
{
    private const int DefaultWindowRows = 8;
    private const int BackfillRows = 4;
    private const int EstimatedRowHeight = 8;
    private bool _rebuildingVirtualWindow;
    private bool _streaming;
    private IReadOnlyList<VirtualizedMessageRow>? _virtualRows;
    private Action? _beforeVirtualWindowBuild;
    private int _virtualStart;
    private int _virtualEnd;
    public MainContentComponent()
    {
        ShowScrollbar = true;
        ScrollbarColor = new Color(50, 50, 50);
        ScrollbarThumbColor = new Color(105, 105, 105);
        AutoScroll = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Fill;
        Margin = new Margin(1, 0, 1, 0);
        VerticalScrollOffsetChanged += OnVerticalScrollOffsetChanged;
    }

    public void AddUserMessage(string message, IReadOnlyList<ImageAttachment> attachments,
        IReadOnlyList<LakeCode.Core.Attachments.FileAttachment>? files = null,
        IReadOnlyList<LakeCode.Core.Attachments.FileMention>? mentions = null)
    {
        AddControl(new UserMessageComponent(message, attachments, files, mentions));
    }

    public void SetVirtualizedRows(IReadOnlyList<VirtualizedMessageRow> rows, Action? beforeWindowBuild = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _virtualRows = rows;
        _beforeVirtualWindowBuild = beforeWindowBuild;
        _virtualStart = Math.Max(0, rows.Count - DefaultWindowRows);
        _virtualEnd = rows.Count;
        RebuildVirtualWindow(scrollToBottom: true);
    }

    public void ClearVirtualizedTurns()
    {
        _virtualRows = null;
        _beforeVirtualWindowBuild = null;
        _virtualStart = 0;
        _virtualEnd = 0;
    }

    public void BeginStreaming() => _streaming = true;

    public void EndStreaming() => _streaming = false;

    private void OnVerticalScrollOffsetChanged(object? sender, ScrollOffsetChangedEventArgs args)
    {
        if (_streaming || _rebuildingVirtualWindow || _virtualRows is null || _virtualRows.Count == 0)
            return;

        int viewport = Math.Max(1, ViewportHeight);
        bool nearTop = args.Offset <= Math.Max(2, viewport / 2);
        bool nearBottom = args.Offset + viewport >= Math.Max(0, TotalContentHeight - Math.Max(2, viewport / 2));

        if (nearTop && _virtualStart > 0)
        {
            _virtualStart = Math.Max(0, _virtualStart - BackfillRows);
            RebuildVirtualWindow(scrollToBottom: false);
        }
        else if (nearBottom)
        {
            int tailStart = Math.Max(0, _virtualRows.Count - DefaultWindowRows);
            if (_virtualStart < tailStart)
            {
                _virtualStart = Math.Min(tailStart, _virtualStart + BackfillRows);
                _virtualEnd = _virtualRows.Count;
                RebuildVirtualWindow(scrollToBottom: false);
            }
        }
    }

    private void RebuildVirtualWindow(bool scrollToBottom)
    {
        if (_virtualRows is null || _rebuildingVirtualWindow)
            return;

        _rebuildingVirtualWindow = true;
        try
        {
            _beforeVirtualWindowBuild?.Invoke();
            ((IControlHost)this).ClearControls();

            int topHeight = _virtualRows.Take(_virtualStart).Sum(row => row.EstimatedHeight);
            int bottomHeight = _virtualRows.Skip(_virtualEnd).Sum(row => row.EstimatedHeight);
            if (topHeight > 0)
                AddSpacer(topHeight);

            for (int i = _virtualStart; i < _virtualEnd; i++)
                AddControl(_virtualRows[i].Factory());

            if (bottomHeight > 0)
                AddSpacer(bottomHeight);

            if (scrollToBottom)
                ScrollToBottom();
            else
                Invalidate(Invalidation.Relayout);
        }
        finally
        {
            _rebuildingVirtualWindow = false;
        }
    }

    private void AddSpacer(int height)
    {
        AddControl(new MarkupControl([])
        {
            Height = Math.Max(1, height),
            Wrap = false,
            EnableSelection = false
        });
    }
}


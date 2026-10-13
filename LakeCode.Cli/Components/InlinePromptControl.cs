using System.Collections.Immutable;
using System.Globalization;
using System.Drawing;
using LakeCode.Core.Attachments;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Color = SharpConsoleUI.Color;

namespace LakeCode.Cli.Components;

/// <summary>Standard PromptControl rendering, with application-owned atomic file ranges.
/// Reimplements input interfaces because PromptControl's edit methods and caret are private.</summary>
internal sealed class InlinePromptControl : PromptControl, IInteractiveControl, IPasteTarget, IMouseAwareControl, ILogicalCursorProvider
{
    private readonly List<FileMention> _mentions = [];
    private int _caret, _anchor = -1;
    private int _wrapWidth = 1;
    private int _scrollTop;
    private bool _dragging;

    public InlinePromptControl()
    {
        PropertyChanged += (_, _) =>
        {
            if (!HasFocus || !IsEnabled) _dragging = false;
        };
    }

    public int Caret => _caret;
    public ImmutableArray<FileMention> Mentions => _mentions.ToImmutableArray();
    public string[] FilePaths => _mentions.OrderBy(item => item.Start).Select(item => item.Path).Distinct(StringComparer.Ordinal).ToArray();
    public new string Input { get => base.Input; set => SetInput(value); }
    public new bool WantsTabKey => false;

    public new void SetInput(string? text)
    {
        _dragging = false;
        _mentions.Clear();
        _caret = (text ?? "").Length;
        _anchor = -1;
        base.SetInput(text);
    }

    public void Restore(string text, IEnumerable<FileMention> mentions)
    {
        _dragging = false;
        _mentions.Clear();
        _mentions.AddRange(mentions.OrderBy(item => item.Start));
        _caret = text.Length;
        _anchor = -1;
        base.SetInput(text);
    }

    public void InsertFile(int start, int length, string path)
    {
        var label = FileMention.Label(path);
        Replace(start, length, label, path);
    }

    public new void Paste(string text)
    {
        if (!IsEnabled || ReadOnly || string.IsNullOrEmpty(text)) return;
        ReplaceSelection(text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    private int Snap(int position, bool right)
    {
        position = Math.Clamp(position, 0, Input.Length);
        foreach (var mention in _mentions)
            if (position > mention.Start && position < mention.Start + mention.Length)
                return right ? mention.Start + mention.Length : mention.Start;
        return position;
    }

    private void ReplaceSelection(string text)
    {
        var start = _anchor < 0 ? _caret : Math.Min(_anchor, _caret);
        var end = _anchor < 0 ? _caret : Math.Max(_anchor, _caret);
        Replace(start, end - start, text);
    }

    private void Replace(int start, int length, string text, string? file = null)
    {
        var end = Snap(start + length, true);
        start = Snap(start, false);
        length = end - start;
        var value = Input.Remove(start, length).Insert(start, text);
        _mentions.RemoveAll(item => item.Start < end && item.Start + item.Length > start);
        for (var i = 0; i < _mentions.Count; i++)
            if (_mentions[i].Start >= end) _mentions[i] = _mentions[i] with { Start = _mentions[i].Start + text.Length - length };
        if (file is not null) _mentions.Add(new FileMention(start, text.Length, file));
        _mentions.Sort((left, right) => left.Start.CompareTo(right.Start));
        _caret = start + text.Length;
        _anchor = -1;
        Sync(value);
    }

    private void Sync(string? value = null)
    {
        if (value is not null) base.SetInput(value);
        // PromptControl exposes no text-offset setter. Its horizontal keys move by UTF-16
        // offsets, independently of wrapping and scroll, unlike SetLogicalCursorPosition.
        var start = _anchor >= 0 ? _anchor : _caret;
        var fromEnd = start > Input.Length / 2;
        base.ProcessKey(Key(fromEnd ? ConsoleKey.End : ConsoleKey.Home, control: true));
        var steps = fromEnd ? Input.Length - start : start;
        var direction = fromEnd ? ConsoleKey.LeftArrow : ConsoleKey.RightArrow;
        for (var i = 0; i < steps; i++) base.ProcessKey(Key(direction));
        if (_anchor >= 0)
        {
            direction = _caret >= _anchor ? ConsoleKey.RightArrow : ConsoleKey.LeftArrow;
            for (var i = 0; i < Math.Abs(_caret - _anchor); i++) base.ProcessKey(Key(direction, shift: true));
        }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool control = false) =>
        new('\0', key, shift, false, control);

    private void Move(int position, bool extend)
    {
        if (extend) { if (_anchor < 0) _anchor = _caret; }
        else _anchor = -1;
        _caret = Snap(position, position >= _caret);
        Sync();
    }

    private int Previous(int position)
    {
        if (position <= 0) return 0;
        var indices = StringInfo.ParseCombiningCharacters(Input);
        return Snap(indices.LastOrDefault(index => index < position), false);
    }

    private int Next(int position)
    {
        var next = StringInfo.ParseCombiningCharacters(Input).FirstOrDefault(index => index > position, Input.Length);
        return Snap(next, true);
    }

    private int Word(int position, bool right)
    {
        if (right)
        {
            while (position < Input.Length && !char.IsWhiteSpace(Input[position])) position = Next(position);
            while (position < Input.Length && char.IsWhiteSpace(Input[position])) position = Next(position);
        }
        else
        {
            while (position > 0 && char.IsWhiteSpace(Input[position - 1])) position = Previous(position);
            while (position > 0 && !char.IsWhiteSpace(Input[position - 1])) position = Previous(position);
        }
        return position;
    }

    public new bool ProcessKey(ConsoleKeyInfo key)
    {
        _dragging = false;
        if (!IsEnabled) return false;
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (ctrl && key.Key == ConsoleKey.A) { _anchor = 0; _caret = Input.Length; Sync(); return true; }
        if (ctrl && key.Key is ConsoleKey.C or ConsoleKey.X)
        {
            if (_anchor >= 0 && _anchor != _caret)
            {
                ClipboardHelper.SetText(Input.Substring(Math.Min(_anchor, _caret), Math.Abs(_anchor - _caret)));
                if (key.Key == ConsoleKey.X && !ReadOnly) ReplaceSelection("");
            }
            return true;
        }
        if (key.Key == ConsoleKey.LeftArrow) { Move(ctrl ? Word(_caret, false) : Previous(_caret), shift); return true; }
        if (key.Key == ConsoleKey.RightArrow) { Move(ctrl ? Word(_caret, true) : Next(_caret), shift); return true; }
        if (key.Key is ConsoleKey.Home or ConsoleKey.End || ctrl && key.Key == ConsoleKey.E)
        {
            var end = key.Key is ConsoleKey.End or ConsoleKey.E;
            var rows = Rows();
            var row = rows[RowIndex(rows, _caret)];
            Move(ctrl ? end ? Input.Length : 0 : end ? row.Offset + row.Length : row.Offset, shift);
            return true;
        }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            var rows = Rows();
            var current = RowIndex(rows, _caret);
            var target = Math.Clamp(current + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, rows.Count - 1);
            var column = UnicodeWidth.CharOffsetToColumn(Input.Substring(rows[current].Offset, rows[current].Length), _caret - rows[current].Offset);
            Move(rows[target].Offset + UnicodeWidth.ColumnToCharOffset(Input.Substring(rows[target].Offset, rows[target].Length), column), shift);
            return true;
        }
        if (ReadOnly) return key.Key != ConsoleKey.Tab;
        if (ctrl && key.Key is ConsoleKey.K or ConsoleKey.U or ConsoleKey.W)
        {
            var start = key.Key == ConsoleKey.K ? _caret : key.Key == ConsoleKey.W ? Word(_caret, false) : Input[.._caret].LastIndexOf('\n') + 1;
            var end = key.Key == ConsoleKey.K ? Input.IndexOf('\n', _caret) : _caret;
            if (end < 0) end = Input.Length;
            Replace(start, end - start, "");
            return true;
        }
        if (key.Key is ConsoleKey.Backspace or ConsoleKey.Delete)
        {
            if (_anchor >= 0 && _anchor != _caret) ReplaceSelection("");
            else if (key.Key == ConsoleKey.Backspace) { var start = ctrl ? Word(_caret, false) : Previous(_caret); Replace(start, _caret - start, ""); }
            else { var end = ctrl ? Word(_caret, true) : Next(_caret); Replace(_caret, end - _caret, ""); }
            return true;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            if (alt || shift || ctrl) ReplaceSelection("\n");
            else base.ProcessKey(key); // Preserve Entered and the existing submission contract.
            return true;
        }
        if (ctrl && key.Key == ConsoleKey.L) { ReplaceSelection("\n"); return true; }
        if (!ctrl && !alt && !char.IsControl(key.KeyChar)) { ReplaceSelection(key.KeyChar.ToString()); return true; }
        return false;
    }

    private List<PromptTextWrapping.Segment> Rows()
    {
        var rows = new List<PromptTextWrapping.Segment>();
        var scratch = new List<PromptTextWrapping.Segment>();
        var start = 0;
        while (true)
        {
            var newline = Input.IndexOf('\n', start);
            var end = newline < 0 ? Input.Length : newline;
            scratch.Clear();
            PromptTextWrapping.WrapWords(scratch, Input[start..end], _wrapWidth);
            foreach (var row in scratch) rows.Add(new PromptTextWrapping.Segment(start + row.Offset, row.Length));
            if (newline < 0) break;
            start = newline + 1;
        }
        return rows;
    }

    private static int RowIndex(List<PromptTextWrapping.Segment> rows, int position)
    {
        for (var i = rows.Count - 1; i >= 0; i--) if (rows[i].Offset <= position) return i;
        return 0;
    }

    public override LayoutSize MeasureDOM(LayoutConstraints constraints)
    {
        var size = base.MeasureDOM(constraints);
        _wrapWidth = Math.Max(1, (Width ?? constraints.MaxWidth) - Margin.Left - Margin.Right - MarkupParser.StripLength(Prompt ?? ""));
        return size;
    }

    private int ScrollTop(List<PromptTextWrapping.Segment> rows)
    {
        // Sync positions the base caret by text offset, so its visible Y yields the scroll offset.
        if (base.GetLogicalCursorPosition() is { } point)
            _scrollTop = Math.Max(0, RowIndex(rows, _caret) - point.Y + Margin.Top);
        return Math.Clamp(_scrollTop, 0, Math.Max(0, rows.Count - 1));
    }

    public new void SetLogicalCursorPosition(Point position)
    {
        var rows = Rows();
        var row = rows[Math.Clamp(position.Y - Margin.Top + ScrollTop(rows), 0, rows.Count - 1)];
        var column = Math.Max(0, position.X - Margin.Left - MarkupParser.StripLength(Prompt ?? ""));
        Move(row.Offset + UnicodeWidth.ColumnToCharOffset(Input.Substring(row.Offset, row.Length), column), false);
    }

    public new bool ProcessMouseEvent(MouseEventArgs args)
    {
        if (!IsEnabled || args.HasFlag(MouseFlags.MouseLeave))
        {
            _dragging = false;
            return false;
        }
        if (args.HasFlag(MouseFlags.Button1Released))
        {
            _dragging = false;
            args.Handled = true;
            return true;
        }
        var motion = args.HasFlag(MouseFlags.ReportMousePosition) || args.HasFlag(MouseFlags.Button1Dragged);
        var held = args.HasFlag(MouseFlags.Button1Pressed) || args.HasFlag(MouseFlags.Button1Dragged);
        if (motion && (!held || !_dragging || !HasFocus))
        {
            _dragging = false;
            return false;
        }
        var extend = motion && _dragging && held;
        if (!extend && !args.HasFlag(MouseFlags.Button1Pressed) && !args.HasFlag(MouseFlags.Button1Clicked) &&
            !args.HasFlag(MouseFlags.Button1DoubleClicked)) return false;
        // Continuous press notifications are not a new click and must not reset the selection.
        if (!motion && _dragging && args.HasFlag(MouseFlags.Button1Pressed))
        {
            args.Handled = true;
            return true;
        }
        if (!HasFocus) this.GetParentWindow()?.FocusManager.SetFocus(this, FocusReason.Mouse);
        var rows = Rows();
        var row = rows[Math.Clamp(args.Position.Y - Margin.Top + ScrollTop(rows), 0, rows.Count - 1)];
        var column = Math.Max(0, args.Position.X - Margin.Left - MarkupParser.StripLength(Prompt ?? ""));
        var pos = row.Offset + UnicodeWidth.ColumnToCharOffset(Input.Substring(row.Offset, row.Length), column);
        var chip = _mentions.FirstOrDefault(item => pos >= item.Start && pos < item.Start + item.Length);
        if (args.HasFlag(MouseFlags.Button1DoubleClicked) && chip is not null)
        {
            _dragging = false;
            _anchor = chip.Start;
            _caret = chip.Start + chip.Length;
            Sync();
        }
        else
        {
            if (!extend)
            {
                _anchor = -1;
                _dragging = args.HasFlag(MouseFlags.Button1Pressed);
            }
            Move(pos, extend);
        }
        args.Handled = true;
        return true;
    }

    public override void PaintDOM(CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        base.PaintDOM(buffer, bounds, clipRect, defaultFg, defaultBg);
        _wrapWidth = Math.Max(1, bounds.Width - Margin.Left - Margin.Right - MarkupParser.StripLength(Prompt ?? ""));
        var rows = Rows();
        var top = ScrollTop(rows);
        for (var index = top; index < Math.Min(rows.Count, top + MaxRows); index++)
        {
            var row = rows[index];
            var y = bounds.Y + Margin.Top + index - top;
            if (y < clipRect.Y || y >= clipRect.Bottom || y >= bounds.Bottom) continue;
            foreach (var chip in _mentions)
            {
                var start = Math.Max(row.Offset, chip.Start);
                var end = Math.Min(row.Offset + row.Length, chip.Start + chip.Length);
                if (start >= end) continue;
                var x = bounds.X + Margin.Left + MarkupParser.StripLength(Prompt ?? "") + UnicodeWidth.GetStringWidth(Input[row.Offset..start]);
                var selected = _anchor >= 0 && start < Math.Max(_anchor, _caret) && end > Math.Min(_anchor, _caret);
                var cells = MarkupParser.Parse(MarkupParser.Escape(Input[start..end]), Color.White,
                    selected ? new Color(55, 95, 110) : new Color(35, 65, 75));
                for (var cell = 0; cell < cells.Count && x + cell < bounds.Right - Margin.Right; cell++)
                    if (x + cell >= clipRect.X && x + cell < clipRect.Right)
                        buffer.SetCell(x + cell, y, cells[cell]);
            }
        }
    }
}

using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

namespace AllInOneInstaller.Terminal;

/// <summary>Draws a <see cref="TerminalSession"/> and forwards keyboard and mouse input to it.</summary>
public sealed class TerminalControl : Control
{

    public static readonly Color Background = Color.Parse("#0B1220");
    private static readonly Color DefaultForeground = Color.Parse("#D5DCE8");
    private static readonly Color SelectionColor = Color.FromArgb(0x70, 0x4F, 0x5F, 0xE6);

    // The 16 ANSI colours (tuned for the dark background), then the xterm 6x6x6 cube and the grey ramp.
    private static readonly Color[] Palette = BuildPalette();

    private TerminalSession? _session;
    private double _fontSize = 14;
    private Typeface _regular, _bold;
    private double _cellWidth = 8, _cellHeight = 17, _baseline;
    private int _scrollOffset; // lines scrolled back from the bottom
    private (long Line, int Column)? _selectionStart, _selectionEnd;
    private bool _selecting;
    private bool _redrawQueued;
    private readonly Dictionary<(int, bool, bool), IBrush> _brushes = [];

    /// <summary>Raised with the new size in characters, after the session has been resized.</summary>
    public event Action<int, int>? TerminalResized;
    /// <summary>A short status message for the page, such as after a copy.</summary>
    public event Action<string>? Notice;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _regular = new Typeface(App.Professional.ProfessionalTheme.Mono);
        _bold = new Typeface(App.Professional.ProfessionalTheme.Mono, FontStyle.Normal, FontWeight.Bold);
        MeasureCell();

        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
        copy.Click += async (_, _) => await CopyAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
        paste.Click += async (_, _) => await PasteAsync();
        var all = new MenuItem { Header = "Select all" };
        all.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "Clear scrollback" };
        clear.Click += (_, _) => ClearScrollback();
        ContextMenu = new ContextMenu { Items = { copy, paste, new Separator(), all, clear } };
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) _session.Output -= QueueRedraw;
            _session = value;
            _scrollOffset = 0;
            _selectionStart = _selectionEnd = null;
            if (_session is not null)
            {
                _session.Output += QueueRedraw;
                FitToBounds();
            }
            InvalidateVisual();
        }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, 9, 28);
            MeasureCell();
            FitToBounds();
            InvalidateVisual();
        }
    }

    public int Columns => Math.Max(20, (int)(Bounds.Width / _cellWidth));
    public int Rows => Math.Max(5, (int)(Bounds.Height / _cellHeight));

    private void MeasureCell()
    {
        var sample = new FormattedText(new string('M', 40), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, _fontSize, Brushes.White);
        _cellWidth = sample.WidthIncludingTrailingWhitespace / 40;
        _cellHeight = Math.Ceiling(sample.Height);
        _baseline = sample.Baseline;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitToBounds();
    }

    private void FitToBounds()
    {
        if (_session is null || Bounds.Width < 10 || Bounds.Height < 10) return;
        int columns = Columns, rows = Rows;
        if (columns == _session.Emulator.Columns && rows == _session.Emulator.Rows) return;
        _session.Resize(columns, rows);
        _scrollOffset = 0;
        TerminalResized?.Invoke(columns, rows);
        InvalidateVisual();
    }

    private void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private static Color[] BuildPalette()
    {
        string[] ansi =
        [
            "#1B2232", "#E5534B", "#57AB5A", "#C69026", "#539BF5", "#B083F0", "#39C5CF", "#C9D1D9",
            "#636E7B", "#FF7B72", "#7EE787", "#E3B341", "#79C0FF", "#D2A8FF", "#56D4DD", "#F0F6FC"
        ];
        var colors = new Color[256];
        for (int i = 0; i < 16; i++) colors[i] = Color.Parse(ansi[i]);
        int[] levels = [0, 95, 135, 175, 215, 255];
        for (int i = 0; i < 216; i++) colors[16 + i] = Color.FromRgb((byte)levels[i / 36], (byte)levels[i / 6 % 6], (byte)levels[i % 6]);
        for (int i = 0; i < 24; i++) { byte v = (byte)(8 + i * 10); colors[232 + i] = Color.FromRgb(v, v, v); }
        return colors;
    }

    private static Color ToColor(int value, bool foreground) => value switch
    {
        TerminalCell.DefaultColor => foreground ? DefaultForeground : Background,
        >= TerminalCell.Rgb => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value),
        _ => Palette[Math.Clamp(value, 0, 255)]
    };

    private IBrush Brush(int color, bool foreground, bool dim)
    {
        var key = (color, foreground, dim);
        if (_brushes.TryGetValue(key, out IBrush? brush)) return brush;
        Color c = ToColor(color, foreground);
        if (dim) c = Color.FromArgb(0xB0, c.R, c.G, c.B);
        if (_brushes.Count > 4096) _brushes.Clear();
        return _brushes[key] = new SolidColorBrush(c).ToImmutable();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Background), new Rect(Bounds.Size));
        if (_session is null) return;

        lock (_session.Emulator.Sync)
        {
            TerminalEmulator term = _session.Emulator;
            int scrollback = term.Scrollback.Count;
            _scrollOffset = Math.Clamp(_scrollOffset, 0, term.AlternateScreen ? 0 : scrollback);
            int firstLine = scrollback - _scrollOffset; // absolute index of the top row
            var (selFrom, selTo) = OrderedSelection();

            for (int row = 0; row < term.Rows; row++)
            {
                TerminalLine? line = term.AlternateScreen ? term.Line(row) : term.AbsoluteLine(firstLine + row);
                if (line is null) continue;
                long absolute = term.ScrollbackDropped + firstLine + row;
                DrawLine(context, line, row, absolute, selFrom, selTo);
            }

            if (_scrollOffset == 0 && term.CursorVisible && !_session.IsClosed)
            {
                var cell = new Rect(term.CursorX * _cellWidth, term.CursorY * _cellHeight, _cellWidth, _cellHeight);
                if (IsFocused) context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0xD5, 0xDC, 0xE8)), cell);
                else context.DrawRectangle(new Pen(new SolidColorBrush(DefaultForeground), 1), cell.Deflate(0.5));
            }
            if (_scrollOffset > 0)
            {
                var label = new FormattedText($" ↑ {_scrollOffset} lines back (type or scroll down to return) ", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _regular, Math.Max(10, _fontSize - 2), Brushes.Black);
                var box = new Rect(Bounds.Width - label.Width - 8, 4, label.Width, label.Height);
                context.FillRectangle(new SolidColorBrush(Color.Parse("#E3B341")), box);
                context.DrawText(label, box.TopLeft);
            }
        }
    }

    private void DrawLine(DrawingContext context, TerminalLine line, int row, long absolute,
        (long Line, int Column)? selFrom, (long Line, int Column)? selTo)
    {
        TerminalCell[] cells = line.Cells;
        double y = row * _cellHeight;
        int count = Math.Min(cells.Length, Columns + 1);
        var text = new StringBuilder();

        int x = 0;
        while (x < count)
        {
            TerminalCell first = cells[x];
            int fg = first.Foreground, bg = first.Background;
            CellFlags flags = first.Flags & ~(CellFlags.Wide | CellFlags.WideTail);
            if ((flags & CellFlags.Inverse) != 0) (fg, bg) = (bg == TerminalCell.DefaultColor ? -2 : bg, fg == TerminalCell.DefaultColor ? -3 : fg);
            int start = x;
            text.Clear();
            // Cells with the same colours and style form one run. A wide character always ends a run to keep the grid.
            while (x < count)
            {
                TerminalCell c = cells[x];
                if ((c.Flags & ~(CellFlags.Wide | CellFlags.WideTail)) != (first.Flags & ~(CellFlags.Wide | CellFlags.WideTail))
                    || c.Foreground != first.Foreground || c.Background != first.Background) break;
                if ((c.Flags & CellFlags.WideTail) != 0) { x++; if (text.Length > 0) break; continue; }
                // Non-ASCII characters often come from a fallback font with a different width. Drawing each in its
                // own cell keeps it from pushing the rest of the run off the grid.
                bool single = c.Rune > 0x7e || (c.Flags & CellFlags.Wide) != 0;
                if (single && text.Length > 0) break;
                text.Append(c.Rune < 0x10000 ? ((char)c.Rune).ToString() : char.ConvertFromUtf32(c.Rune));
                x++;
                if ((c.Flags & CellFlags.Wide) != 0 && x < count && (cells[x].Flags & CellFlags.WideTail) != 0) x++;
                if (single) break;
            }
            if (x == start) { x++; continue; }

            var rect = new Rect(start * _cellWidth, y, (x - start) * _cellWidth, _cellHeight);
            if (bg != TerminalCell.DefaultColor)
                context.FillRectangle(bg == -3 ? new SolidColorBrush(DefaultForeground) : Brush(bg, false, false), rect);

            if ((flags & CellFlags.Hidden) == 0 && text.ToString().Trim().Length > 0)
            {
                IBrush brush = fg == -2 ? new SolidColorBrush(Background) : Brush(fg, true, (flags & CellFlags.Dim) != 0);
                var formatted = new FormattedText(text.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    (flags & CellFlags.Bold) != 0 ? _bold : _regular, _fontSize, brush);
                if ((flags & CellFlags.Italic) != 0) formatted.SetFontStyle(FontStyle.Italic);
                // Keep every character on the grid even when a fallback font is a little wider or narrower.
                double natural = formatted.WidthIncludingTrailingWhitespace;
                if (natural > 0 && Math.Abs(natural - rect.Width) > 0.5)
                {
                    using (context.PushTransform(Matrix.CreateScale(rect.Width / natural, 1) * Matrix.CreateTranslation(rect.X, y)))
                        context.DrawText(formatted, default);
                }
                else context.DrawText(formatted, rect.TopLeft);
            }
            if ((flags & (CellFlags.Underline | CellFlags.Strike)) != 0)
            {
                var pen = new Pen(fg == -2 ? new SolidColorBrush(Background) : Brush(fg, true, false), 1);
                if ((flags & CellFlags.Underline) != 0) context.DrawLine(pen, new Point(rect.X, y + _baseline + 2.5), new Point(rect.Right, y + _baseline + 2.5));
                if ((flags & CellFlags.Strike) != 0) context.DrawLine(pen, new Point(rect.X, y + _cellHeight / 2), new Point(rect.Right, y + _cellHeight / 2));
            }
        }

        if (selFrom is { } from && selTo is { } to && absolute >= from.Line && absolute <= to.Line)
        {
            int a = absolute == from.Line ? from.Column : 0;
            int b = absolute == to.Line ? to.Column : cells.Length - 1;
            if (b >= a) context.FillRectangle(new SolidColorBrush(SelectionColor), new Rect(a * _cellWidth, y, (b - a + 1) * _cellWidth, _cellHeight));
        }
    }

    private ((long Line, int Column)?, (long Line, int Column)?) OrderedSelection()
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b) return (null, null);
        return a.Line < b.Line || a.Line == b.Line && a.Column <= b.Column ? (a, b) : (b, a);
    }

    private (long Line, int Column) PositionAt(Point point)
    {
        TerminalEmulator term = _session!.Emulator;
        int row = Math.Clamp((int)(point.Y / _cellHeight), 0, term.Rows - 1);
        int column = Math.Clamp((int)(point.X / _cellWidth), 0, term.Columns - 1);
        int top = term.AlternateScreen ? term.Scrollback.Count : term.Scrollback.Count - _scrollOffset;
        return (term.ScrollbackDropped + top + row, column);
    }

    private string SelectedText()
    {
        if (_session is null) return "";
        lock (_session.Emulator.Sync)
        {
            TerminalEmulator term = _session.Emulator;
            var (from, to) = OrderedSelection();
            if (from is not { } f || to is not { } t) return "";
            // Selection lines are absolute. In the alternate screen the rows come right after the scrollback.
            int Index(long line) => (int)(line - term.ScrollbackDropped);
            return term.AlternateScreen
                ? AlternateText(term, Index(f.Line) - term.Scrollback.Count, f.Column, Index(t.Line) - term.Scrollback.Count, t.Column)
                : term.Text((Index(f.Line), f.Column), (Index(t.Line), t.Column));
        }
    }

    private static string AlternateText(TerminalEmulator term, int fromRow, int fromColumn, int toRow, int toColumn)
    {
        var text = new StringBuilder();
        for (int row = Math.Max(0, fromRow); row <= Math.Min(term.Rows - 1, toRow); row++)
        {
            TerminalCell[] cells = term.Line(row).Cells;
            int a = row == fromRow ? fromColumn : 0, b = row == toRow ? toColumn : cells.Length - 1;
            var part = new StringBuilder();
            for (int x = a; x <= Math.Min(b, cells.Length - 1); x++)
                if ((cells[x].Flags & CellFlags.WideTail) == 0) part.Append(char.ConvertFromUtf32(cells[x].Rune));
            text.Append(part.ToString().TrimEnd());
            if (row != toRow) text.Append('\n');
        }
        return text.ToString();
    }

    private void SelectAll()
    {
        if (_session is null) return;
        lock (_session.Emulator.Sync)
        {
            TerminalEmulator term = _session.Emulator;
            long first = term.ScrollbackDropped + (term.AlternateScreen ? term.Scrollback.Count : 0);
            _selectionStart = (first, 0);
            _selectionEnd = (term.ScrollbackDropped + term.Scrollback.Count + term.Rows - 1, term.Columns - 1);
        }
        InvalidateVisual();
    }

    private void ClearScrollback()
    {
        if (_session is null) return;
        lock (_session.Emulator.Sync) _session.Emulator.Feed("\x1b[3J"u8);
        _scrollOffset = 0;
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    private void SelectWord(Point point)
    {
        var (line, column) = PositionAt(point);
        lock (_session!.Emulator.Sync)
        {
            TerminalEmulator term = _session.Emulator;
            int index = (int)(line - term.ScrollbackDropped);
            TerminalLine? row = term.AlternateScreen ? term.Line(index - term.Scrollback.Count) : term.AbsoluteLine(index);
            if (row is null) return;
            static bool Word(int r) => r > ' ' && !"\"'`()[]{}<>,;|".Contains((char)Math.Min(r, 0xffff));
            if (!Word(row.Cells[column].Rune)) return;
            int a = column, b = column;
            while (a > 0 && Word(row.Cells[a - 1].Rune)) a--;
            while (b < row.Cells.Length - 1 && Word(row.Cells[b + 1].Rune)) b++;
            _selectionStart = (line, a);
            _selectionEnd = (line, b);
        }
        InvalidateVisual();
    }

    public async Task CopyAsync()
    {
        string text = SelectedText();
        if (text.Length == 0) { Notice?.Invoke("Select text with the mouse first."); return; }
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            Notice?.Invoke($"Copied {text.Length} characters.");
        }
    }

    public async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        // Terminals send Enter as CR. Bracketed paste tells the shell the text was pasted, so it does not run it line by line.
        text = text.ReplaceLineEndings("\r");
        bool bracketed;
        lock (_session.Emulator.Sync) bracketed = _session.Emulator.BracketedPaste;
        SendInput(bracketed ? "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~" : text);
        Focus();
    }

    private void SendInput(string text)
    {
        if (_session is null || text.Length == 0) return;
        if (_scrollOffset != 0 || _selectionStart is not null)
        {
            _scrollOffset = 0;
            _selectionStart = _selectionEnd = null;
            InvalidateVisual();
        }
        _session.Send(text);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;
        KeyModifiers mods = e.KeyModifiers;
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0, alt = (mods & KeyModifiers.Alt) != 0;

        if (ctrl && shift && e.Key == Key.C) { e.Handled = true; await CopyAsync(); return; }
        if (ctrl && shift && e.Key == Key.V || shift && e.Key == Key.Insert) { e.Handled = true; await PasteAsync(); return; }
        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            ScrollView(e.Key == Key.PageUp ? Rows - 1 : -(Rows - 1));
            return;
        }
        if (ctrl && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; TerminalFontSize += 1; return; }
        if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; TerminalFontSize -= 1; return; }

        bool app;
        lock (_session.Emulator.Sync) app = _session.Emulator.ApplicationCursorKeys;
        string? sequence = KeySequence(e.Key, ctrl, shift, alt, app);
        if (sequence is not null)
        {
            e.Handled = true;
            SendInput(sequence);
        }
    }

    // xterm's modifier parameter: 1 + shift + 2*alt + 4*ctrl.
    private static string? KeySequence(Key key, bool ctrl, bool shift, bool alt, bool applicationCursor)
    {
        int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int code) => modifier > 1 ? $"\x1b[{code};{modifier}~" : $"\x1b[{code}~";
        string Function(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : $"\x1bO{final}";

        switch (key)
        {
            case Key.Enter: return alt ? "\x1b\r" : "\r";
            case Key.Back: return ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Function('P');
            case Key.F2: return Function('Q');
            case Key.F3: return Function('R');
            case Key.F4: return Function('S');
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
        }

        if (ctrl && !shift)
        {
            string prefix = alt ? "\x1b" : "";
            if (key is >= Key.A and <= Key.Z) return prefix + (char)(key - Key.A + 1);
            switch (key)
            {
                case Key.Space: case Key.D2: return prefix + "\0";
                case Key.OemOpenBrackets: case Key.D3: return prefix + "\x1b";
                case Key.OemBackslash: case Key.OemPipe: case Key.D4: return prefix + "\x1c";
                case Key.OemCloseBrackets: case Key.D5: return prefix + "\x1d";
                case Key.D6: return prefix + "\x1e";
                case Key.D7: case Key.OemQuestion: return prefix + "\x1f";
                case Key.D8: return prefix + "\x7f";
            }
        }
        // Alt acts as Meta and sends ESC before the key. AltGr arrives as Ctrl+Alt and is left to text input.
        if (alt && !ctrl && key is >= Key.A and <= Key.Z)
            return "\x1b" + (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (alt && !ctrl && key is >= Key.D0 and <= Key.D9)
            return "\x1b" + (char)('0' + (key - Key.D0));
        return null;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text) || _session is null) return;
        e.Handled = true;
        SendInput(e.Text);
    }

    private void ScrollView(int lines)
    {
        if (_session is null) return;
        int max;
        lock (_session.Emulator.Sync) max = _session.Emulator.AlternateScreen ? 0 : _session.Emulator.Scrollback.Count;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, max);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null) return;
        e.Handled = true;
        int lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0) return;
        bool alternate, app;
        lock (_session.Emulator.Sync) { alternate = _session.Emulator.AlternateScreen; app = _session.Emulator.ApplicationCursorKeys; }
        if (alternate)
        {
            // Full-screen programs like less or htop have no scrollback, so the wheel sends arrow keys.
            string arrow = app ? (lines > 0 ? "\x1bOA" : "\x1bOB") : (lines > 0 ? "\x1b[A" : "\x1b[B");
            _session.Send(string.Concat(Enumerable.Repeat(arrow, Math.Abs(lines))));
        }
        else ScrollView(lines);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null) return;
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed) { _ = PasteAsync(); e.Handled = true; return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2) { SelectWord(point.Position); _selecting = false; e.Handled = true; return; }
        _selectionStart = _selectionEnd = null;
        _selecting = true;
        _anchor = PositionAt(point.Position);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    private (long Line, int Column) _anchor;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting || _session is null) return;
        Point position = e.GetPosition(this);
        if (position.Y < 0) ScrollView(1);
        else if (position.Y > Bounds.Height) ScrollView(-1);
        (long, int) here = PositionAt(position);
        if (_selectionStart is null && here == _anchor) return;
        _selectionStart = _anchor;
        _selectionEnd = here;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }
}

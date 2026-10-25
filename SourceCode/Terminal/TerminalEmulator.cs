using System.Globalization;
using System.Text;

namespace AllInOneInstaller.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64,
    /// <summary>The right half of a double-width character, which the cell before it draws.</summary>
    WideTail = 128,
    Wide = 256
}

/// <summary>A character cell. Each colour is <see cref="DefaultColor"/>, a palette index 0-255, or <see cref="Rgb"/> | 0xRRGGBB.</summary>
public struct TerminalCell
{
    public const int DefaultColor = -1;
    public const int Rgb = 0x1000000;

    public int Rune;
    public int Foreground;
    public int Background;
    public CellFlags Flags;

    public static TerminalCell Blank(int background = DefaultColor) =>
        new() { Rune = ' ', Foreground = DefaultColor, Background = background };
}

public sealed class TerminalLine
{
    public TerminalCell[] Cells;
    /// <summary>The text wrapped onto the next line, so copying joins the two.</summary>
    public bool Wrapped;

    public TerminalLine(int columns, int background = TerminalCell.DefaultColor)
    {
        Cells = new TerminalCell[columns];
        Clear(0, columns, background);
    }

    public void Clear(int from, int to, int background)
    {
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++) Cells[i] = TerminalCell.Blank(background);
    }

    public void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        int old = Cells.Length;
        Array.Resize(ref Cells, columns);
        for (int i = old; i < columns; i++) Cells[i] = TerminalCell.Blank();
    }
}

/// <summary>
/// Emulates the part of xterm that common Linux programs use, including 256-colour and truecolor SGR, scroll
/// regions, the alternate screen, DEC line drawing and scrollback. It is not thread safe, so lock <see cref="Sync"/> around every call.
/// </summary>
public sealed class TerminalEmulator
{
    public const int MaxScrollback = 5000;

    public object Sync { get; } = new();
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AlternateScreen => _screen == _alternate;
    public string Title { get; private set; } = "";
    /// <summary>Lines scrolled off the top of the main screen, oldest first.</summary>
    public IReadOnlyList<TerminalLine> Scrollback => _scrollback;
    /// <summary>Lines dropped from the scrollback so far, which keeps absolute line numbers in selections stable.</summary>
    public long ScrollbackDropped { get; private set; }

    /// <summary>Responses to send back to the program, such as cursor position and device attribute reports.</summary>
    public event Action<string>? Reply;
    public event Action? Bell;

    private readonly List<TerminalLine> _scrollback = [];
    private List<TerminalLine> _main;
    private List<TerminalLine> _alternate;
    private List<TerminalLine> _screen;

    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly char[] _chars = new char[8192];
    private char _highSurrogate;

    // Pen and modes
    private int _fg = TerminalCell.DefaultColor, _bg = TerminalCell.DefaultColor;
    private CellFlags _flags;
    private bool _pendingWrap, _autoWrap = true, _originMode, _insertMode, _newLineMode;
    private int _top, _bottom;
    private bool[] _tabStops = [];
    private readonly bool[] _lineDrawing = new bool[2];
    private int _charset; // 0 = G0, 1 = G1 (shift out)
    private (int X, int Y, int Fg, int Bg, CellFlags Flags, bool Origin, bool G0, bool G1, int Charset, bool Wrap) _saved;
    private int _lastRune = ' ';

    // Parser
    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, Dcs, DcsEscape }
    private State _state;
    private readonly StringBuilder _params = new();
    private char _private, _intermediate;
    private readonly StringBuilder _osc = new();

    public TerminalEmulator(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _main = NewScreen();
        _alternate = NewScreen();
        _screen = _main;
        Reset();
    }

    private List<TerminalLine> NewScreen() => Enumerable.Range(0, Rows).Select(_ => new TerminalLine(Columns)).ToList();

    public TerminalLine Line(int row) => _screen[row];

    private void Reset()
    {
        _fg = _bg = TerminalCell.DefaultColor;
        _flags = CellFlags.None;
        _pendingWrap = _originMode = _insertMode = _newLineMode = false;
        _autoWrap = CursorVisible = true;
        ApplicationCursorKeys = BracketedPaste = false;
        _top = 0;
        _bottom = Rows - 1;
        _lineDrawing[0] = _lineDrawing[1] = false;
        _charset = 0;
        _tabStops = new bool[Columns];
        for (int i = 8; i < Columns; i += 8) _tabStops[i] = true;
        _saved = (0, 0, _fg, _bg, CellFlags.None, false, false, false, 0, true);
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int take = Math.Min(data.Length, 4096);
            int count = _utf8.GetChars(data[..take], _chars, flush: false);
            for (int i = 0; i < count; i++) FeedChar(_chars[i]);
            data = data[take..];
        }
    }

    private void FeedChar(char c)
    {
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        int rune = c;
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate == 0) return;
            rune = char.ConvertToUtf32(_highSurrogate, c);
        }
        _highSurrogate = '\0';
        Process(rune);
    }

    private void Process(int c)
    {
        // CAN and SUB abort a sequence. ESC starts a new one anywhere except inside OSC and DCS strings.
        if (c is 0x18 or 0x1a) { _state = State.Ground; return; }
        switch (_state)
        {
            case State.Ground:
                if (c < 0x20 || c == 0x7f) Control(c);
                else Print(c);
                return;
            case State.Escape:
                Escape(c);
                return;
            case State.EscapeIntermediate:
                EscapeIntermediate(c);
                return;
            case State.Csi:
                if (c == 0x1b) { Enter(State.Escape); return; }
                if (c < 0x20) { Control(c); return; }
                if (c is >= '0' and <= '9' or ';' or ':') _params.Append((char)c);
                else if (c is '?' or '>' or '=' or '<' && _params.Length == 0) _private = (char)c;
                else if (c is >= 0x20 and <= 0x2f) _intermediate = (char)c;
                else if (c is >= 0x40 and <= 0x7e) { _state = State.Ground; Csi((char)c); }
                else _state = State.Ground;
                return;
            case State.Osc:
                if (c == 0x07) { _state = State.Ground; Osc(); }
                else if (c == 0x1b) _state = State.OscEscape;
                else if (_osc.Length < 4096) _osc.Append(char.ConvertFromUtf32(c));
                return;
            case State.OscEscape:
                _state = State.Ground;
                if (c == '\\') Osc();
                else { Enter(State.Escape); Escape(c); }
                return;
            case State.Dcs:
                if (c == 0x1b) _state = State.DcsEscape;
                else if (c == 0x07) _state = State.Ground;
                return;
            case State.DcsEscape:
                _state = c == '\\' ? State.Ground : State.Dcs;
                return;
        }
    }

    private void Enter(State state)
    {
        _state = state;
        _params.Clear();
        _private = _intermediate = '\0';
    }

    private void Control(int c)
    {
        switch (c)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: // BS
                if (_pendingWrap) _pendingWrap = false;
                else if (CursorX > 0) CursorX--;
                break;
            case 0x09: // HT
                _pendingWrap = false;
                CursorX = NextTab(CursorX);
                break;
            case 0x0a: case 0x0b: case 0x0c: // LF VT FF
                Index();
                if (_newLineMode) CursorX = 0;
                break;
            case 0x0d: CursorX = 0; _pendingWrap = false; break;
            case 0x0e: _charset = 1; break; // SO
            case 0x0f: _charset = 0; break; // SI
            case 0x1b: Enter(State.Escape); break;
        }
    }

    private int NextTab(int x)
    {
        for (int i = x + 1; i < Columns; i++) if (_tabStops[i]) return i;
        return Columns - 1;
    }

    // DEC Special Graphics, the line-drawing set selected by ESC ( 0 and used by mc, dialog and older ncurses.
    private static int LineDrawing(int c) => c switch
    {
        '`' => '◆', 'a' => '▒', 'f' => '°', 'g' => '±', 'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└', 'n' => '┼',
        'o' => '⎺', 'p' => '⎻', 'q' => '─', 'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
        'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£', '~' => '·', _ => c
    };

    private void Print(int c)
    {
        if (_lineDrawing[_charset]) c = LineDrawing(c);
        int width = RuneWidth(c);
        if (width == 0) return; // combining marks and zero-width characters are not drawn
        _lastRune = c;

        if (_pendingWrap && _autoWrap)
        {
            _screen[CursorY].Wrapped = true;
            CursorX = 0;
            Index();
        }
        _pendingWrap = false;
        if (width == 2 && CursorX == Columns - 1)
        {
            if (!_autoWrap) return;
            _screen[CursorY].Wrapped = true;
            _screen[CursorY].Cells[CursorX] = TerminalCell.Blank(_bg);
            CursorX = 0;
            Index();
        }

        TerminalCell[] cells = _screen[CursorY].Cells;
        if (_insertMode) InsertCells(width);
        FixWide(cells, CursorX);
        if (width == 2) FixWide(cells, CursorX + 1);
        cells[CursorX] = new TerminalCell { Rune = c, Foreground = _fg, Background = _bg, Flags = _flags | (width == 2 ? CellFlags.Wide : 0) };
        if (width == 2) cells[CursorX + 1] = new TerminalCell { Rune = ' ', Foreground = _fg, Background = _bg, Flags = _flags | CellFlags.WideTail };

        if (CursorX + width >= Columns)
        {
            CursorX = Columns - 1;
            _pendingWrap = _autoWrap;
        }
        else CursorX += width;
    }

    // Overwriting half of a wide character blanks its other half.
    private void FixWide(TerminalCell[] cells, int x)
    {
        if (x < 0 || x >= cells.Length) return;
        if ((cells[x].Flags & CellFlags.WideTail) != 0 && x > 0) cells[x - 1] = TerminalCell.Blank(cells[x - 1].Background);
        if ((cells[x].Flags & CellFlags.Wide) != 0 && x + 1 < cells.Length) cells[x + 1] = TerminalCell.Blank(cells[x + 1].Background);
    }

    /// <summary>Returns 0 for combining and zero-width characters, 2 for East Asian wide characters and emoji, otherwise 1.</summary>
    public static int RuneWidth(int c)
    {
        if (c < 0x300) return 1;
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        if (c == 0x200b) return 0;
        bool wide = c is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0x303e or >= 0x3041 and <= 0x33ff or >= 0x3400 and <= 0x4dbf
            or >= 0x4e00 and <= 0x9fff or >= 0xa000 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
            or >= 0xfe30 and <= 0xfe4f or >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1f64f
            or >= 0x1f900 and <= 0x1f9ff or >= 0x1f680 and <= 0x1f6ff or >= 0x20000 and <= 0x3fffd;
        return wide ? 2 : 1;
    }

    private void Index()
    {
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            TerminalLine line = _screen[_top];
            _screen.RemoveAt(_top);
            if (_top == 0 && _screen == _main)
            {
                _scrollback.Add(line);
                if (_scrollback.Count > MaxScrollback)
                {
                    _scrollback.RemoveAt(0);
                    ScrollbackDropped++;
                }
            }
            _screen.Insert(_bottom, new TerminalLine(Columns, _bg));
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            _screen.RemoveAt(_bottom);
            _screen.Insert(_top, new TerminalLine(Columns, _bg));
        }
    }

    private void Escape(int c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': Enter(State.Csi); break;
            case ']': _osc.Clear(); _state = State.Osc; break;
            case 'P': _state = State.Dcs; break;
            case '_': case '^': case 'X': _state = State.Dcs; break; // APC, PM and SOS are ignored like DCS.
            case '(': case ')': case '*': case '+': case '#': case '%': case ' ':
                _intermediate = (char)c;
                _state = State.EscapeIntermediate;
                break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': _pendingWrap = false; Index(); break;
            case 'E': _pendingWrap = false; CursorX = 0; Index(); break;
            case 'M': _pendingWrap = false; ReverseIndex(); break;
            case 'H': if (CursorX < Columns) _tabStops[CursorX] = true; break;
            case 'c':
                _screen = _main;
                foreach (TerminalLine line in _main) line.Clear(0, Columns, TerminalCell.DefaultColor);
                CursorX = CursorY = 0;
                Reset();
                break;
            case '=': case '>': break; // Keypad modes. The keypad sends the same either way here.
            case '\\': break;
        }
    }

    private void EscapeIntermediate(int c)
    {
        _state = State.Ground;
        switch (_intermediate)
        {
            case '(': _lineDrawing[0] = c == '0'; break;
            case ')': _lineDrawing[1] = c == '0'; break;
            case '#' when c == '8': // DECALN fills the screen with E.
                foreach (TerminalLine line in _screen)
                    for (int x = 0; x < Columns; x++) line.Cells[x] = new TerminalCell { Rune = 'E', Foreground = TerminalCell.DefaultColor, Background = TerminalCell.DefaultColor };
                break;
        }
    }

    private void SaveCursor() =>
        _saved = (CursorX, CursorY, _fg, _bg, _flags, _originMode, _lineDrawing[0], _lineDrawing[1], _charset, _pendingWrap);

    private void RestoreCursor()
    {
        (CursorX, CursorY, _fg, _bg, _flags, _originMode, _lineDrawing[0], _lineDrawing[1], _charset, _pendingWrap) = _saved;
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
    }

    private void Osc()
    {
        string text = _osc.ToString();
        int semicolon = text.IndexOf(';');
        if (semicolon > 0 && text[..semicolon] is "0" or "2") Title = text[(semicolon + 1)..];
    }

    // Empty parameters read as 0. Callers apply their own default through Arg().
    private int[] Params()
    {
        if (_params.Length == 0) return [];
        string[] parts = _params.ToString().Split(';', ':');
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            values[i] = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? Math.Min(v, 65535) : 0;
        return values;
    }

    private static int Arg(int[] p, int index, int fallback) => index < p.Length && p[index] > 0 ? p[index] : fallback;

    private void Csi(char final)
    {
        int[] p = Params();
        int n = Arg(p, 0, 1);
        if (_intermediate != '\0')
        {
            if (_intermediate == '!' && final == 'p') { SoftReset(); }
            return; // Cursor style (SP q) and other intermediates are ignored.
        }
        if (_private == '?')
        {
            if (final is 'h' or 'l') foreach (int mode in p) DecMode(mode, final == 'h');
            return;
        }
        if (_private == '>')
        {
            if (final == 'c') Reply?.Invoke("\x1b[>0;276;0c");
            return;
        }
        if (_private != '\0') return;

        if (final != 'b') _pendingWrap = false;
        switch (final)
        {
            case '@': InsertCells(n); break;
            case 'A': CursorY = Math.Max(CursorY < _top ? 0 : _top, CursorY - n); break;
            case 'B': case 'e': CursorY = Math.Min(CursorY > _bottom ? Rows - 1 : _bottom, CursorY + n); break;
            case 'C': case 'a': CursorX = Math.Min(Columns - 1, CursorX + n); break;
            case 'D': CursorX = Math.Max(0, CursorX - n); break;
            case 'E': CursorX = 0; CursorY = Math.Min(_bottom, CursorY + n); break;
            case 'F': CursorX = 0; CursorY = Math.Max(_top, CursorY - n); break;
            case 'G': case '`': CursorX = Math.Clamp(n - 1, 0, Columns - 1); break;
            case 'H': case 'f': MoveTo(Arg(p, 1, 1) - 1, Arg(p, 0, 1) - 1); break;
            case 'I': for (int i = 0; i < n; i++) CursorX = NextTab(CursorX); break;
            case 'Z':
                for (int i = 0; i < n; i++)
                {
                    int x = CursorX - 1;
                    while (x > 0 && !_tabStops[x]) x--;
                    CursorX = Math.Max(0, x);
                }
                break;
            case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;
            case 'L': if (CursorY >= _top && CursorY <= _bottom) { int t = _top; _top = CursorY; ScrollDown(n); _top = t; CursorX = 0; } break;
            case 'M': if (CursorY >= _top && CursorY <= _bottom) { int t = _top; _top = CursorY; ScrollUp(n); _top = t; CursorX = 0; } break;
            case 'P': DeleteCells(n); break;
            case 'S': ScrollUp(n); break;
            case 'T': ScrollDown(n); break;
            case 'X': _screen[CursorY].Clear(CursorX, CursorX + n, _bg); break;
            case 'b': for (int i = 0; i < Math.Min(n, Columns * Rows); i++) Print(_lastRune); break;
            case 'c': Reply?.Invoke("\x1b[?62;22c"); break;
            case 'd': CursorY = Math.Clamp(n - 1 + (_originMode ? _top : 0), 0, Rows - 1); break;
            case 'g':
                if (p.Length == 0 || p[0] == 0) { if (CursorX < Columns) _tabStops[CursorX] = false; }
                else if (p[0] == 3) Array.Clear(_tabStops);
                break;
            case 'h': case 'l':
                foreach (int mode in p)
                {
                    if (mode == 4) _insertMode = final == 'h';
                    else if (mode == 20) _newLineMode = final == 'h';
                }
                break;
            case 'm': Sgr(p); break;
            case 'n':
                if (n == 5) Reply?.Invoke("\x1b[0n");
                else if (n == 6) Reply?.Invoke($"\x1b[{CursorY + 1 - (_originMode ? _top : 0)};{CursorX + 1}R");
                break;
            case 'r':
                int top = Arg(p, 0, 1) - 1, bottom = Arg(p, 1, Rows) - 1;
                if (bottom >= Rows) bottom = Rows - 1;
                if (top < bottom) { _top = top; _bottom = bottom; MoveTo(0, 0); }
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
        }
    }

    private void SoftReset()
    {
        CursorVisible = _autoWrap = true;
        _originMode = _insertMode = false;
        ApplicationCursorKeys = false;
        _top = 0;
        _bottom = Rows - 1;
        _fg = _bg = TerminalCell.DefaultColor;
        _flags = CellFlags.None;
        _lineDrawing[0] = _lineDrawing[1] = false;
        _charset = 0;
    }

    private void MoveTo(int x, int y)
    {
        if (_originMode) y = Math.Clamp(y + _top, _top, _bottom);
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
        _pendingWrap = false;
    }

    private void DecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 6: _originMode = on; MoveTo(0, 0); break;
            case 7: _autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 47: case 1047: SwitchScreen(on, false); break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049: SwitchScreen(on, true); break;
            case 2004: BracketedPaste = on; break;
        }
    }

    private void SwitchScreen(bool alternate, bool saveCursor)
    {
        if (alternate == AlternateScreen) return;
        if (alternate)
        {
            if (saveCursor) SaveCursor();
            _screen = _alternate;
            foreach (TerminalLine line in _alternate) { line.Clear(0, Columns, TerminalCell.DefaultColor); line.Wrapped = false; }
        }
        else
        {
            _screen = _main;
            if (saveCursor) RestoreCursor();
        }
        _pendingWrap = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++) { _screen[y].Clear(0, Columns, _bg); _screen[y].Wrapped = false; }
                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < CursorY; y++) { _screen[y].Clear(0, Columns, _bg); _screen[y].Wrapped = false; }
                break;
            case 2:
                // Like xterm, "clear" moves the main screen's content into the scrollback.
                if (_screen == _main)
                {
                    int last = Rows - 1;
                    while (last >= 0 && IsBlank(_main[last])) last--;
                    for (int y = 0; y <= last; y++) { _scrollback.Add(_main[y]); _main[y] = new TerminalLine(Columns, _bg); }
                    TrimScrollback();
                }
                foreach (TerminalLine line in _screen) { line.Clear(0, Columns, _bg); line.Wrapped = false; }
                break;
            case 3:
                ScrollbackDropped += _scrollback.Count;
                _scrollback.Clear();
                break;
        }
    }

    private void TrimScrollback()
    {
        int extra = _scrollback.Count - MaxScrollback;
        if (extra <= 0) return;
        _scrollback.RemoveRange(0, extra);
        ScrollbackDropped += extra;
    }

    private static bool IsBlank(TerminalLine line) =>
        line.Cells.All(c => c.Rune == ' ' && c.Background == TerminalCell.DefaultColor && (c.Flags & CellFlags.Inverse) == 0);

    private void EraseLine(int mode)
    {
        TerminalLine line = _screen[CursorY];
        switch (mode)
        {
            case 0: line.Clear(CursorX, Columns, _bg); line.Wrapped = false; break;
            case 1: line.Clear(0, CursorX + 1, _bg); break;
            case 2: line.Clear(0, Columns, _bg); line.Wrapped = false; break;
        }
    }

    private void InsertCells(int count)
    {
        TerminalCell[] cells = _screen[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX, cells, CursorX + count, Columns - CursorX - count);
        for (int i = CursorX; i < CursorX + count; i++) cells[i] = TerminalCell.Blank(_bg);
    }

    private void DeleteCells(int count)
    {
        TerminalCell[] cells = _screen[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX + count, cells, CursorX, Columns - CursorX - count);
        for (int i = Columns - count; i < Columns; i++) cells[i] = TerminalCell.Blank(_bg);
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0) p = [0];
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _fg = _bg = TerminalCell.DefaultColor; _flags = CellFlags.None; break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Dim; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4: _flags |= CellFlags.Underline; break;
                case 5: case 6: break; // blink
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Hidden; break;
                case 9: _flags |= CellFlags.Strike; break;
                case 21: _flags |= CellFlags.Underline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~CellFlags.Underline; break;
                case 25: break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Hidden; break;
                case 29: _flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 39: _fg = TerminalCell.DefaultColor; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 49: _bg = TerminalCell.DefaultColor; break;
                case >= 90 and <= 97: _fg = v - 90 + 8; break;
                case >= 100 and <= 107: _bg = v - 100 + 8; break;
                case 38: case 48:
                    int color;
                    if (i + 2 < p.Length && p[i + 1] == 5) { color = Math.Clamp(p[i + 2], 0, 255); i += 2; }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    {
                        color = TerminalCell.Rgb | (Math.Clamp(p[i + 2], 0, 255) << 16) | (Math.Clamp(p[i + 3], 0, 255) << 8) | Math.Clamp(p[i + 4], 0, 255);
                        i += 4;
                    }
                    else return;
                    if (v == 38) _fg = color; else _bg = color;
                    break;
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows) return;

        foreach (List<TerminalLine> screen in new[] { _main, _alternate })
        {
            foreach (TerminalLine line in screen) line.Resize(columns);
            bool isMain = screen == _main;
            bool current = screen == _screen;
            if (rows < Rows)
            {
                // Lines above the cursor go to the scrollback, empty lines below it are dropped.
                int remove = Rows - rows;
                int cursor = current ? CursorY : Rows - 1;
                int below = Math.Min(remove, Rows - 1 - cursor);
                for (int i = 0; i < below && screen.Count > rows; i++)
                    if (IsBlank(screen[^1]) || !current) screen.RemoveAt(screen.Count - 1); else break;
                while (screen.Count > rows)
                {
                    if (isMain) _scrollback.Add(screen[0]);
                    screen.RemoveAt(0);
                    if (current) CursorY--;
                }
            }
            else
            {
                while (screen.Count < rows)
                {
                    if (isMain && _scrollback.Count > 0 && current)
                    {
                        screen.Insert(0, _scrollback[^1]);
                        _scrollback.RemoveAt(_scrollback.Count - 1);
                        _scrollback.TrimExcess();
                        screen[0].Resize(columns);
                        CursorY++;
                        _saved.Y++;
                    }
                    else screen.Add(new TerminalLine(columns));
                }
            }
        }
        foreach (TerminalLine line in _scrollback) line.Resize(columns);
        TrimScrollback();

        Columns = columns;
        Rows = rows;
        CursorX = Math.Clamp(CursorX, 0, columns - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _saved.X = Math.Clamp(_saved.X, 0, columns - 1);
        _saved.Y = Math.Clamp(_saved.Y, 0, rows - 1);
        _top = 0;
        _bottom = rows - 1;
        _pendingWrap = false;
        var tabs = new bool[columns];
        for (int i = 0; i < columns; i++) tabs[i] = i < _tabStops.Length ? _tabStops[i] : i % 8 == 0 && i > 0;
        _tabStops = tabs;
    }

    /// <summary>Gets a line by index, counting the scrollback first and then the screen.</summary>
    public TerminalLine? AbsoluteLine(int index) =>
        index < 0 ? null : index < _scrollback.Count ? _scrollback[index] : index - _scrollback.Count < Rows ? _screen[index - _scrollback.Count] : null;

    public int AbsoluteCount => _scrollback.Count + Rows;

    /// <summary>The text between two absolute positions, inclusive, with wrapped lines joined.</summary>
    public string Text((int Line, int Column) from, (int Line, int Column) to)
    {
        if (to.Line < from.Line || to.Line == from.Line && to.Column < from.Column) (from, to) = (to, from);
        var text = new StringBuilder();
        for (int y = from.Line; y <= to.Line; y++)
        {
            TerminalLine? line = AbsoluteLine(y);
            if (line is null) continue;
            int start = y == from.Line ? from.Column : 0;
            int end = y == to.Line ? Math.Min(to.Column, line.Cells.Length - 1) : line.Cells.Length - 1;
            var part = new StringBuilder();
            for (int x = start; x <= end; x++)
            {
                if ((line.Cells[x].Flags & CellFlags.WideTail) != 0) continue;
                part.Append(char.ConvertFromUtf32(line.Cells[x].Rune));
            }
            bool joined = line.Wrapped && y != to.Line;
            text.Append(joined ? part.ToString() : part.ToString().TrimEnd());
            if (y != to.Line && !joined) text.Append('\n');
        }
        return text.ToString();
    }
}

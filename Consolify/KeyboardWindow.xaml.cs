using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Consolify.Interop;
using Consolify.Services;

namespace Consolify;

internal enum KeyAction { Char, Shift, Layer, Backspace, Delete, Space, Enter, Tab, Escape, CaretLeft, CaretRight }

/// <summary>
/// One key. <paramref name="Units"/> is its width in grid columns, and <paramref name="Hint"/> is
/// the gamepad button drawn in its corner, so the shortcuts are discoverable without a manual.
/// </summary>
internal sealed record KeyDef(string Lower, string? Upper = null, KeyAction Action = KeyAction.Char,
                              int Units = 1, string? Hint = null);

/// <summary>
/// A gamepad-driven on-screen keyboard that never takes the foreground.
///
/// The Windows touch keyboard is a system UIAccess window with its own focus and z-order rules,
/// which is why it misbehaves over games -- nothing outside it can fix that. This one is styled
/// WS_EX_NOACTIVATE and answers WM_MOUSEACTIVATE with MA_NOACTIVATE, so it cannot be activated
/// even by a click: whatever the user was typing into keeps focus and its caret, and keystrokes
/// injected with SendInput land there.
///
/// It cannot appear over a game in *exclusive* fullscreen -- no topmost window can, the touch
/// keyboard included. Borderless windowed is fine.
/// </summary>
public partial class KeyboardWindow : Window
{
    /// <summary>
    /// Every layer is this many columns wide and every key is a whole number of columns starting
    /// on a column boundary, so the keys line up in a true grid: the key above "s" is always "w",
    /// never something between two keys. Rows used to be centred at their natural widths, which
    /// staggered them like a real keyboard and made Up/Down a guess.
    /// </summary>
    private const int Columns = 12;

    private static KeyDef[] Chars(string chars) =>
        chars.Select(c => new KeyDef(c.ToString(), char.ToUpperInvariant(c).ToString())).ToArray();

    /// <summary>A row of single-column character keys plus the fixed two-column key on the right.</summary>
    private static KeyDef[] RowOf(string chars, params KeyDef[] tail) => Chars(chars).Concat(tail).ToArray();

    // Hints match the Xbox keyboard's own bindings, so muscle memory carries over.
    private static readonly KeyDef BackKey = new("Back", Action: KeyAction.Backspace, Units: 2, Hint: "X");
    private static readonly KeyDef ShiftKey = new("Shift", Action: KeyAction.Shift, Units: 2, Hint: "LS");
    private static readonly KeyDef SpaceKey = new("Space", Action: KeyAction.Space, Units: 6, Hint: "Y");
    private static readonly KeyDef EnterKey = new("Enter", Action: KeyAction.Enter, Units: 2, Hint: "Menu");
    private static readonly KeyDef TabKey = new("Tab", Action: KeyAction.Tab, Units: 2);
    private static readonly KeyDef EscKey = new("Esc", Action: KeyAction.Escape, Units: 2);
    private static readonly KeyDef DelKey = new("Del", Action: KeyAction.Delete, Units: 2);
    private static readonly KeyDef LeftKey = new("←", Action: KeyAction.CaretLeft, Hint: "LB");
    private static readonly KeyDef RightKey = new("→", Action: KeyAction.CaretRight, Hint: "RB");

    // Both layers share one skeleton: same 12x5 grid, same right-hand column, same bottom row.
    // Switching layers therefore never resizes or reshuffles the keyboard -- only the faces change.
    private static KeyDef[][] Layer(string r0, string r1, string r2, string r3, KeyDef layerKey, KeyDef r3Tail) => new[]
    {
        RowOf(r0, LeftKey, RightKey),
        RowOf(r1, BackKey),
        RowOf(r2, EnterKey),
        RowOf(r3, r3Tail),
        new[] { layerKey, TabKey, SpaceKey, EscKey },
    };

    private static readonly KeyDef[][] Letters = Layer(
        "1234567890",
        "qwertyuiop",
        "asdfghjkl;",
        "zxcvbnm,./",
        new KeyDef("&123", Action: KeyAction.Layer, Units: 2, Hint: "LT"),
        ShiftKey);

    // The apostrophe, double quote and backtick go in by code point (39, 34, 96) so the last row
    // does not turn into a thicket of escapes.
    //
    // Shift has nothing to do here -- the layer already carries both cases of every ASCII symbol,
    // so a Shift key would light up and type nothing -- and the slot goes to Del instead.
    private static readonly KeyDef[][] Symbols = Layer(
        "1234567890",
        "!@#$%^&*()",
        "-_=+[]{}\\|",
        ";:" + (char)39 + (char)34 + (char)96 + "~<>?/",
        new KeyDef("abc", Action: KeyAction.Layer, Units: 2, Hint: "LT"),
        DelKey);

    private KeyDef[][] _layout = Letters;
    private bool _shift;
    private int _row = 1, _col;
    /// <summary>Grid column the highlight tries to keep while moving up and down.</summary>
    private int _wantCol;
    private double _keySize = 64, _gap = 6, _scale = 1;
    private readonly List<List<Border>> _cells = new();

    /// <summary>Raised when the keyboard wants to close itself (B, or Menu after committing).</summary>
    public event Action? CloseRequested;

    public KeyboardWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Root.MouseMove += OnRootMouseMove;
        Root.MouseLeave += (_, _) => SetPointerOnKey(false);
        Grip.MouseEnter += (_, _) => SetPointerOnKey(false);
        Grip.MouseLeftButtonDown += (_, _) => BeginDrag();
        Grip.MouseMove += (_, _) => { if (_dragging) DragToCursor(); };
        Grip.MouseLeftButtonUp += (_, _) => EndDrag();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // The whole point: no foreground, ever. TOOLWINDOW additionally keeps it out of Alt-Tab.
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));

        // WS_EX_NOACTIVATE still lets a click activate the window; this refuses that too, which is
        // what lets the pointer press keys without the app underneath losing its caret.
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        // Losing the capture (Alt-Tab, another window grabbing it) has to end the drag, or the
        // keyboard would follow the pointer around after the button was long since released.
        if (msg == NativeMethods.WM_CAPTURECHANGED) _dragging = false;
        return IntPtr.Zero;
    }

    // ---- pad / pointer arming ----
    //
    // Same model as the launcher UI. The D-pad paints a highlight and A presses it; moving the
    // pointer (real mouse, or the left stick driving it) hands over to hover instead. With the
    // pointer resting on nothing there is no highlight at all, and A is released back to the
    // gamepad-mouse so it can click a text field in the app underneath -- which is the whole point
    // of a keyboard that never takes focus.

    private volatile bool _padMode = true;
    private volatile bool _pointerOnKey;
    private Point _lastPointer = new(double.NaN, double.NaN);

    /// <summary>
    /// Should the pad's buttons type? False when the pointer is driving and is not over a key,
    /// which is when they should be clicking instead. Read from the gamepad poll thread.
    /// </summary>
    public bool Armed => _padMode || _pointerOnKey;

    /// <summary>The host's input mode changed ("pad" on a D-pad press, "pointer" on stick movement).</summary>
    public void SetInputMode(string mode)
    {
        bool pad = mode == "pad";
        if (_padMode == pad) return;
        _padMode = pad;
        Paint();
    }

    private void SetPointerOnKey(bool on)
    {
        if (_pointerOnKey == on) return;
        _pointerOnKey = on;
        Paint();
    }

    /// <summary>
    /// A real mouse move is its own switch to pointer mode -- the gamepad service only sees the
    /// stick. Guarded on the position actually changing, because WPF also raises MouseMove when
    /// the tree under a stationary pointer is rebuilt.
    /// </summary>
    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (p == _lastPointer) return;
        _lastPointer = p;
        if (_padMode) { _padMode = false; Paint(); }
    }

    // ---- moving the window ----

    private bool _dragging;
    private NativeMethods.POINT _dragFrom;
    private int _dragOriginX, _dragOriginY;
    /// <summary>Once it has been dragged, showing it again must not yank it back to the default
    /// spot -- it was moved off something for a reason.</summary>
    private bool _moved;

    private void BeginDrag()
    {
        NativeMethods.GetCursorPos(out _dragFrom);
        NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        _dragOriginX = r.Left;
        _dragOriginY = r.Top;
        _dragging = true;
        // Capture, or the drag stops the moment the pointer outruns the grab bar.
        Grip.CaptureMouse();
    }

    private void DragToCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        _moved = true;
        MoveTo(_dragOriginX + (p.X - _dragFrom.X), _dragOriginY + (p.Y - _dragFrom.Y));
    }

    private void EndDrag()
    {
        _dragging = false;
        Grip.ReleaseMouseCapture();
    }

    /// <summary>Move without resizing or activating; screen pixels, so no DIP conversion to get wrong.</summary>
    private void MoveTo(int x, int y) =>
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
            x, y, 0, 0, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);

    /// <summary>
    /// Size the keyboard for a display, show it, and park it near the bottom of that display.
    ///
    /// Order matters. WPF sizes the window to its content (SizeToContent), so the real pixel size
    /// only exists once it has been shown and laid out -- and SetWindowPos needs an HWND, which
    /// does not exist before that either. So: build, show, measure the actual window rect, then
    /// move it without resizing. Reading the rect back rather than converting DIPs by hand keeps
    /// it correct on a scaled display.
    /// </summary>
    public void ShowOn(DisplayInfo display, double scale)
    {
        bool resized = Math.Abs(scale - _scale) > 0.001;
        _scale = scale;
        _keySize = Math.Round(Math.Clamp(display.Height * 0.058 * scale, 28, 150));
        _gap = Math.Round(Math.Max(2, _keySize * 0.10));
        Build();

        Show();
        UpdateLayout();

        // A resize invalidates wherever it was dragged to -- the old top-left would push a bigger
        // keyboard off the screen edge -- so re-park it.
        if (_moved && !resized) return;
        _moved = false;

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        MoveTo(display.X + (display.Width - w) / 2,
               display.Y + display.Height - h - (int)(display.Height * 0.06));
    }

    private void Build()
    {
        Rows.Children.Clear();
        _cells.Clear();

        Grip.Height = Math.Round(_keySize * 0.44);
        Grip.Margin = new Thickness(0, 0, 0, Math.Round(_keySize * 0.10));
        GripTitle.FontSize = Math.Round(_keySize * 0.20);
        GripBar.Width = Math.Round(_keySize * 1.4);
        GripBar.Height = Math.Max(3, Math.Round(_keySize * 0.06));

        for (int r = 0; r < _layout.Length; r++)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, r == 0 ? 0 : _gap, 0, 0),
            };
            var rowCells = new List<Border>();

            for (int c = 0; c < _layout[r].Length; c++)
            {
                var key = _layout[r][c];
                int rr = r, cc = c;

                // The arrows are single glyphs like a character key, so they get its type size --
                // at the word-key size they read as specks.
                bool glyph = key.Action is KeyAction.Char or KeyAction.CaretLeft or KeyAction.CaretRight;
                var label = new TextBlock
                {
                    FontSize = glyph ? _keySize * 0.42 : _keySize * 0.26,
                    FontFamily = KeyFont,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var content = new Grid();
                content.Children.Add(label);
                if (key.Hint is { } hint) content.Children.Add(HintBadge(hint));

                var cell = new Border
                {
                    Width = _keySize * key.Units + _gap * (key.Units - 1),
                    Height = _keySize,
                    CornerRadius = new CornerRadius(_keySize * 0.16),
                    Margin = new Thickness(c == 0 ? 0 : _gap, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    Child = content,
                    Tag = label,
                };

                // The pointer is a first-class way to drive this: hovering moves the highlight and
                // a click presses, so the stick can type without the D-pad and vice versa.
                cell.MouseEnter += (_, _) => { _row = rr; _col = cc; _wantCol = ColumnOf(rr, cc); SetPointerOnKey(true); Paint(); };
                cell.MouseLeftButtonUp += (_, _) => { _row = rr; _col = cc; Press(); };

                rowCells.Add(cell);
                panel.Children.Add(cell);
            }

            _cells.Add(rowCells);
            Rows.Children.Add(panel);
        }

        _row = Math.Clamp(_row, 0, _layout.Length - 1);
        _col = Math.Clamp(_col, 0, _layout[_row].Length - 1);
        Paint();
    }

    private string Face(KeyDef k) =>
        k.Action == KeyAction.Char && _shift && k.Upper is { } up ? up : k.Lower;

    // Brighter than the first pass: on a dark key over a dark game the old fill and the background
    // were nearly the same value, so the grid read as one grey slab. Each key now carries its own
    // lighter fill and a visible edge.
    private static readonly FontFamily KeyFont = new("Segoe UI Symbol, Segoe UI");
    private static readonly Brush KeyFill = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x48));
    private static readonly Brush KeyEdge = new SolidColorBrush(Color.FromRgb(0x7E, 0x7E, 0x92));
    private static readonly Brush KeyInk = new SolidColorBrush(Colors.White);
    private static readonly Brush FocusFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x53));
    private static readonly Brush FocusEdge = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x8E));
    private static readonly Brush FocusInk = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A));
    private static readonly Brush LatchFill = new SolidColorBrush(Color.FromRgb(0x7A, 0x59, 0x36));
    private static readonly Brush PillFill = new SolidColorBrush(Color.FromArgb(0xB8, 0x14, 0x14, 0x18));
    private static readonly Brush PillInk = new SolidColorBrush(Color.FromRgb(0xF3, 0xF2, 0xF0));

    /// <summary>
    /// The pad button drawn in a key's corner. The face buttons get their real Xbox shape and
    /// colour and the shoulders/sticks a pill, because "A" as a green disc is read at a glance
    /// from the sofa where the word "Menu" in 11px is not.
    /// </summary>
    private static readonly Dictionary<string, Color> FaceColors = new()
    {
        ["A"] = Color.FromRgb(0x3A, 0xA0, 0x3C),
        ["B"] = Color.FromRgb(0xD3, 0x43, 0x3C),
        ["X"] = Color.FromRgb(0x3C, 0x7C, 0xD3),
        ["Y"] = Color.FromRgb(0xE2, 0xB1, 0x28),
    };

    private UIElement HintBadge(string hint)
    {
        bool face = FaceColors.TryGetValue(hint, out var color);
        // Menu (three bars) and View are glyphs on the pad itself, so draw them, not their names.
        string text = hint == "Menu" ? "☰" : hint == "View" ? "❐" : hint;
        double h = Math.Round(_keySize * 0.28);
        // Inset past the key's own corner radius, or the badge rides out over the rounded edge.
        double inset = Math.Round(_keySize * 0.10);

        var badge = new Border
        {
            Height = h,
            MinWidth = h,
            Background = face ? new SolidColorBrush(color) : PillFill,
            CornerRadius = new CornerRadius(h / 2),
            BorderThickness = new Thickness(face ? 0 : 1),
            BorderBrush = face ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(face ? 0 : _keySize * 0.07, 0, face ? 0 : _keySize * 0.07, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, inset, inset, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = Math.Round(_keySize * (hint == "Menu" ? 0.16 : 0.17)),
                FontFamily = KeyFont,
                FontWeight = face ? FontWeights.SemiBold : FontWeights.Normal,
                // Yellow needs dark ink; the other three carry white.
                Foreground = hint == "Y" ? FocusInk : PillInk,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return badge;
    }

    /// <summary>Repaint faces and the highlight without rebuilding the tree.</summary>
    private void Paint()
    {
        bool show = Armed;
        for (int r = 0; r < _cells.Count; r++)
            for (int c = 0; c < _cells[r].Count; c++)
            {
                var key = _layout[r][c];
                var cell = _cells[r][c];
                bool focused = show && r == _row && c == _col;
                bool latched = key.Action == KeyAction.Shift && _shift;

                cell.Background = focused ? FocusFill : latched ? LatchFill : KeyFill;
                cell.BorderBrush = focused ? FocusEdge : KeyEdge;
                if (cell.Tag is TextBlock tb)
                {
                    tb.Text = Face(key);
                    tb.Foreground = focused ? FocusInk : KeyInk;
                }
            }
    }

    // ---- gamepad-facing API ----

    /// <summary>Leftmost grid column a key occupies.</summary>
    private int ColumnOf(int row, int col)
    {
        int x = 0;
        for (int i = 0; i < col; i++) x += _layout[row][i].Units;
        return x;
    }

    /// <summary>The key covering <paramref name="column"/> in a row. Every row spans all
    /// <see cref="Columns"/> columns, so this always finds one.</summary>
    private int KeyAtColumn(int row, int column)
    {
        column = Math.Clamp(column, 0, Columns - 1);
        int x = 0;
        for (int c = 0; c < _layout[row].Length; c++)
        {
            x += _layout[row][c].Units;
            if (column < x) return c;
        }
        return _layout[row].Length - 1;
    }

    public void Move(string dir)
    {
        // A D-pad press is the pad taking over from the pointer, exactly as in the launcher.
        _padMode = true;
        switch (dir)
        {
            case "Left":
                _col = _col > 0 ? _col - 1 : _layout[_row].Length - 1;
                _wantCol = ColumnOf(_row, _col);
                break;
            case "Right":
                _col = _col < _layout[_row].Length - 1 ? _col + 1 : 0;
                _wantCol = ColumnOf(_row, _col);
                break;
            case "Up":
            case "Down":
                _row = dir == "Up"
                    ? (_row > 0 ? _row - 1 : _layout.Length - 1)
                    : (_row < _layout.Length - 1 ? _row + 1 : 0);
                // _wantCol is sticky, so passing through a wide key and out the other side
                // returns to the column you started in rather than that key's left edge.
                _col = KeyAtColumn(_row, _wantCol);
                break;
        }
        Paint();
    }

    /// <summary>Press the highlighted key.</summary>
    public void Press()
    {
        if (!Armed) return;
        var key = _layout[_row][_col];
        switch (key.Action)
        {
            case KeyAction.Char:
                NativeMethods.SendChar(Face(key)[0]);
                // Shift is one-shot, like a phone keyboard: nobody wants to unlatch it by hand
                // after every capital.
                if (_shift) { _shift = false; Paint(); }
                break;
            case KeyAction.Shift:      ToggleShift(); break;
            case KeyAction.Layer:      ToggleLayer(); break;
            case KeyAction.Backspace:  Backspace(); break;
            case KeyAction.Delete:     NativeMethods.SendVirtualKey(NativeMethods.VK_DELETE, extended: true); break;
            case KeyAction.Space:      Space(); break;
            case KeyAction.Enter:      Commit(); break;
            case KeyAction.Tab:        NativeMethods.SendVirtualKey(NativeMethods.VK_TAB); break;
            case KeyAction.Escape:     NativeMethods.SendVirtualKey(NativeMethods.VK_ESCAPE); break;
            case KeyAction.CaretLeft:  CaretLeft(); break;
            case KeyAction.CaretRight: CaretRight(); break;
        }
    }

    public void Backspace() => NativeMethods.SendVirtualKey(NativeMethods.VK_BACK);
    public void Space() => NativeMethods.SendChar(' ');
    public void ToggleShift() { _shift = !_shift; Paint(); }
    public void ToggleLayer() { _layout = _layout == Letters ? Symbols : Letters; _shift = false; Build(); }
    public void CaretLeft() => NativeMethods.SendVirtualKey(NativeMethods.VK_LEFT, extended: true);
    public void CaretRight() => NativeMethods.SendVirtualKey(NativeMethods.VK_RIGHT, extended: true);

    /// <summary>Enter, then get out of the way -- the same thing Menu does on the Xbox keyboard.</summary>
    public void Commit()
    {
        NativeMethods.SendVirtualKey(NativeMethods.VK_RETURN);
        CloseRequested?.Invoke();
    }

    public void RequestClose() => CloseRequested?.Invoke();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Consolify.Interop;
using Consolify.Services;

namespace Consolify;

internal enum KeyAction { Char, Shift, Layer, Backspace, Space, Enter, Tab, Escape }

/// <summary>
/// One key. <paramref name="Units"/> is its width as a multiple of a standard key, and
/// <paramref name="Hint"/> is the gamepad button printed in its corner, so the shortcuts are
/// discoverable without a manual.
/// </summary>
internal sealed record KeyDef(string Lower, string? Upper = null, KeyAction Action = KeyAction.Char,
                              double Units = 1, string? Hint = null);

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
    private static KeyDef[] Row(string chars) =>
        chars.Select(c => new KeyDef(c.ToString(), char.ToUpperInvariant(c).ToString())).ToArray();

    private static KeyDef[] Edged(KeyDef first, string middle, KeyDef last) =>
        new[] { first }.Concat(Row(middle)).Append(last).ToArray();

    // Hints match the Xbox keyboard's own bindings, so muscle memory carries over.
    private static readonly KeyDef BackKey = new("Back", Action: KeyAction.Backspace, Units: 1.5, Hint: "X");
    private static readonly KeyDef ShiftKey = new("Shift", Action: KeyAction.Shift, Units: 1.5, Hint: "LS");
    private static readonly KeyDef SpaceKey = new("Space", Action: KeyAction.Space, Units: 4, Hint: "Y");
    private static readonly KeyDef EnterKey = new("Enter", Action: KeyAction.Enter, Units: 3, Hint: "Menu");
    private static readonly KeyDef TabKey = new("Tab", Action: KeyAction.Tab, Units: 1.5);

    private static readonly KeyDef[][] Letters =
    {
        Row("1234567890"),
        Row("qwertyuiop"),
        Row("asdfghjkl"),
        Edged(ShiftKey, "zxcvbnm,.", BackKey),
        new[] { new KeyDef("&123", Action: KeyAction.Layer, Units: 1.5, Hint: "LT"), TabKey, SpaceKey, EnterKey },
    };

    private static readonly KeyDef[][] Symbols =
    {
        Row("1234567890"),
        Row("!@#$%^&*()"),
        Row("-_=+[]{}\\|"),
        // The apostrophe, double quote and backtick go in by code point (39, 34, 96) so this
        // row does not turn into a thicket of escapes.
        Edged(new KeyDef("Esc", Action: KeyAction.Escape, Units: 1.5),
              ";:" + (char)39 + (char)34 + (char)96 + "~/?", BackKey),
        new[] { new KeyDef("abc", Action: KeyAction.Layer, Units: 1.5, Hint: "LT"), TabKey, SpaceKey, EnterKey },
    };

    private KeyDef[][] _layout = Letters;
    private bool _shift;
    private int _row = 1, _col;
    private double _keySize = 64, _gap = 6;
    private readonly List<List<Border>> _cells = new();

    /// <summary>Raised when the keyboard wants to close itself (B, or Menu after committing).</summary>
    public event Action? CloseRequested;

    public KeyboardWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
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
        return IntPtr.Zero;
    }

    /// <summary>
    /// Size the keyboard for a display, show it, and park it near the bottom of that display.
    ///
    /// Order matters. WPF sizes the window to its content (SizeToContent), so the real pixel size
    /// only exists once it has been shown and laid out -- and SetWindowPos needs an HWND, which
    /// does not exist before that either. So: build, show, measure the actual window rect, then
    /// move it without resizing. Reading the rect back rather than converting DIPs by hand keeps
    /// it correct on a scaled display.
    /// </summary>
    public void ShowOn(DisplayInfo display)
    {
        _keySize = Math.Round(Math.Clamp(display.Height * 0.058, 40, 96));
        _gap = Math.Round(_keySize * 0.10);
        Build();

        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            display.X + (display.Width - w) / 2,
            display.Y + display.Height - h - (int)(display.Height * 0.06),
            0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
    }

    private void Build()
    {
        Rows.Children.Clear();
        _cells.Clear();

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

                var label = new TextBlock
                {
                    FontSize = key.Action == KeyAction.Char ? _keySize * 0.42 : _keySize * 0.26,
                    FontFamily = new FontFamily("Segoe UI"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var content = new Grid();
                content.Children.Add(label);

                if (key.Hint is { } hint)
                {
                    content.Children.Add(new Border
                    {
                        Background = HintFill,
                        CornerRadius = new CornerRadius(_keySize * 0.10),
                        Padding = new Thickness(_keySize * 0.09, _keySize * 0.01, _keySize * 0.09, _keySize * 0.02),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, _keySize * 0.06, _keySize * 0.06, 0),
                        Child = new TextBlock
                        {
                            Text = hint,
                            FontSize = _keySize * 0.18,
                            FontFamily = new FontFamily("Segoe UI"),
                            Foreground = HintInk,
                        },
                    });
                }

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
                cell.MouseEnter += (_, _) => { _row = rr; _col = cc; Paint(); };
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
    private static readonly Brush KeyFill = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x48));
    private static readonly Brush KeyEdge = new SolidColorBrush(Color.FromRgb(0x7E, 0x7E, 0x92));
    private static readonly Brush KeyInk = new SolidColorBrush(Colors.White);
    private static readonly Brush FocusFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x53));
    private static readonly Brush FocusEdge = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x8E));
    private static readonly Brush FocusInk = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A));
    private static readonly Brush LatchFill = new SolidColorBrush(Color.FromRgb(0x7A, 0x59, 0x36));
    private static readonly Brush HintFill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private static readonly Brush HintInk = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x53));

    /// <summary>Repaint faces and the highlight without rebuilding the tree.</summary>
    private void Paint()
    {
        for (int r = 0; r < _cells.Count; r++)
            for (int c = 0; c < _cells[r].Count; c++)
            {
                var key = _layout[r][c];
                var cell = _cells[r][c];
                bool focused = r == _row && c == _col;
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

    /// <summary>
    /// Horizontal centre of a key in key-units, so moving between ragged rows lands under the
    /// finger instead of resetting to column 0.
    /// </summary>
    private double CentreOf(int row, int col)
    {
        double x = 0;
        for (int i = 0; i < col; i++) x += _layout[row][i].Units;
        return x + _layout[row][col].Units / 2;
    }

    private int NearestCol(int row, double centre)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int c = 0; c < _layout[row].Length; c++)
        {
            double d = Math.Abs(CentreOf(row, c) - centre);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    public void Move(string dir)
    {
        switch (dir)
        {
            case "Left":  _col = _col > 0 ? _col - 1 : _layout[_row].Length - 1; break;
            case "Right": _col = _col < _layout[_row].Length - 1 ? _col + 1 : 0; break;
            case "Up":
            case "Down":
            {
                double centre = CentreOf(_row, _col);
                _row = dir == "Up"
                    ? (_row > 0 ? _row - 1 : _layout.Length - 1)
                    : (_row < _layout.Length - 1 ? _row + 1 : 0);
                _col = NearestCol(_row, centre);
                break;
            }
        }
        Paint();
    }

    /// <summary>Press the highlighted key.</summary>
    public void Press()
    {
        var key = _layout[_row][_col];
        switch (key.Action)
        {
            case KeyAction.Char:
                NativeMethods.SendChar(Face(key)[0]);
                // Shift is one-shot, like a phone keyboard: nobody wants to unlatch it by hand
                // after every capital.
                if (_shift) { _shift = false; Paint(); }
                break;
            case KeyAction.Shift:     ToggleShift(); break;
            case KeyAction.Layer:     ToggleLayer(); break;
            case KeyAction.Backspace: Backspace(); break;
            case KeyAction.Space:     Space(); break;
            case KeyAction.Enter:     Commit(); break;
            case KeyAction.Tab:       NativeMethods.SendVirtualKey(NativeMethods.VK_TAB); break;
            case KeyAction.Escape:    NativeMethods.SendVirtualKey(NativeMethods.VK_ESCAPE); break;
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

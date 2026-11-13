using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Spread;

/// <summary>What the top bar gives its widgets.</summary>
internal sealed class BarContext
{
    public Config Cfg { get; }
    public Dispatcher Dispatcher { get; }
    /// <summary>Call when the widget's size or visibility changed (the bar then re-checks that everything fits).</summary>
    public Action Changed { get; }
    /// <summary>Brings the real taskbar on screen, runs the function on a worker thread (true = done), then puts the taskbar away again. On false it stays for the user.</summary>
    public Action<Func<bool>, string?> WithRealTaskbar { get; }
    public BarContext(Config cfg, Dispatcher d, Action changed, Action<Func<bool>, string?> withRealTaskbar) { Cfg = cfg; Dispatcher = d; Changed = changed; WithRealTaskbar = withRealTaskbar; }
}

/// <summary>One item in the top bar. <see cref="Tick"/> runs on the UI thread once a second; slow work must go to a worker thread.</summary>
internal abstract class BarWidget
{
    public abstract string Id { get; }
    /// <summary>When the bar is too narrow, widgets with a LOW priority disappear first.</summary>
    public virtual int Priority => 5;
    public FrameworkElement View { get; protected set; } = null!;
    public bool Present { get; protected set; } = true;
    protected BarContext Ctx = null!;
    long _next;

    public void Attach(BarContext c) { Ctx = c; Build(); }
    protected abstract void Build();
    public virtual void Tick() { }
    public virtual void OnForeground() { }
    public virtual void Stop() { }

    /// <summary>True when at least <paramref name="ms"/> passed since the last time this returned true (the first call is true).</summary>
    protected bool Every(int ms)
    {
        long n = Environment.TickCount64;
        if (n < _next) return false;
        _next = n + ms;
        return true;
    }

    protected void SetPresent(bool p)
    {
        if (p == Present) return;
        Present = p;
        Ctx.Changed();
    }
}

internal static class BarUi
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Emoji = new("Segoe UI Emoji");
    public static readonly FontFamily Text = new("Segoe UI");
    /// <summary>Frozen: shared by every popover and toast, and an unfrozen brush belongs to the thread that first touched this class.</summary>
    public static readonly Brush Dim = Frozen(Color.FromArgb(0xAA, 255, 255, 255));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static TextBlock Txt(string s, double size = 13, Brush? fg = null, FontWeight? w = null) => new()
    {
        Text = s, FontSize = size, Foreground = fg ?? Brushes.White, FontFamily = Text, FontWeight = w ?? FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A line "left ....... right" for popovers.</summary>
    public static FrameworkElement Row(string left, string right, bool dim = false)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 220 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Txt(left, 13, dim ? Dim : Brushes.White, FontWeights.Normal); l.Margin = new Thickness(0, 0, 18, 0);
        var r = Txt(right, 13, dim ? Dim : Brushes.White, FontWeights.SemiBold);
        Grid.SetColumn(r, 1);
        g.Children.Add(l); g.Children.Add(r);
        return g;
    }

    public static Border Button(string text, Action onClick)
    {
        var b = new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 6, 6, 0), Cursor = System.Windows.Input.Cursors.Hand, Child = Txt(text, 13) };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
        b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }
}

/// <summary>
/// A small panel under a bar item. Never takes the keyboard focus; closes when anything outside it is clicked
/// (and when its item is clicked again).
/// </summary>
internal sealed class Popover : Window
{
    static Popover? _open;
    static FrameworkElement? _anchor, _closedAnchor;
    static long _closedAt;

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(80) };
    bool _wasDown = true;
    IntPtr _hwnd;
    Action? _onClosed;
    bool _center, _alignRight;

    public static void Toggle(FrameworkElement anchor, Func<FrameworkElement> build, Action? onClosed = null, bool center = false, bool alignRight = false)
    {
        if (_open != null)
        {
            bool same = _anchor == anchor;
            _open.CloseNow();
            if (same) return;
        }
        else if (_closedAnchor == anchor && Environment.TickCount64 - _closedAt < 350) return;   // the click that just closed it
        FrameworkElement content;
        try { content = build(); } catch (Exception e) { Log.Write("popover failed: " + e.Message); return; }
        var p = new Popover(content) { _onClosed = onClosed, _center = center, _alignRight = alignRight };
        _open = p; _anchor = anchor;
        p.PlaceUnder(anchor, content);
        p.Show();
        p._poll.Start();
    }

    public static void CloseAny() => _open?.CloseNow();

    /// <summary>Popovers never take the keyboard focus, except when a text box in them asks for it (search, login fields).</summary>
    public static void RequestFocus()
    {
        var p = _open;
        if (p == null || p._hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(p._hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) Native.SetWindowLongPtr(p._hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
        try { p.Activate(); Native.SetForegroundWindow(p._hwnd); } catch { }
    }

    Popover(FrameworkElement content)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 12), Child = content
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        _poll.Tick += (_, _) => Poll();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CloseNow(); } };
    }

    void PlaceUnder(FrameworkElement anchor, FrameworkElement content)
    {
        double dpi = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var pt = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = content.DesiredSize.Width + 30;
        double x = _center ? pt.X / dpi + anchor.ActualWidth / 2 - w / 2 : _alignRight ? pt.X / dpi + anchor.ActualWidth - w : pt.X / dpi;
        Left = Math.Max(4, Math.Min(x, SystemParameters.PrimaryScreenWidth - w - 8));
        Top = pt.Y / dpi + 2;
    }

    void Poll()
    {
        bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed) return;
        Native.GetCursorPos(out var p);
        var under = Native.WindowFromPoint(p);
        var root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, 2);
        if (root == _hwnd && root != IntPtr.Zero) return;
        CloseNow();
    }

    void CloseNow()
    {
        _poll.Stop();
        _closedAt = Environment.TickCount64; _closedAnchor = _anchor;
        if (_open == this) _open = null;
        try { _onClosed?.Invoke(); } catch { }
        try { Close(); } catch { }
    }
}

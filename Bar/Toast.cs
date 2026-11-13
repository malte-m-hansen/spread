using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Spread;

// Spread's own notification cards ("toasts"): small, never take focus, click opens the link. Generic: other widgets can use them.

internal enum ToastKind { Jira, Confluence, Info, Security }

/// <summary>Sticky = stays until the user closes it (or StickySeconds, when set).</summary>
internal sealed record ToastSpec(string Id, ToastKind Kind, string Title, string Body, string Footer, string? Url, bool Sticky = false);

/// <summary>Pure queue logic (clock is a parameter): capacity, FIFO, auto-dismiss, hover pause, mute, suppression.</summary>
internal sealed class ToastQueue
{
    public sealed class Entry
    {
        public ToastSpec Spec = null!;
        public double Remaining;
        public bool Hovered;
    }

    public int Capacity { get; set; } = 3;
    public double Seconds { get; set; } = 10;
    /// <summary>How long a sticky toast stays; 0 = until it is closed.</summary>
    public double StickySeconds { get; set; } = 0;
    /// <summary>True while a toast must not appear (fullscreen app, overview open, notifications off). Waiting toasts are kept.</summary>
    public bool Suppressed { get; set; }

    readonly List<Entry> _visible = new();   // newest first
    readonly Queue<ToastSpec> _waiting = new();
    double _muteUntil = -1, _last = -1;

    public IReadOnlyList<Entry> Visible => _visible;
    public int Waiting => _waiting.Count;
    public bool IsMuted(double now) => now < _muteUntil;

    /// <summary>False when it was dropped (muted, or the same id is already shown/waiting).</summary>
    public bool Enqueue(ToastSpec s, double now)
    {
        if (IsMuted(now)) return false;
        if (_visible.Any(v => v.Spec.Id == s.Id) || _waiting.Any(w => w.Id == s.Id)) return false;
        _waiting.Enqueue(s);
        return true;
    }

    public void Mute(double now, double seconds) { _muteUntil = now + seconds; DismissAll(); _waiting.Clear(); }
    public void Dismiss(Entry e) => _visible.Remove(e);
    public void DismissAll() => _visible.Clear();
    public void SetHover(Entry e, bool hovered) => e.Hovered = hovered;

    /// <summary>Advances time. Returns true when the visible set changed.</summary>
    public bool Tick(double now)
    {
        double dt = _last < 0 ? 0 : Math.Max(0, now - _last);
        _last = now;
        bool changed = false;
        foreach (var e in _visible.ToList())
        {
            if (e.Hovered || Suppressed) continue;
            e.Remaining -= dt;
            if (e.Remaining <= 0) { _visible.Remove(e); changed = true; }
        }
        while (!Suppressed && _visible.Count < Capacity && _waiting.Count > 0)
        {
            var next = _waiting.Dequeue();
            _visible.Insert(0, new Entry { Spec = next, Remaining = next.Sticky ? (StickySeconds > 0 ? StickySeconds : double.PositiveInfinity) : Seconds });
            changed = true;
        }
        return changed;
    }
}

internal sealed class ToastWindow : Window
{
    public readonly ToastQueue.Entry Entry;
    readonly Action<ToastWindow> _clicked, _closed, _muted, _clearAll;
    public const double CardWidth = 392, CardHeight = 92;

    static Color AccentOf(ToastKind k) => k switch { ToastKind.Jira => Color.FromRgb(0x4C, 0x8D, 0xFF), ToastKind.Confluence => Color.FromRgb(0x3D, 0xD6, 0xB4), ToastKind.Security => Color.FromRgb(0xF0, 0x55, 0x5B), _ => Color.FromRgb(0xB0, 0xB0, 0xB0) };
    static string LetterOf(ToastKind k) => k switch { ToastKind.Jira => "J", ToastKind.Confluence => "C", ToastKind.Security => "!", _ => "i" };

    public ToastWindow(ToastQueue.Entry entry, Action<ToastWindow> clicked, Action<ToastWindow> closed, Action<ToastWindow> muted, Action<ToastWindow> clearAll, Action<ToastWindow, bool> hover)
    {
        Entry = entry; _clicked = clicked; _closed = closed; _muted = muted; _clearAll = clearAll;
        var s = entry.Spec;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Width = CardWidth; Height = CardHeight; Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var accent = AccentOf(s.Kind);
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
            Child = new TextBlock { Text = LetterOf(s.Kind), Foreground = Brushes.Black, FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var title = new TextBlock { Text = s.Title, Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var body = new TextBlock { Text = s.Body, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };
        var foot = new TextBlock { Text = s.Footer, Foreground = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), FontFamily = BarUi.Text, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel(); text.Children.Add(title); text.Children.Add(body); text.Children.Add(foot);
        var x = new TextBlock { Text = "✕", Foreground = BarUi.Dim, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; _closed(this); };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1); Grid.SetColumn(x, 2);
        grid.Children.Add(badge); grid.Children.Add(text); grid.Children.Add(x);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1E, 0x1E, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 12, 10), Margin = new Thickness(0), Child = grid, Cursor = s.Url != null ? Cursors.Hand : Cursors.Arrow
        };
        Content = card;
        card.MouseLeftButtonUp += (_, _) => _clicked(this);
        card.MouseEnter += (_, _) => hover(this, true);
        card.MouseLeave += (_, _) => hover(this, false);
        var menu = new ContextMenu();
        var mute = new MenuItem { Header = "Mute alerts for 1 hour" }; mute.Click += (_, _) => _muted(this);
        var clear = new MenuItem { Header = "Dismiss all" }; clear.Click += (_, _) => _clearAll(this);
        menu.Items.Add(mute); menu.Items.Add(clear);
        card.ContextMenu = menu;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
    }

    public void FadeIn() => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

    public void FadeOutAndClose()
    {
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
        a.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, a);
    }
}

/// <summary>Shows the toasts top-right under the top bar, at most 3 at a time, and keeps them out of the way (fullscreen, overview, notifications off).</summary>
internal static class ToastManager
{
    static readonly ToastQueue Queue = new();
    static readonly Dictionary<ToastQueue.Entry, ToastWindow> Windows = new();
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static Config _cfg = null!;
    static Func<bool> _overlayVisible = () => false;
    static bool _init, _dnd = true;
    static double _dndChecked = -100;

    static double Now => Clock.Elapsed.TotalSeconds;

    public static void Init(Config cfg, Func<bool> overlayVisible)
    {
        _cfg = cfg; _overlayVisible = overlayVisible;
        if (_init) return;
        _init = true;
        Timer.Tick += (_, _) => Tick();
        Timer.Start();
    }

    public static void Show(ToastSpec spec)
    {
        if (!_init) return;
        Queue.Seconds = Math.Clamp(_cfg.Atlassian.Alerts.ToastSeconds, 3, 60);
        Queue.StickySeconds = Math.Max(0, _cfg.GitHub.Toast.Seconds);
        if (Queue.Enqueue(spec, Now)) Tick();
    }

    public static void MuteAn(double seconds) { Queue.Mute(Now, seconds); Tick(); }

    static bool Fullscreen()
    {
        try
        {
            var hmon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            return Native.GetMonitorInfo(hmon, ref mi) && HotEdge.ForegroundIsFullscreen(mi.rcMonitor);
        }
        catch { return false; }
    }

    static void Tick()
    {
        double now = Now;
        if (now - _dndChecked > 5) { _dnd = DndProbe.ToastsEnabled(); _dndChecked = now; }
        Queue.Suppressed = _overlayVisible() || Fullscreen() || (_cfg.Atlassian.Alerts.RespectDnd && !_dnd);
        Queue.Tick(now);
        Sync();
    }

    static void Sync()
    {
        // close windows of entries that are gone
        foreach (var kv in Windows.ToList())
            if (!Queue.Visible.Contains(kv.Key)) { Windows.Remove(kv.Key); kv.Value.FadeOutAndClose(); }
        // create windows for new entries
        foreach (var e in Queue.Visible)
            if (!Windows.ContainsKey(e))
            {
                var w = new ToastWindow(e, Clicked, Dismissed, Muted, ClearAll, (win, h) => Queue.SetHover(win.Entry, h));
                Windows[e] = w;
                Place(w, Queue.Visible.ToList().IndexOf(e));
                w.Show(); w.FadeIn();
            }
        int i = 0;
        foreach (var e in Queue.Visible) { if (Windows.TryGetValue(e, out var w)) Place(w, i); i++; }
    }

    static void Place(ToastWindow w, int index)
    {
        var hmon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        Native.GetMonitorInfo(hmon, ref mi);
        uint dpi = 96;
        try { if (Native.GetDpiForMonitor(hmon, 0, out uint dx, out _) == 0) dpi = dx; } catch { }
        double k = dpi / 96.0;
        string pos = (_cfg.Atlassian.Alerts.Position ?? "top-center").ToLowerInvariant();
        w.Left = pos == "top-right" ? mi.rcWork.Right / k - ToastWindow.CardWidth - 14
               : pos == "top-left" ? mi.rcWork.Left / k + 14
               : (mi.rcWork.Left + mi.rcWork.Right) / 2.0 / k - ToastWindow.CardWidth / 2;
        w.Top = mi.rcWork.Top / k + 12 + index * (ToastWindow.CardHeight + 8);
    }

    static void Clicked(ToastWindow w)
    {
        var url = w.Entry.Spec.Url;
        Queue.Dismiss(w.Entry); Sync();
        try { if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    static void Dismissed(ToastWindow w) { Queue.Dismiss(w.Entry); Sync(); }
    static void Muted(ToastWindow w) => MuteAn(3600);
    static void ClearAll(ToastWindow w) { Queue.DismissAll(); Sync(); }
}

/// <summary>`--toast-test`: queue rules with a fake clock; with `--show` also three sample toasts (synthetic text) on screen.</summary>
internal static class ToastTest
{
    static int _fail;
    static void Check(string name, bool ok) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}"); if (!ok) _fail++; }
    static ToastSpec S(string id) => new(id, ToastKind.Jira, "t" + id, "b", "f", null);

    public static int Run(string[] args)
    {
        Console.WriteLine("Toast queue checks (fake clock):");
        var q = new ToastQueue { Capacity = 3, Seconds = 10 };
        for (int i = 1; i <= 5; i++) q.Enqueue(S(i.ToString()), 0);
        q.Tick(0);
        Check("max 3 visible, 2 waiting", q.Visible.Count == 3 && q.Waiting == 2);
        Check("newest on top", q.Visible[0].Spec.Id == "3" && q.Visible[2].Spec.Id == "1");
        Check("duplicate id dropped", !q.Enqueue(S("1"), 0) && !q.Enqueue(S("5"), 0));
        q.Tick(5);
        Check("not expired after 5 s", q.Visible.Count == 3);
        q.SetHover(q.Visible[2], true);
        q.Tick(10.5);
        Check("hover pauses (entry 1 stays), others expire and the queue refills", q.Visible.Any(v => v.Spec.Id == "1") && q.Visible.Any(v => v.Spec.Id == "4") && q.Visible.Any(v => v.Spec.Id == "5") && q.Waiting == 0);
        q.SetHover(q.Visible.First(v => v.Spec.Id == "1"), false);
        q.Tick(21);
        Check("everything expires after the hover ends", q.Visible.Count == 0);
        q.Suppressed = true; q.Enqueue(S("a"), 30); q.Tick(30);
        Check("suppressed: nothing shown, nothing lost", q.Visible.Count == 0 && q.Waiting == 1);
        q.Suppressed = false; q.Tick(31);
        Check("shown when no longer suppressed", q.Visible.Count == 1 && q.Visible[0].Spec.Id == "a");
        q.Mute(40, 3600);
        Check("mute clears and drops new toasts", q.Visible.Count == 0 && !q.Enqueue(S("m"), 100) && q.IsMuted(100) && !q.IsMuted(3641));
        Console.WriteLine(_fail == 0 ? "TOAST TEST PASSED" : $"TOAST TEST FAILED ({_fail})");
        if (_fail != 0 || !args.Contains("--show")) return _fail == 0 ? 0 : 1;

        Console.WriteLine("Showing 3 sample toasts for 14 s ...");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var cfg = new Config();
        ToastManager.Init(cfg, () => false);
        ToastManager.Show(new ToastSpec("s1", ToastKind.Jira, "Sample Person mentioned you", "Could you have a look at the new data model before the sprint review on Monday?", "PROJ-1019 · Sample issue title · 14:02", "https://example.com"));
        ToastManager.Show(new ToastSpec("s2", ToastKind.Confluence, "Mentioned in Confluence", "Retrospective: sample sprint", "Sample Person · 13:58", "https://example.com"));
        ToastManager.Show(new ToastSpec("s3", ToastKind.Jira, "Sample Person assigned PROJ-1020 to you", "Another sample issue title", "PROJ-1020 · 13:50", "https://example.com"));
        var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
        stop.Tick += (_, _) => app.Shutdown();
        stop.Start();
        app.Run();
        return 0;
    }
}

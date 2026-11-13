using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// The always-visible top bar (GNOME style): Activities button + active application on the left, clock/date in the middle,
/// status on the right. It is a real AppBar: Windows reserves its screen space (maximized windows stop below it) and the
/// overview, which covers only the work area, leaves it untouched.
/// </summary>
internal sealed class TopBarWindow : Window
{
    static readonly IntPtr HWND_BOTTOM = new(1);

    readonly Config _cfg;
    readonly WinEvents _events;
    readonly Action _toggleOverview;
    readonly WindowCache _cache;
    readonly StackPanel _tray = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _trayTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    string _traySig = "";
    bool _trayBusy;
    readonly TextBlock _appName = new(), _clock = new(), _battery = new();
    readonly TextBlock _badgeText = new() { Foreground = Brushes.Black, FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
    readonly Border _badge = new() { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x40)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    ClockPanel? _panel;
    FrameworkElement? _centrePill;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly uint _selfPid = (uint)Environment.ProcessId;
    IntPtr _hwnd;
    uint _callbackMsg, _taskbarCreatedMsg;
    bool _registered;
    RECT _mon;
    int _heightPx;

    /// <summary>Raised (debounced) when the usable screen area changed, e.g. after the bar or taskbar moved.</summary>
    public event Action? WorkAreaChanged;

    public TopBarWindow(Config cfg, WinEvents events, WindowCache cache, Action toggleOverview)
    {
        _cfg = cfg; _events = events; _cache = cache; _toggleOverview = toggleOverview;
        Title = "Spread top bar";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; Width = 400; Height = cfg.TopBarHeight;
        Background = Brushes.Black;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = BuildUi();
        _timer.Tick += (_, _) => { UpdateClock(); UpdateBattery(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); WorkAreaChanged?.Invoke(); };
    }

    // ---------- UI ----------

    internal static FrameworkElement Pill(FrameworkElement content, Action onClick, double pad = 14)
    {
        var b = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(14), Padding = new Thickness(pad, 0, pad, 0), Margin = new Thickness(2, 3, 2, 3), Child = content, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    static TextBlock Label(string text, double size = 13.5, FontWeight? weight = null) => new()
    {
        Text = text, Foreground = Brushes.White, FontSize = size, FontFamily = new FontFamily("Segoe UI"), FontWeight = weight ?? FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    readonly List<BarWidget> _left = new(), _right = new(), _inPill = new();
    readonly StackPanel _leftPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
    readonly StackPanel _rightPanel = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    readonly DispatcherTimer _widgetTimer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };   // Normal: a busy dispatcher must not starve the widgets
    BarContext _ctx = null!;
    bool _fitQueued;

    BarWidget? Make(BarWidget w) { if (_cfg.DisabledWidgets.Contains(w.Id, StringComparer.OrdinalIgnoreCase)) return null; w.Attach(_ctx); return w; }

    void RequestFit()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(new Action(Fit), DispatcherPriority.Background);
    }

    /// <summary>Hides the lowest-priority widgets until everything fits in the bar's width.</summary>
    void Fit()
    {
        _fitQueued = false;
        var all = _left.Concat(_right).ToList();
        foreach (var w in all) w.View.Visibility = w.Present ? Visibility.Visible : Visibility.Collapsed;
        double avail = ActualWidth > 0 ? ActualWidth : _mon.Right - _mon.Left;
        if (avail <= 0) return;
        double Need()
        {
            // the left and right parts each get the same share of what the centred clock leaves: the wider one decides
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            _leftPanel.Measure(inf); _rightPanel.Measure(inf); _centrePill?.Measure(inf);
            return 2 * Math.Max(_leftPanel.DesiredSize.Width, _rightPanel.DesiredSize.Width) + (_centrePill?.DesiredSize.Width ?? 160) + 24;
        }
        // first the active-app name gives up space (it is cut with "…"), then the least important widgets disappear
        _appName.Width = _cfg.AppNameWidth;
        while (Need() > avail && _appName.Width > 70) _appName.Width -= 10;
        var hidden = new List<string>();
        foreach (var w in all.Where(w => w.Present).OrderBy(w => w.Priority))
        {
            if (Need() <= avail) break;
            w.View.Visibility = Visibility.Collapsed; hidden.Add(w.Id);
        }
        string hid = string.Join(",", hidden);
        if (hid != _lastHidden) { _lastHidden = hid; Log.Write(hid.Length == 0 ? "top bar: every widget fits again" : $"top bar: no room for {hid} (bar {avail:0} px wide)"); }
    }
    string _lastHidden = "";
    int _fitTicks;   // while widgets are hidden for lack of room, look again every few seconds (the content changes after start: weather, meetings ...)

    FrameworkElement BuildUi()
    {
        _ctx = new BarContext(_cfg, Dispatcher, RequestFit, WithRealTaskbar);
        foreach (var w in new BarWidget?[] { Make(new WeatherWidget()), Make(new MeetingWidget()), Make(new AtlassianWidget()), Make(new GitHubWidget()) }) if (w != null) _left.Add(w);
        foreach (var w in new BarWidget?[] { Make(new VpnWidget()), Make(new MediaWidget()), Make(new WslWidget()), Make(new SystemLoadWidget()), Make(new ClipboardWidget()), Make(new ScreenshotWidget()), Make(new BellWidget()) }) if (w != null) _right.Add(w);
        foreach (var w in new BarWidget?[] { Make(new NetworkWidget()), Make(new VolumeWidget()) }) if (w != null) _inPill.Add(w);
        _widgetTimer.Tick += (_, _) => { if (_lastHidden.Length > 0 && ++_fitTicks % 3 == 0) RequestFit(); try { if (_cfg.ClockPanel || _cfg.ClockBadge) PanelData.Prefetch(_cfg); } catch (Exception e) { Log.Write("prefetch: " + e.GetType().Name); } foreach (var w in _left.Concat(_right).Concat(_inPill)) { try { w.Tick(); } catch (Exception e) { Log.Write($"widget {w.Id}: {e.Message}"); } } };
        SizeChanged += (_, _) => RequestFit();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left: Activities + active application
        var left = _leftPanel;
        left.Children.Add(Pill(Label("Activities"), _toggleOverview, 16));
        _appName.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255));
        _appName.FontSize = 13.5; _appName.FontFamily = new FontFamily("Segoe UI"); _appName.FontWeight = FontWeights.SemiBold; _appName.VerticalAlignment = VerticalAlignment.Center;
        _appName.Margin = new Thickness(12, 0, 8, 0); _appName.Width = _cfg.AppNameWidth; _appName.TextTrimming = TextTrimming.CharacterEllipsis;
        _appName.Cursor = Cursors.Hand; _appName.Background = Brushes.Transparent;
        _appName.MouseLeftButtonUp += (_, _) => Popover.Toggle(_appName, BuildAppMenu);
        left.Children.Add(_appName);
        foreach (var w in _left) left.Children.Add(w.View);
        Grid.SetColumn(left, 0); grid.Children.Add(left);

        // centre: clock + date; click opens the Windows calendar flyout (Win+N)
        _clock.Foreground = Brushes.White; _clock.FontSize = 13.5; _clock.FontFamily = new FontFamily("Segoe UI"); _clock.FontWeight = FontWeights.SemiBold; _clock.VerticalAlignment = VerticalAlignment.Center;
        var clockRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        clockRow.Children.Add(_clock);
        _badge.Child = _badgeText;
        if (_cfg.ClockBadge) clockRow.Children.Add(_badge);
        PanelData.Changed += UpdateBadge;
        FrameworkElement centre = null!;
        centre = Pill(clockRow, () => OpenClockPanel(centre), 16);
        _centrePill = centre;
        Grid.SetColumn(centre, 1); grid.Children.Add(centre);

        // right: battery + quick settings (Win+A)
        _battery.Foreground = Brushes.White; _battery.FontSize = 13; _battery.FontFamily = new FontFamily("Segoe UI"); _battery.FontWeight = FontWeights.SemiBold; _battery.VerticalAlignment = VerticalAlignment.Center;
        var gear = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var rightRow = new StackPanel { Orientation = Orientation.Horizontal };
        _battery.Margin = new Thickness(0, 0, 10, 0);
        foreach (var w in _inPill) rightRow.Children.Add(w.View);
        rightRow.Children.Add(_battery); rightRow.Children.Add(gear);
        FrameworkElement right = null!;
        right = Pill(rightRow, () => OpenQuickSettings(right), 14);
        right.Margin = new Thickness(2, 3, 8, 3);
        var rightHost = _rightPanel;
        foreach (var w in _right) rightHost.Children.Add(w.View);
        if (_cfg.TaskbarManaged)
        {
            var tb = Pill(Label("Taskbar", 13), ToggleTaskbar, 12);
            rightHost.Children.Add(tb);
        }
        rightHost.Children.Add(right);
        Grid.SetColumn(rightHost, 2); grid.Children.Add(rightHost);
        return grid;
    }

    void UpdateClock()
    {
        var n = DateTime.Now;
        string week = _cfg.ShowWeek ? $"  W{ISOWeek.GetWeekOfYear(n)}" : "";
        _clock.Text = n.ToString("ddd d MMM", CultureInfo.CurrentCulture) + week + n.ToString("   HH:mm", CultureInfo.CurrentCulture);
    }

    void UpdateBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) { _battery.Text = ""; return; }
        int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        _battery.Text = (plugged ? "⚡ " : "") + pct + "%";
    }

    internal static void SendWin(char key) => SendCombo(8, (byte)key);

    /// <summary>Presses a key combination (RegisterHotKey modifier flags: 1 Alt, 2 Ctrl, 4 Shift, 8 Win).</summary>
    internal static void SendCombo(uint mods, byte vk)
    {
        var m = new List<byte>();
        if ((mods & 2) != 0) m.Add(0x11);
        if ((mods & 1) != 0) m.Add(0x12);
        if ((mods & 4) != 0) m.Add(0x10);
        if ((mods & 8) != 0) m.Add(0x5B);
        foreach (var k in m) Native.keybd_event(k, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
        for (int i = m.Count - 1; i >= 0; i--) Native.keybd_event(m[i], 0, 2, UIntPtr.Zero);
    }

    // ---------- app menu (click on the active app's name) ----------

    string? _fgPath;
    IntPtr _fgHwnd;

    /// <summary>Full name, the app's windows (click = go there), and a few safe actions. Closing only sends the normal "close" request: unsaved work is still asked about.</summary>
    FrameworkElement BuildAppMenu()
    {
        var sp = new StackPanel { MinWidth = 340 };
        sp.Children.Add(BarUi.Txt(string.IsNullOrEmpty(_appName.Text) ? "No application" : _appName.Text, 15));
        if (_fgPath != null) sp.Children.Add(BarUi.Txt(Path.GetFileName(_fgPath), 11.5, BarUi.Dim, FontWeights.Normal));
        var wins = _fgPath == null ? new List<WinInfo>() : _cache.Get(out _).Where(w => string.Equals(w.ExePath, _fgPath, StringComparison.OrdinalIgnoreCase)).ToList();

        sp.Children.Add(new TextBlock { Text = wins.Count == 1 ? "1 window" : $"{wins.Count} windows", Foreground = BarUi.Dim, FontFamily = new FontFamily("Segoe UI"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
        foreach (var w in wins.Take(12))
        {
            var win = w;
            string title = string.IsNullOrWhiteSpace(w.Title) ? "(no title)" : (w.Title.Length > 58 ? w.Title[..57] + "…" : w.Title);
            var t = BarUi.Txt((w.Hwnd == _fgHwnd ? "● " : "   ") + title + (w.Minimized ? "   (minimized)" : ""), 13, Brushes.White, w.Hwnd == _fgHwnd ? FontWeights.SemiBold : FontWeights.Normal);
            var row = new Border { Child = t, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) =>
            {
                Popover.CloseAny();
                if (Native.IsIconic(win.Hwnd)) Native.ShowWindow(win.Hwnd, Native.SW_RESTORE);
                Native.ForceForeground(win.Hwnd);
            };
            sp.Children.Add(row);
        }
        if (wins.Count > 12) sp.Children.Add(BarUi.Txt($"…and {wins.Count - 12} more", 11.5, BarUi.Dim, FontWeights.Normal));

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (_fgHwnd != IntPtr.Zero && Native.IsWindow(_fgHwnd))
            buttons.Children.Add(BarUi.Button("Close this window", () => { Popover.CloseAny(); Native.PostMessage(_fgHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
        if (wins.Count > 1)
        {
            Border? all = null; bool armed = false; int n = wins.Count;
            all = BarUi.Button($"Close all {n} windows", () =>
            {
                if (!armed) { armed = true; ((TextBlock)all!.Child).Text = $"Click again to close all {n}"; all.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xC0, 0x1C, 0x28)); return; }
                Popover.CloseAny();
                foreach (var w in wins) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            });
            buttons.Children.Add(all);
        }
        if (_fgPath != null && File.Exists(_fgPath))
            buttons.Children.Add(BarUi.Button("Open file location", () => { Popover.CloseAny(); try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_fgPath}\"") { UseShellExecute = true }); } catch { } }));
        sp.Children.Add(buttons);
        return sp;
    }

    // ---------- quick settings (status group) ----------

    QuickSettingsPanel? _qs;

    void OpenQuickSettings(FrameworkElement anchor)
    {
        if (!_cfg.QuickSettings.Enabled) { SendWin('A'); return; }
        Popover.Toggle(anchor, () => { _qs = new QuickSettingsPanel(_ctx); return _qs.Root; }, () => { _qs?.Stop(); _qs = null; }, alignRight: true);
    }

    // ---------- clock panel ----------

    void OpenClockPanel(FrameworkElement anchor)
    {
        if (!_cfg.ClockPanel) { SendWin('N'); return; }
        Popover.Toggle(anchor, () => { _panel = new ClockPanel(_ctx); return _panel.Root; }, () => { _panel?.Stop(); _panel = null; }, center: true);
    }

    /// <summary>The orange number next to the clock = unread mails in Outlook. Kept up to date by <see cref="PanelData"/> in the background.</summary>
    void UpdateBadge()
    {
        int n = PanelData.Unread;
        _badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _badgeText.Text = n > 99 ? "99+" : n.ToString();
        _badge.ToolTip = n > 0 ? $"{n} unread mail in Outlook" : null;
    }

    // ---------- real taskbar on demand ----------

    /// <summary>Taskbar on screen -> work on a worker thread -> taskbar away again (unless the work failed: then it stays so you can do it by hand).</summary>
    void WithRealTaskbar(Func<bool> work, string? keepWhileForeground)
    {
        if (!_cfg.TaskbarManaged) { System.Threading.Tasks.Task.Run(work); return; }
        Native.GetCursorPos(out var origin);   // revealing the taskbar moves the pointer to the screen edge: put it back afterwards
        if (!TaskbarControl.Revealed) ToggleTaskbar();
        System.Threading.Tasks.Task.Run(() =>
        {
            System.Threading.Thread.Sleep(700);   // let the taskbar slide in and build its tray
            bool ok = false;
            try { ok = work(); } catch (Exception e) { Log.Write("taskbar work failed: " + e.Message); }
            if (ok && keepWhileForeground != null)
            {
                // the program's own window must not lose focus because the taskbar leaves: wait until it is no longer in front (max 60 s)
                System.Threading.Thread.Sleep(1200);
                long until = Environment.TickCount64 + 60_000;
                while (Environment.TickCount64 < until)
                {
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
                    string n = "";
                    try { n = Process.GetProcessById((int)pid).ProcessName; } catch { }
                    if (!n.Equals(keepWhileForeground, StringComparison.OrdinalIgnoreCase)) break;
                    System.Threading.Thread.Sleep(400);
                }
            }
            if (ok) System.Threading.Thread.Sleep(350);   // let the program's window appear first
            Native.SetCursorPos(origin.X, origin.Y);
            if (ok) Dispatcher.BeginInvoke(new Action(() => { if (TaskbarControl.Revealed) ToggleTaskbar(); }));
        });
    }

    readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromSeconds(45) };

    /// <summary>Shows the real taskbar (for tray icons / Start); click again, or wait 45 s, to put it away.</summary>
    void ToggleTaskbar()
    {
        bool on = !TaskbarControl.Revealed;
        TaskbarControl.SetRevealed(on);
        _revealTimer.Stop(); _outsideTimer.Stop();
        if (on) { _revealTimer.Start(); _wasDown = true; _outsideTimer.Start(); }
    }

    readonly DispatcherTimer _outsideTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    bool _wasDown;

    /// <summary>While the taskbar is out: a click outside it (and outside its menus/flyouts and our bar) puts it away.</summary>
    void CheckClickOutside()
    {
        if (!TaskbarControl.Revealed) { _outsideTimer.Stop(); return; }
        bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed) return;
        Native.GetCursorPos(out var p);
        var under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero) return;
        var root = Native.GetAncestor(under, 2 /*GA_ROOT*/);
        if (root == IntPtr.Zero) root = under;
        var cls = new StringBuilder(64); Native.GetClassName(root, cls, cls.Capacity);
        string c = cls.ToString();
        Native.GetWindowThreadProcessId(root, out uint pid);
        string procName = "";
        try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }
        bool taskbarish = c.StartsWith("Shell_", StringComparison.Ordinal) || c == "#32768" || c.Contains("Overflow") || c.Contains("XamlExplorerHostIsland")
                          || c == "Windows.UI.Core.CoreWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c == "NotifyIconOverflowWindow"
                          || string.Equals(procName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(procName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(procName, "SearchHost", StringComparison.OrdinalIgnoreCase);
        if (pid == _selfPid || taskbarish) return;
        _revealTimer.Stop(); _outsideTimer.Stop();
        TaskbarControl.SetRevealed(false);
    }

    // ---------- tray strip (no longer used: real menus are not reachable, see RealTray) ----------

    void RefreshTray()
    {
        if (_trayBusy) return;
        _trayBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var entries = TrayRegistry.ReadRunning(_cfg.TrayExclude);
                string sig = string.Join("|", entries.Select(e => e.ExePath + "~" + e.Tooltip + "~" + e.Promoted));
                Dispatcher.BeginInvoke(new Action(() => { if (sig != _traySig) { _traySig = sig; RebuildTray(entries); } }));
            }
            catch (Exception e) { Log.Write("tray refresh: " + e.Message); }
            finally { _trayBusy = false; }
        });
    }

    void RebuildTray(List<TrayEntry> entries)
    {
        _tray.Children.Clear();
        foreach (var en in entries)
        {
            var captured = en;
            var img = new Image { Source = en.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var cell = new Border { Width = 28, Margin = new Thickness(1, 3, 1, 3), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = img, Cursor = Cursors.Hand };
            cell.ToolTip = Describe(en.ExePath);   // the stored tooltip is only the first one the icon ever showed (stale), so just the app name
            cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            cell.MouseLeftButtonUp += (_, _) => ActivateTray(captured);
            _tray.Children.Add(cell);
        }
        if (entries.Count > 0) _tray.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8), Background = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)) });
    }

    /// <summary>
    /// Clicking an icon performs the click on the REAL tray icon through UI Automation, so the program opens its own menu/window
    /// (see <see cref="RealTray"/>). If that fails and the program has a window, the window is brought forward instead.
    /// </summary>
    void ActivateTray(TrayEntry en)
    {
        string described = Describe(en.ExePath);
        System.Threading.Tasks.Task.Run(() =>
        {
            string status = RealTray.Click(en, described);
            Log.Debug($"tray click on {Path.GetFileName(en.ExePath)}: {status}");
            if (!status.StartsWith("invoked", StringComparison.Ordinal))
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var w in _cache.Get(out _))
                        if (string.Equals(w.ExePath, en.ExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, Native.SW_RESTORE);
                            Native.ForceForeground(w.Hwnd);
                            return;
                        }
                }));
        });
    }

    // ---------- active application label ----------

    string? ExePathOf(uint pid)
    {
        var hp = Native.OpenProcess(0x1000, false, pid);
        if (hp == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return Native.QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null; }
        finally { Native.CloseHandle(hp); }
    }

    string Describe(string path)
    {
        if (_names.TryGetValue(path, out var n)) return n;
        try { n = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(n)) n = Path.GetFileNameWithoutExtension(path);
        return _names[path] = n!;
    }

    void OnForeground(IntPtr h)
    {
        try
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == _selfPid || pid == 0) return;          // the overview itself does not change the label
            var path = ExePathOf(pid);
            if (path != null) { _appName.Text = Describe(path); _appName.ToolTip = _appName.Text; _fgPath = path; _fgHwnd = h; }
        }
        catch { }
        foreach (var w in _right) { try { w.OnForeground(); } catch { } }
    }

    // ---------- AppBar registration ----------

    public void Init()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _callbackMsg = Native.RegisterWindowMessage("SpreadAppBarCallback");
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");

        UpdateClock(); UpdateBattery(); _timer.Start(); _widgetTimer.Start();
        if (_cfg.TaskbarManaged)
        {
            if (HotkeyParser.TryParse(_cfg.TaskbarHotkey, out uint mods, out uint vk) && !Native.RegisterHotKey(_hwnd, 0x5351, mods | 0x4000, vk))
                Log.Write($"taskbar hotkey '{_cfg.TaskbarHotkey}' could not be registered (already in use?)");
            _revealTimer.Tick += (_, _) => { _revealTimer.Stop(); _outsideTimer.Stop(); TaskbarControl.SetRevealed(false); };
            _outsideTimer.Tick += (_, _) => CheckClickOutside();
        }
        _events.Foreground += OnForeground;
        OnForeground(Native.GetForegroundWindow());

        Show();
        RegisterAppBar();
    }

    void RegisterAppBar()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var hmon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*primary*/);
        Native.GetMonitorInfo(hmon, ref mi);
        _mon = mi.rcMonitor;
        uint dpi = 96;
        try { if (Native.GetDpiForMonitor(hmon, 0, out uint dx, out _) == 0) dpi = dx; } catch { }
        _heightPx = (int)Math.Round(_cfg.TopBarHeight * dpi / 96.0);

        if (!_registered)
        {
            var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callbackMsg };
            Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
            _registered = true;
        }
        SetPosition();
    }

    void SetPosition()
    {
        var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd, uEdge = Native.ABE_TOP };
        abd.rc = new RECT { Left = _mon.Left, Top = _mon.Top, Right = _mon.Right, Bottom = _mon.Top + _heightPx };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        abd.rc.Bottom = abd.rc.Top + _heightPx;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _debounce.Stop(); _debounce.Start();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)Native.WM_HOTKEY && wParam.ToInt32() == 0x5351) { ToggleTaskbar(); handled = true; }
        else if (msg == (int)_callbackMsg && _callbackMsg != 0)
        {
            switch (wParam.ToInt32())
            {
                case Native.ABN_FULLSCREENAPP:   // a fullscreen app is in front: step behind it, come back afterwards
                    Native.SetWindowPos(_hwnd, lParam != IntPtr.Zero ? HWND_BOTTOM : Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    break;
                case Native.ABN_POSCHANGED:
                    SetPosition();
                    break;
            }
            handled = true;
        }
        else if (msg == (int)_taskbarCreatedMsg && _taskbarCreatedMsg != 0)   // Explorer restarted
        {
            _registered = false; RegisterAppBar();
            if (_cfg.TaskbarManaged) TaskbarControl.ReapplyIfNeeded();
        }
        else if (msg == 0x007E /*WM_DISPLAYCHANGE*/ || msg == 0x02E0 /*WM_DPICHANGED*/)
            Dispatcher.BeginInvoke(new Action(RegisterAppBar), DispatcherPriority.Background);
        else if (msg == 0x001A /*WM_SETTINGCHANGE*/ && wParam.ToInt32() == 0x2F /*SPI_SETWORKAREA*/)
        { _debounce.Stop(); _debounce.Start(); }
        return IntPtr.Zero;
    }

    /// <summary>Gives the reserved screen space back.</summary>
    public void Unregister()
    {
        try
        {
            _revealTimer.Stop(); Native.UnregisterHotKey(_hwnd, 0x5351); _widgetTimer.Stop();
            foreach (var w in _left.Concat(_right).Concat(_inPill)) { try { w.Stop(); } catch { } }
            _timer.Stop(); _trayTimer.Stop(); _events.Foreground -= OnForeground;
            if (_registered)
            {
                var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
                Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
                _registered = false;
            }
        }
        catch { }
    }
}

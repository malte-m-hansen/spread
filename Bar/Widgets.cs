using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Spread;

// ---------------- weather ----------------

internal sealed class WeatherWidget : BarWidget
{
    public override string Id => "weather";
    public override int Priority => 8;
    readonly TextBlock _txt = new() { VerticalAlignment = VerticalAlignment.Center };
    WeatherData? _data;
    bool _busy;
    long _due;

    protected override void Build()
    {
        View = TopBarWindow.Pill(_txt, () => Popover.Toggle(View, BuildPopover), 10);
        _data = WeatherProbe.LoadCache();
        Show();
        SetPresent(_data != null);
    }

    void Show()
    {
        _txt.Inlines.Clear();
        if (_data == null) return;
        _txt.Inlines.Add(new Run(WeatherProbe.Glyph(_data.Code, _data.IsDay)) { FontFamily = BarUi.Emoji, FontSize = 14, Foreground = Brushes.White });
        _txt.Inlines.Add(new Run($" {Math.Round(_data.Temp):0}°") { FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        View.ToolTip = $"{Ctx.Cfg.WeatherLocation.Name}: {WeatherProbe.Describe(_data.Code)}, wind {_data.Wind:0} km/h";
    }

    public override void Tick()
    {
        if (_busy || Environment.TickCount64 < _due) return;
        _busy = true;
        _due = Environment.TickCount64 + 20 * 60_000;
        var loc = Ctx.Cfg.WeatherLocation;
        _ = Task.Run(async () =>
        {
            WeatherData? d = null;
            try { d = await WeatherProbe.FetchAsync(loc); } catch (Exception e) { Log.Write("weather fetch failed: " + e.GetType().Name); }
            _ = Ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                if (d == null) { _due = Environment.TickCount64 + 2 * 60_000; return; }   // try again soon
                _data = d; Show(); SetPresent(true); Ctx.Changed();
            }));
        });
    }

    FrameworkElement BuildPopover()
    {
        var sp = new StackPanel();
        if (_data == null) { sp.Children.Add(BarUi.Txt("No weather data yet")); return sp; }
        sp.Children.Add(BarUi.Txt($"{Ctx.Cfg.WeatherLocation.Name}   {Math.Round(_data.Temp):0}°  {WeatherProbe.Describe(_data.Code)}", 15));
        sp.Children.Add(BarUi.Row("Wind", $"{_data.Wind:0} km/h", true));
        var now = DateTime.Now;
        var hours = _data.Hours.Where(h => h.Time >= now.Date.AddHours(now.Hour)).Take(12).Where((h, i) => i % 2 == 0);
        sp.Children.Add(new Border { Height = 8 });
        foreach (var h in hours) sp.Children.Add(BarUi.Row($"{h.Time:HH:mm}", $"{WeatherProbe.Glyph(h.Code, WeatherProbe.IsDayAt(_data, h.Time))} {Math.Round(h.Temp):0}°"));
        sp.Children.Add(new Border { Height = 8 });
        foreach (var d in _data.Days.Skip(1).Take(3)) sp.Children.Add(BarUi.Row($"{d.Date:ddd d MMM}", $"{WeatherProbe.Glyph(d.Code, true)} {Math.Round(d.Max):0}° / {Math.Round(d.Min):0}°"));
        return sp;
    }
}

// ---------------- next meeting ----------------

internal sealed class MeetingWidget : BarWidget
{
    public override string Id => "meeting";
    public override int Priority => 7;
    readonly TextBlock _txt = BarUi.Txt("", 13);
    List<Meeting> _list = new();
    bool _busy, _logged;
    long _due;

    protected override void Build() { View = TopBarWindow.Pill(_txt, () => Popover.Toggle(View, BuildPopover), 10); Present = false; }

    public override void Tick()
    {
        Refresh();
        if (_busy || Environment.TickCount64 < _due) return;
        _busy = true; _due = Environment.TickCount64 + 120_000;
        var t = new Thread(() =>
        {
            var (list, status) = OutlookProbe.Read(8);
            Ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                if (list == null) { if (!_logged) { Log.Write("meeting widget: " + status); _logged = true; } _list = new(); }
                else { _list = list; _logged = false; }
                Refresh();
            }));
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    Meeting? Next() => _list.FirstOrDefault(m => m.End > DateTime.Now);

    void Refresh()
    {
        var m = Next();
        if (m == null) { SetPresent(false); return; }
        var now = DateTime.Now;
        string when = m.Start <= now ? "now" : (m.Start - now).TotalMinutes < 60 ? $"in {Math.Ceiling((m.Start - now).TotalMinutes):0} min" : $"at {m.Start:HH:mm}";
        int max = Ctx.Cfg.MeetingTitleMax;
        string title = max > 0 && m.Title.Length > max ? m.Title[..Math.Max(1, max - 1)].TrimEnd() + "…" : m.Title;
        _txt.Text = $"{title} · {when}";
        View.ToolTip = string.IsNullOrWhiteSpace(m.Location) ? m.Title : m.Title + Environment.NewLine + m.Location;
        SetPresent(true);
    }

    FrameworkElement BuildPopover()
    {
        var sp = new StackPanel();
        var upcoming = _list.Where(m => m.End > DateTime.Now).Take(4).ToList();
        if (upcoming.Count == 0) sp.Children.Add(BarUi.Txt("Nothing in the next 8 hours"));
        foreach (var m in upcoming)
        {
            var title = m.Title.Length > 40 ? m.Title[..39] + "…" : m.Title;
            sp.Children.Add(BarUi.Row(title, $"{m.Start:HH:mm}–{m.End:HH:mm}"));
            if (m.JoinUrl != null)
            {
                var url = m.JoinUrl;
                sp.Children.Add(BarUi.Button("Join", () => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } Popover.CloseAny(); }));
            }
        }
        return sp;
    }
}

// ---------------- system load ----------------

internal sealed class SystemLoadWidget : BarWidget
{
    public override string Id => "load";
    public override int Priority => 6;
    readonly TextBlock _txt = new() { FontSize = 13, FontFamily = BarUi.Text, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left, Width = 96 };
    readonly List<double> _hist = new();

    protected override void Build() { View = TopBarWindow.Pill(_txt, () => Popover.Toggle(View, BuildPopover), 10); }

    public override void Tick()
    {
        if (!Every(2000)) return;
        var (cpu, used, total) = LoadProbe.Read();
        _hist.Add(cpu); if (_hist.Count > 60) _hist.RemoveAt(0);
        _txt.Text = $"{cpu:0}%  {used:0.0} GB";
        bool hot = cpu > 85 || used / total > 0.92;
        _txt.Foreground = hot ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : Brushes.White;
        View.ToolTip = $"CPU {cpu:0}%, memory {used:0.0} of {total:0.0} GB";
    }

    FrameworkElement BuildPopover()
    {
        var sp = new StackPanel();
        var (cpu, used, total) = LoadProbe.Read();
        sp.Children.Add(BarUi.Row("CPU", $"{(_hist.Count > 0 ? _hist[^1] : cpu):0}%"));
        sp.Children.Add(BarUi.Row("Memory", $"{used:0.0} / {total:0.0} GB"));
        var canvas = new Canvas { Width = 220, Height = 44, Margin = new Thickness(0, 8, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)) };
        var pl = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)), StrokeThickness = 1.6 };
        for (int i = 0; i < _hist.Count; i++) pl.Points.Add(new Point(i * (220.0 / 59), 44 - 42 * _hist[i] / 100.0 - 1));
        canvas.Children.Add(pl);
        sp.Children.Add(canvas);
        sp.Children.Add(BarUi.Txt("CPU, last 2 minutes", 11, BarUi.Dim, FontWeights.Normal));
        return sp;
    }
}

// ---------------- keyboard layout ----------------

internal sealed class KeyboardWidget : BarWidget
{
    public override string Id => "keyboard";
    public override int Priority => 4;
    readonly TextBlock _txt = BarUi.Txt("", 13);

    protected override void Build()
    {
        View = TopBarWindow.Pill(_txt, () => TopBarWindow.SendWin(' '), 9);
        View.ToolTip = "Keyboard layout (click: next, Win+Space)";
        Present = KeyboardProbe.InstalledCount() > 1;
        Update();
    }

    void Update() => _txt.Text = KeyboardProbe.Current();
    public override void OnForeground() => Update();
    public override void Tick() { if (Every(2000)) Update(); }
}

// ---------------- clipboard history ----------------

internal sealed class ClipboardWidget : BarWidget
{
    public override string Id => "clipboard";
    public override int Priority => 3;

    protected override void Build()
    {
        var t = new TextBlock { Text = "📋", FontFamily = BarUi.Emoji, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        View = TopBarWindow.Pill(t, Click, 8);
        View.ToolTip = "Clipboard history";
    }

    void Click()
    {
        bool ditto = false;
        try { var ps = Process.GetProcessesByName("Ditto"); ditto = ps.Length > 0; foreach (var p in ps) p.Dispose(); } catch { }
        if (ditto && HotkeyParser.TryParse(Ctx.Cfg.ClipboardHotkey, out uint mods, out uint vk)) TopBarWindow.SendCombo(mods, (byte)vk);
        else TopBarWindow.SendWin('V');
    }
}

// ---------------- notifications ----------------

internal sealed class BellWidget : BarWidget
{
    public override string Id => "bell";
    public override int Priority => 9;
    readonly TextBlock _t = new() { FontFamily = BarUi.Emoji, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };

    protected override void Build()
    {
        var pill = (Border)TopBarWindow.Pill(_t, () => TopBarWindow.SendWin('N'), 8);
        pill.MouseRightButtonUp += (_, _) => { DndProbe.Set(!DndProbe.ToastsEnabled()); Update(); };
        View = pill;
        Update();
    }

    void Update()
    {
        bool on = DndProbe.ToastsEnabled();
        _t.Text = on ? "🔔" : "🔕";
        View.ToolTip = on ? "Notifications on. Click: notification centre. Right-click: turn notifications off" : "Notifications OFF. Right-click: turn them on";
    }

    public override void Tick() { if (Every(5000)) Update(); }
}

// ---------------- WSL ----------------

internal sealed class WslWidget : BarWidget
{
    public override string Id => "wsl";
    public override int Priority => 2;
    readonly TextBlock _txt = new() { FontSize = 13, FontFamily = BarUi.Text, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 74 };
    bool _busy; long _busySince;

    protected override void Build() { View = TopBarWindow.Pill(_txt, () => Popover.Toggle(View, BuildPopover), 10); Present = false; }

    public override void Tick()
    {
        if (_busy && Environment.TickCount64 - _busySince > 30_000) { _busy = false; Log.Write("wsl probe did not answer for 30 s, trying again"); }
        if (_busy || !Every(10_000)) return;
        _busy = true; _busySince = Environment.TickCount64;
        Task.Run(() =>
        {
            (bool running, long bytes) r = (false, 0);
            try { r = WslProbe.Read(); }
            catch (Exception e) { Log.Write("wsl probe failed: " + e.GetType().Name); }
            Ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                _txt.Text = r.bytes > 0 ? $"WSL {r.bytes / 1073741824.0:0.0} GB" : "WSL";
                if (r.running != Present) Log.Write(r.running ? "wsl: running, widget shown" : "wsl: stopped, widget hidden");
                SetPresent(r.running);
            }));
        });
    }

    FrameworkElement BuildPopover()
    {
        var sp = new StackPanel();
        sp.Children.Add(BarUi.Txt("WSL is running", 14));
        var host = new StackPanel();
        host.Children.Add(BarUi.Txt("looking up distributions…", 12, BarUi.Dim, FontWeights.Normal));
        sp.Children.Add(host);
        Task.Run(() =>
        {
            var d = WslProbe.RunningDistros();
            Ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                host.Children.Clear();
                if (d.Length == 0) host.Children.Add(BarUi.Txt("no distribution is running (the VM is idling)", 12, BarUi.Dim, FontWeights.Normal));
                foreach (var x in d) host.Children.Add(BarUi.Row(x, "running"));
            }));
        });
        sp.Children.Add(BarUi.Button("Shut down WSL", () =>
        {
            Popover.CloseAny();
            var r = MessageBox.Show("Shut down WSL now? Running containers and Linux programs are stopped.", "Spread", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) WslProbe.Shutdown();
        }));
        return sp;
    }
}

// ---------------- now playing ----------------

internal sealed class MediaWidget : BarWidget
{
    public override string Id => "media";
    public override int Priority => 5;
    readonly TextBlock _txt = BarUi.Txt("", 13);
    MediaInfo? _info;
    bool _busy;

    protected override void Build() { View = TopBarWindow.Pill(_txt, () => Popover.Toggle(View, BuildPopover), 10); Present = false; }

    public override void Tick()
    {
        if (_busy || !Every(2000)) return;
        _busy = true;
        Task.Run(() =>
        {
            var i = MediaProbe.Read();
            Ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false; _info = i;
                if (i != null)
                {
                    string s = string.IsNullOrWhiteSpace(i.Artist) ? i.Title : i.Artist + " – " + i.Title;
                    _txt.Text = (i.Playing ? "♪ " : "⏸ ") + (s.Length > 36 ? s[..35] + "…" : s);
                    _txt.Opacity = i.Playing ? 1 : 0.65;
                }
                SetPresent(i != null);
            }));
        });
    }

    FrameworkElement BuildPopover()
    {
        var sp = new StackPanel();
        if (_info == null) { sp.Children.Add(BarUi.Txt("Nothing is playing")); return sp; }
        sp.Children.Add(BarUi.Txt(_info.Title, 14));
        if (!string.IsNullOrWhiteSpace(_info.Artist)) sp.Children.Add(BarUi.Txt(_info.Artist, 12, BarUi.Dim, FontWeights.Normal));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BarUi.Button("⏮", () => MediaProbe.Command("prev")));
        row.Children.Add(BarUi.Button(_info.Playing ? "⏸ Pause" : "▶ Play", () => { MediaProbe.Command("toggle"); Popover.CloseAny(); }));
        row.Children.Add(BarUi.Button("⏭", () => MediaProbe.Command("next")));
        sp.Children.Add(row);
        return sp;
    }
}

// ---------------- network state, shared by the network and VPN widgets ----------------

/// <summary>
/// One network read every 5 s on a worker thread, shared by the network and the VPN widget. With GlobalProtect the read looks at the agent's
/// registry state, the tail of its multi-MB log and may do DNS lookups: that must not run on the UI thread. Results arrive on the UI thread.
/// </summary>
internal static class NetPoll
{
    static bool _busy, _failLogged;
    static long _due, _busySince;
    public static NetState? Last { get; private set; }
    public static event Action? Updated;

    /// <summary>Called from the widgets' Tick (UI thread): starts a read when one is due and none is running.</summary>
    public static void Poll(BarContext ctx)
    {
        long now = Environment.TickCount64;
        if (_busy && now - _busySince > 30_000) { _busy = false; Log.Write("network probe did not answer for 30 s, trying again"); }
        if (_busy || now < _due) return;
        _busy = true; _busySince = now; _due = now + 5000;
        string match = ctx.Cfg.VpnAdapterMatch;
        Task.Run(() =>
        {
            NetState? s = null;
            try { s = NetProbe.Read(match); }
            catch (Exception e) { if (!_failLogged) { _failLogged = true; Log.Write("network probe failed: " + e.GetType().Name); } }
            ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                if (s == null) return;
                _failLogged = false; Last = s;
                Updated?.Invoke();
            }));
        });
    }
}

// ---------------- network (inside the status pill) ----------------

internal sealed class NetworkWidget : BarWidget
{
    public override string Id => "network";
    readonly TextBlock _glyph = new() { FontFamily = BarUi.Icons, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Background = Brushes.Transparent, Text = "\uE701", Opacity = 0.35 };

    protected override void Build() { View = _glyph; NetPoll.Updated += Update; Update(); NetPoll.Poll(Ctx); }

    void Update()
    {
        var s = NetPoll.Last;
        if (s == null) return;   // the first read is still running
        _glyph.Text = s.Kind == "ethernet" ? "\uE839" : "\uE701";
        _glyph.Opacity = s.Kind == "none" ? 0.35 : 1;
        _glyph.ToolTip = s.Kind == "none" ? "No network" : (s.Kind == "wifi" ? "Wi-Fi: " : "Ethernet: ") + s.Name;
    }

    public override void Tick() => NetPoll.Poll(Ctx);
    public override void Stop() => NetPoll.Updated -= Update;
}

// ---------------- VPN (its own button, NOT part of the status pill) ----------------

internal sealed class VpnWidget : BarWidget
{
    public override string Id => "vpn";
    public override int Priority => 8;
    readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _text = BarUi.Txt("VPN", 12.5);
    string _last = "";

    protected override void Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot); row.Children.Add(_text);
        View = TopBarWindow.Pill(row, OpenPanel, 9);
        Present = !string.IsNullOrWhiteSpace(Ctx.Cfg.VpnAdapterMatch);
        _dot.Fill = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)); _text.Opacity = 0.55;   // until the first read is back
        NetPoll.Updated += Update;
        Update();
        NetPoll.Poll(Ctx);
    }

    void Update()
    {
        var s = NetPoll.Last;
        if (s == null) return;
        _dot.Fill = s.VpnUp ? new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)) : new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        _text.Opacity = s.VpnUp ? 1 : 0.55;
        View.ToolTip = (s.VpnUp ? $"VPN connected ({s.VpnName})" : $"VPN not connected{(s.VpnName.Length > 0 ? " (" + s.VpnName + ")" : "")}") + ". Click: open the GlobalProtect panel";
        if (s.VpnName != _last) { _last = s.VpnName; Log.Debug($"vpn: up={s.VpnUp} {s.VpnName}"); }
    }

    /// <summary>Same as clicking the GlobalProtect icon in the real tray: taskbar in, press that icon, taskbar away once the panel is closed.</summary>
    void OpenPanel() => Ctx.WithRealTaskbar(() =>
    {
        string r = RealTray.InvokeByKeys("GlobalProtect");
        Log.Write("vpn panel: " + r);
        return r.StartsWith("invoked", StringComparison.Ordinal);
    }, null);   // put the taskbar away right after the click so the bottom hot edge works while the panel is open

    public override void Tick() => NetPoll.Poll(Ctx);
    public override void Stop() => NetPoll.Updated -= Update;
}

// ---------------- volume (inside the status pill) ----------------

internal sealed class VolumeWidget : BarWidget
{
    public override string Id => "volume";
    readonly TextBlock _glyph = new() { FontFamily = BarUi.Icons, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Background = Brushes.Transparent };
    float _level; bool _mute;

    protected override void Build()
    {
        View = _glyph;
        _glyph.MouseWheel += (_, e) =>
        {
            if (!VolumeProbe.TryGet(out var l, out var _m1)) return;
            VolumeProbe.Set(l + (e.Delta > 0 ? 0.04f : -0.04f));
            Update(); e.Handled = true;
        };
        _glyph.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (VolumeProbe.TryGet(out var _l2, out var m)) VolumeProbe.SetMute(!m);
            Update(); e.Handled = true;
        };
        Update();
    }

    void Update()
    {
        if (!VolumeProbe.TryGet(out _level, out _mute)) { _glyph.Visibility = Visibility.Collapsed; return; }
        _glyph.Visibility = Visibility.Visible;
        _glyph.Text = _mute || _level <= 0.001f ? "" : "";
        _glyph.Opacity = _mute ? 0.5 : 1;
        _glyph.ToolTip = _mute ? "Muted (middle-click: unmute)" : $"Volume {Math.Round(_level * 100)}%  (scroll: change, middle-click: mute)";
    }

    public override void Tick() { if (Every(1000)) Update(); }
}

// ---------------- screenshot (Greenshot) ----------------

/// <summary>Sends the very same hotkey the user presses for Greenshot's region capture, so its whole flow (editor, save, ...) runs unchanged.</summary>
internal sealed class ScreenshotWidget : BarWidget
{
    public override string Id => "screenshot";
    public override int Priority => 3;

    protected override void Build()
    {
        var t = new TextBlock { Text = "📷", FontFamily = BarUi.Emoji, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        View = TopBarWindow.Pill(t, Click, 8);
        View.ToolTip = $"Screenshot of a region (Greenshot, {Ctx.Cfg.ScreenshotHotkey})";
        Present = false;
    }

    static bool Running()
    {
        try { var ps = Process.GetProcessesByName("Greenshot"); bool r = ps.Length > 0; foreach (var p in ps) p.Dispose(); return r; } catch { return false; }
    }

    public override void Tick() { if (Every(5000)) SetPresent(Running()); }

    void Click()
    {
        Popover.CloseAny();
        if (!HotkeyParser.TryParse(Ctx.Cfg.ScreenshotHotkey, out uint mods, out uint vk)) { Log.Write("screenshot hotkey is not valid: " + Ctx.Cfg.ScreenshotHotkey); return; }
        // let the mouse button come up and the pointer settle before the capture starts
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); TopBarWindow.SendCombo(mods, (byte)vk); };
        t.Start();
    }
}

// ---------------- Everything (file search) ----------------

/// <summary>Opens the search window of voidtools Everything (it runs as a single instance: starting it again without arguments raises its search window).</summary>
internal sealed class EverythingWidget : BarWidget
{
    public override string Id => "everything";
    public override int Priority => 6;
    string? _exe;

    protected override void Build()
    {
        var t = new TextBlock { Text = "🔍", FontFamily = BarUi.Emoji, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        View = TopBarWindow.Pill(t, Click, 8);
        View.ToolTip = "Everything (file search)";
        Present = Resolve();
    }

    bool Resolve()
    {
        string? found = null;
        try
        {
            foreach (var n in new[] { "Everything", "Everything64", "Everything1.5a" })
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { var path = p.MainModule?.FileName; if (!string.IsNullOrEmpty(path)) found ??= path; } catch { }
                    p.Dispose();
                }
        }
        catch { }
        if (found == null)
            foreach (var c in new[] { @"C:\Program Files\Everything\Everything.exe", @"C:\Program Files\Everything 1.5a\Everything64.exe", @"C:\Program Files (x86)\Everything\Everything.exe" })
                if (System.IO.File.Exists(c)) { found = c; break; }
        _exe = found ?? _exe;
        return _exe != null;
    }

    public override void Tick() { if (_exe == null && Every(15_000)) SetPresent(Resolve()); }

    void Click()
    {
        if (_exe == null && !Resolve()) return;
        try { Process.Start(new ProcessStartInfo(_exe!, "-toggle-window") { UseShellExecute = false }); }
        catch (Exception e) { Log.Write("everything: " + e.Message); }
    }
}

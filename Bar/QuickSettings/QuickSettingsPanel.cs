using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace Spread;

/// <summary>
/// Spread's own quick settings (GNOME style): battery + settings/lock/power, volume / microphone / brightness sliders with device pickers,
/// toggle tiles (Wi-Fi, Bluetooth, power mode, night light, dark style, do not disturb, airplane, keep awake, VPN) and shortcuts.
/// Opens at once and fills in; every tile hides itself when this PC cannot do it.
/// </summary>
internal sealed class QuickSettingsPanel
{
    readonly BarContext _ctx;
    readonly Config _cfg;
    readonly QuickSettingsConfig _q;
    public FrameworkElement Root { get; }
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _closed;

    // header
    readonly TextBlock _batMain = QsUi.Label("", 15, null, FontWeights.SemiBold), _batSub = QsUi.Label("", 12, QsUi.Faint);
    readonly FrameworkElement _batBox;
    readonly StackPanel _powerMenu = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    // sliders
    readonly SliderBar _vol = new(), _mic = new(), _bri = new();
    readonly Border _volBtn, _micBtn, _briBtn;
    readonly FrameworkElement _micRow, _briRow;
    readonly StackPanel _soundDetail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    string? _soundMode;
    bool _volMuted, _micMuted;

    // tiles
    readonly Dictionary<string, TogglePill> _tiles = new();
    readonly HashSet<string> _hidden = new();
    readonly UniformGrid _grid = new() { Columns = 2, Margin = new Thickness(-4, 12, -4, 0) };
    readonly StackPanel _detail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    string? _detailFor;
    bool _wifiOn, _btOn, _anyRadio;
    string _wifiName = "";

    public QuickSettingsPanel(BarContext ctx)
    {
        _ctx = ctx; _cfg = ctx.Cfg; _q = ctx.Cfg.QuickSettings;

        // ---------- header ----------
        var batText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        batText.Children.Add(_batMain); batText.Children.Add(_batSub);
        var bat = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bat.Children.Add(QsUi.Glyph("🔋", 22, true)); bat.Children.Add(batText);
        _batBox = bat;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var settingsBtn = QsUi.RoundButton("", () => OpenUri("ms-settings:"), 38, false, "Windows settings");
        var lockBtn = QsUi.RoundButton("", () => { Popover.CloseAny(); SessionCtl.Lock(); }, 38, false, "Lock");
        settingsBtn.Margin = new Thickness(0, 0, 8, 0); lockBtn.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(settingsBtn); buttons.Children.Add(lockBtn);
        if (_q.ShowPowerButtons)
        {
            var powerBtn = QsUi.RoundButton("", () => QsUi.SetOpen(_powerMenu, _powerMenu.Visibility != Visibility.Visible), 38, false, "Power");
            buttons.Children.Add(powerBtn);
        }
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        head.Children.Add(bat); head.Children.Add(buttons);
        BuildPowerMenu();

        // ---------- sliders ----------
        _volBtn = QsUi.RoundButton("", ToggleVolMute, 36, false, "Mute / unmute");
        _micBtn = QsUi.RoundButton("", ToggleMicMute, 36, false, "Mute / unmute the microphone");
        _briBtn = QsUi.RoundButton("", () => { }, 36, false, "Brightness");
        var volRow = SliderRow(_volBtn, _vol, () => ShowDevices(false));
        _micRow = SliderRow(_micBtn, _mic, () => ShowDevices(true));
        _briRow = SliderRow(_briBtn, _bri, null);
        _briRow.Visibility = Visibility.Collapsed; _micRow.Visibility = Visibility.Collapsed;
        _vol.Changed += v => { AudioCtl.SetLevel(false, (float)v); if (_volMuted && v > 0) { AudioCtl.SetMute(false, false); _volMuted = false; } UpdateVolIcon(v); };
        _mic.Changed += v => { AudioCtl.SetLevel(true, (float)v); if (_micMuted && v > 0) { AudioCtl.SetMute(true, false); _micMuted = false; } UpdateMicIcon(v); };
        _bri.Changed += v => BrightnessCtl.Set((int)Math.Round(Math.Max(_q.BrightnessMin, v * 100)));

        // ---------- tiles ----------
        Tile("wifi", "", false, "Wi-Fi", true, ToggleWifi, () => ToggleDetail("wifi"));
        Tile("bluetooth", "", false, "Bluetooth", true, ToggleBluetooth, () => ToggleDetail("bluetooth"));
        Tile("power", "⚡", true, "Power mode", true, () => ToggleDetail("power"), () => ToggleDetail("power"));
        Tile("night", "🌙", true, "Night light", false, () => OpenUri("ms-settings:nightlight"), null);
        Tile("dark", "◐", true, "Dark style", false, ToggleDark, null);
        Tile("dnd", "🔕", true, "Do not disturb", false, ToggleDnd, null);
        Tile("airplane", "", false, "Airplane mode", false, ToggleAirplane, null);
        Tile("awake", "☕", true, "Keep awake", false, ToggleAwake, null);
        Tile("vpn", "🛡", true, "VPN", false, OpenVpn, null);
        LayoutTiles();

        // ---------- footer ----------
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, act) in new (string, Action)[]
        {
            ("Network & internet", () => OpenUri("ms-settings:network")), ("Sound", () => OpenUri("ms-settings:sound")),
            ("Display", () => OpenUri("ms-settings:display")), ("Project (Win+P)", () => { Popover.CloseAny(); TopBarWindow.SendWin('P'); })
        })
        {
            var l = new Border { Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = QsUi.Label(text, 12, QsUi.Faint) };
            l.MouseEnter += (_, _) => l.Background = QsUi.Hover; l.MouseLeave += (_, _) => l.Background = Brushes.Transparent;
            var a = act; l.MouseLeftButtonUp += (_, _) => a();
            footer.Children.Add(l);
        }

        var spreadSection = BuildSpreadSection();
        var all = new StackPanel { Width = 392 };
        all.Children.Add(head); all.Children.Add(_powerMenu);
        all.Children.Add(Spacer(14));
        all.Children.Add(volRow); all.Children.Add(_micRow); all.Children.Add(_briRow);
        all.Children.Add(_soundDetail);
        all.Children.Add(_grid); all.Children.Add(_detail);
        all.Children.Add(spreadSection);
        all.Children.Add(footer);
        Root = all;

        UpdateBattery();
        RefreshAll();
        _timer.Tick += (_, _) => { UpdateSound(); UpdateBattery(); UpdateAwake(); };
        _timer.Start();
    }

    public void Stop() { _closed = true; _timer.Stop(); }

    static FrameworkElement Spacer(double h) => new Border { Height = h };

    FrameworkElement SliderRow(Border btn, SliderBar bar, Action? chevron)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(bar, 1);
        g.Children.Add(btn); g.Children.Add(bar);
        if (chevron != null)
        {
            var c = QsUi.RoundButton("", chevron, 32, false, "Choose device");
            Grid.SetColumn(c, 2); g.Children.Add(c);
        }
        else g.ColumnDefinitions[2].Width = new GridLength(32);
        return g;
    }

    // ---------------- tiles ----------------

    void Tile(string id, string glyph, bool emoji, string title, bool chevron, Action click, Action? chevClick)
    {
        var t = new TogglePill(glyph, emoji, title, chevron);
        t.Clicked += () => { try { click(); } catch (Exception e) { Log.Write($"tile {id}: {e.GetType().Name}"); } };
        if (chevClick != null) t.ChevronClicked += () => { try { chevClick(); } catch (Exception e) { Log.Write($"tile {id}: {e.GetType().Name}"); } };
        _tiles[id] = t;
    }

    void LayoutTiles()
    {
        _grid.Children.Clear();
        foreach (var id in _q.Tiles)
            if (_tiles.TryGetValue(id, out var t) && !_hidden.Contains(id)) _grid.Children.Add(t);
    }

    void SetAvailable(string id, bool available)
    {
        if (available) { if (_hidden.Remove(id)) LayoutTiles(); }
        else if (_hidden.Add(id)) LayoutTiles();
    }

    void UI(Action a) { if (!_closed) _ctx.Dispatcher.BeginInvoke(a); }

    void OpenUri(string uri)
    {
        Popover.CloseAny();
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // ---------------- refresh ----------------

    bool _briSeen;

    async void RefreshAll()
    {
        try
        {
            UpdateSound(); UpdateBattery();
            _tiles["dark"].IsOn = ThemeMode.IsDark(); _tiles["dark"].Subtitle = ThemeMode.IsDark() ? "On" : "Off";
            bool toasts = DndProbe.ToastsEnabled(); _tiles["dnd"].IsOn = !toasts; _tiles["dnd"].Subtitle = toasts ? "Off" : "On";
            UpdateAwake();
            UpdatePowerTile();
            _tiles["night"].Subtitle = "Open settings";
            SetAvailable("vpn", !string.IsNullOrWhiteSpace(_cfg.VpnAdapterMatch));
            BrightnessCtl.ReadAsync(v => UI(() => { if (v != null) { _briSeen = true; _briRow.Visibility = Visibility.Visible; if (!_bri.Dragging) _bri.Value = v.Value / 100.0; } else if (!_briSeen) _briRow.Visibility = Visibility.Collapsed; }));   // a failed read later does not hide a slider that worked
            _ = System.Threading.Tasks.Task.Run(() => NetProbe.Read(_cfg.VpnAdapterMatch)).ContinueWith(t => UI(() => { if (t.IsCompletedSuccessfully) { _tiles["vpn"].IsOn = t.Result.VpnUp; _tiles["vpn"].Subtitle = t.Result.VpnUp ? (t.Result.VpnName.StartsWith("GlobalProtect: ") ? t.Result.VpnName["GlobalProtect: ".Length..] : "Connected") : "Not connected"; } }));
            await RefreshRadios();
        }
        catch (Exception e) { Log.Write("quick settings refresh: " + e.GetType().Name); }
    }

    async System.Threading.Tasks.Task RefreshRadios()
    {
        var radios = await RadioCtl.ListAsync();
        var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
        var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        var nets = await System.Threading.Tasks.Task.Run(() => WifiCtl.Networks(false));
        UI(() =>
        {
            SetAvailable("wifi", wifi != null); SetAvailable("bluetooth", bt != null); SetAvailable("airplane", radios.Count > 0);
            _wifiOn = wifi?.State == RadioState.On; _btOn = bt?.State == RadioState.On; _anyRadio = radios.Any(r => r.State == RadioState.On);
            _wifiName = nets.FirstOrDefault(n => n.Connected)?.Ssid ?? "";
            _tiles["wifi"].IsOn = _wifiOn; _tiles["wifi"].Subtitle = !_wifiOn ? "Off" : _wifiName.Length > 0 ? _wifiName : "Not connected";
            _tiles["bluetooth"].IsOn = _btOn; _tiles["bluetooth"].Subtitle = _btOn ? "On" : "Off";
            _tiles["airplane"].IsOn = radios.Count > 0 && !_anyRadio; _tiles["airplane"].Subtitle = _anyRadio ? "Off" : "On";
        });
    }

    void UpdateSound()
    {
        if (AudioCtl.TryGet(false, out float v, out bool m))
        {
            _volMuted = m;
            if (!_vol.Dragging) _vol.Value = v;
            UpdateVolIcon(v);
        }
        bool hasMic = AudioCtl.TryGet(true, out float mv, out bool mm);
        _micRow.Visibility = hasMic ? Visibility.Visible : Visibility.Collapsed;
        if (hasMic) { _micMuted = mm; if (!_mic.Dragging) _mic.Value = mv; UpdateMicIcon(mv); }
    }

    void UpdateVolIcon(double v) => ((TextBlock)_volBtn.Child).Text = _volMuted || v <= 0.001 ? "" : "";
    void UpdateMicIcon(double v) => ((TextBlock)_micBtn.Child).Text = _micMuted || v <= 0.001 ? "" : "";

    void ToggleVolMute() { AudioCtl.SetMute(false, !_volMuted); UpdateSound(); }
    void ToggleMicMute() { AudioCtl.SetMute(true, !_micMuted); UpdateSound(); }

    void UpdateBattery()
    {
        var b = BatteryInfo.Read();
        _batBox.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b == null) return;
        _batMain.Text = $"{b.Value.pct} %";
        _batSub.Text = BatteryInfo.Describe(b.Value);
    }

    void UpdateAwake()
    {
        var t = _tiles["awake"];
        t.IsOn = KeepAwake.IsOn;
        t.Subtitle = !KeepAwake.IsOn ? "Off" : KeepAwake.Until != null ? $"until {KeepAwake.Until:HH:mm}" : "On";
    }

    void UpdatePowerTile()
    {
        var g = PowerMode.Get();
        SetAvailable("power", g != null);
        if (g == null) return;
        _tiles["power"].Subtitle = PowerMode.NameOf(g.Value);
        _tiles["power"].IsOn = g.Value != Guid.Empty;
    }

    // ---------------- actions ----------------

    // async void: an exception never reaches the tile's try/catch (it is raised on the dispatcher), so each one catches its own
    async void ToggleWifi()
    {
        try { bool on = !_wifiOn; _tiles["wifi"].IsOn = on; _tiles["wifi"].Subtitle = on ? "Turning on…" : "Turning off…"; await RadioCtl.SetAsync(RadioKind.WiFi, on); await RefreshRadios(); }
        catch (Exception e) { Log.Write("tile wifi: " + e.GetType().Name); }
    }
    async void ToggleBluetooth()
    {
        try { bool on = !_btOn; _tiles["bluetooth"].IsOn = on; await RadioCtl.SetAsync(RadioKind.Bluetooth, on); await RefreshRadios(); }
        catch (Exception e) { Log.Write("tile bluetooth: " + e.GetType().Name); }
    }
    async void ToggleAirplane()
    {
        try { bool turnRadiosOn = !_anyRadio; await RadioCtl.SetAllAsync(turnRadiosOn); await RefreshRadios(); }
        catch (Exception e) { Log.Write("tile airplane: " + e.GetType().Name); }
    }
    void ToggleDark() { bool dark = !ThemeMode.IsDark(); ThemeMode.SetDark(dark); _tiles["dark"].IsOn = dark; _tiles["dark"].Subtitle = dark ? "On" : "Off"; }
    void ToggleDnd() { bool toastsOn = DndProbe.ToastsEnabled(); DndProbe.Set(!toastsOn); _tiles["dnd"].IsOn = toastsOn; _tiles["dnd"].Subtitle = toastsOn ? "On" : "Off"; }
    void ToggleAwake() { KeepAwake.Set(!KeepAwake.IsOn, _q.KeepAwakeMinutes); UpdateAwake(); }

    void OpenVpn()
    {
        Popover.CloseAny();
        _ctx.WithRealTaskbar(() => RealTray.InvokeByKeys("GlobalProtect").StartsWith("invoked", StringComparison.Ordinal), null);
    }

    // ---------------- detail regions ----------------

    void ToggleDetail(string id)
    {
        if (_detailFor == id && _detail.Visibility == Visibility.Visible) { QsUi.SetOpen(_detail, false); _detailFor = null; return; }
        _detailFor = id;
        _detail.Children.Clear();
        _detail.Children.Add(QsUi.Label(id switch { "wifi" => "Wi-Fi networks", "bluetooth" => "Bluetooth devices", _ => "Power mode" }, 12, QsUi.Faint, FontWeights.SemiBold));
        if (id == "power") FillPower(); else if (id == "wifi") FillWifi(); else FillBluetooth();
        _detail.Visibility = Visibility.Collapsed;
        QsUi.SetOpen(_detail, true);
    }

    Border Row(string text, string sub, bool marked, Action? click, string? tail = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(QsUi.Label(marked ? "●" : "", 12, QsUi.Accent));
        var mid = new StackPanel();
        mid.Children.Add(QsUi.Label(text, 13.5));
        if (sub.Length > 0) mid.Children.Add(QsUi.Label(sub, 11.5, QsUi.Faint));
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        if (tail != null) { var t = QsUi.Label(tail, 12, QsUi.Faint); t.Margin = new Thickness(8, 0, 4, 0); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 2); g.Children.Add(t); }
        var b = new Border { Child = g, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = click != null ? Cursors.Hand : Cursors.Arrow };
        if (click != null) { b.MouseEnter += (_, _) => b.Background = QsUi.Hover; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent; b.MouseLeftButtonUp += (_, _) => click(); }
        return b;
    }

    void FillPower()
    {
        var cur = PowerMode.Get();
        foreach (var (id, name) in PowerMode.Modes)
        {
            var gid = id;
            _detail.Children.Add(Row(name, "", cur == id, () => { PowerMode.Set(gid); UpdatePowerTile(); ToggleDetail("power"); }));
        }
    }

    void FillWifi()
    {
        var host = new StackPanel(); _detail.Children.Add(host);
        host.Children.Add(QsUi.Label("Scanning…", 12.5, QsUi.Faint));
        System.Threading.Tasks.Task.Run(() => WifiCtl.Networks(true)).ContinueWith(t => UI(() =>
        {
            if (_detailFor != "wifi") return;
            host.Children.Clear();
            var nets = t.IsCompletedSuccessfully ? t.Result : new List<WifiNet>();
            if (nets.Count == 0) host.Children.Add(QsUi.Label(_wifiOn ? "No networks found." : "Wi-Fi is off.", 12.5, QsUi.Faint));
            foreach (var n in nets.Take(10))
            {
                var net = n;
                string bars = WifiCtl.Bars(n.Signal) switch { 4 => "▂▄▆█", 3 => "▂▄▆ ", 2 => "▂▄  ", 1 => "▂   ", _ => "    " };
                host.Children.Add(Row(n.Ssid, n.Connected ? "Connected" : n.Saved ? "Saved" : n.Secure ? "Secured - connect in Windows" : "Open", n.Connected,
                    n.Connected ? null : () =>
                    {
                        string profile = net.Profile.Length > 0 ? net.Profile : net.Ssid;
                        if (net.Saved) { System.Threading.Tasks.Task.Run(() => WifiCtl.Connect(profile)).ContinueWith(_ => { System.Threading.Thread.Sleep(2500); UI(() => _ = RefreshRadios()); }); }
                        else OpenUri("ms-settings:network-wifi");
                    }, bars));
            }
        }));
    }

    void FillBluetooth()
    {
        var host = new StackPanel(); _detail.Children.Add(host);
        host.Children.Add(QsUi.Label("Looking…", 12.5, QsUi.Faint));
        RadioCtl.BluetoothAsync().ContinueWith(t => UI(() =>
        {
            if (_detailFor != "bluetooth") return;
            host.Children.Clear();
            var devs = t.IsCompletedSuccessfully ? t.Result : new List<BtDevice>();
            if (devs.Count == 0) host.Children.Add(QsUi.Label("No paired devices.", 12.5, QsUi.Faint));
            foreach (var d in devs.Take(10)) host.Children.Add(Row(d.Name, d.Connected ? "Connected" : "Paired", d.Connected, () => OpenUri("ms-settings:bluetooth")));
            host.Children.Add(Row("Bluetooth settings…", "", false, () => OpenUri("ms-settings:bluetooth")));
        }));
    }

    void ShowDevices(bool capture)
    {
        string mode = capture ? "in" : "out";
        if (_soundMode == mode && _soundDetail.Visibility == Visibility.Visible) { QsUi.SetOpen(_soundDetail, false); _soundMode = null; return; }
        _soundMode = mode;
        _soundDetail.Children.Clear();
        _soundDetail.Children.Add(QsUi.Label(capture ? "Input device" : "Output device", 12, QsUi.Faint, FontWeights.SemiBold));
        foreach (var d in AudioCtl.List(capture))
        {
            var dev = d;
            _soundDetail.Children.Add(Row(d.Name, "", d.IsDefault, () => { AudioCtl.SetDefault(dev.Id); _soundMode = null; QsUi.SetOpen(_soundDetail, false); UpdateSound(); }));
        }
        _soundDetail.Visibility = Visibility.Collapsed;
        QsUi.SetOpen(_soundDetail, true);
    }

    // ---------------- Spread's own settings ----------------

    FrameworkElement BuildSpreadSection()
    {
        var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };

        // start with Windows (a switch)
        var auto = new TogglePill("\uE768", false, "Start with Windows", false) { Margin = new Thickness(0, 3, 0, 3), Height = 52 };
        auto.IsOn = Program.IsAutostart(); auto.Subtitle = auto.IsOn ? "On" : "Off";
        auto.Clicked += () => { bool on = !Program.IsAutostart(); Program.SetAutostart(on); auto.IsOn = on; auto.Subtitle = on ? "On" : "Off"; };
        body.Children.Add(auto);

        var verbose = new TogglePill("\uE9D9", false, "Detailed log", false) { Margin = new Thickness(0, 3, 0, 3), Height = 52 };
        verbose.IsOn = Log.IsVerbose; verbose.Subtitle = verbose.IsOn ? "On - log grows faster" : "Off - only errors and start/stop";
        verbose.Clicked += () =>
        {
            bool on = !Log.IsVerbose;
            Log.Level = on ? "verbose" : "normal"; _cfg.LogLevel = Log.Level; _cfg.Save();
            verbose.IsOn = on; verbose.Subtitle = on ? "On - log grows faster" : "Off - only errors and start/stop";
        };
        body.Children.Add(verbose);

        var dock = new TogglePill("▦", true, "Dock in the overview", false) { Margin = new Thickness(0, 3, 0, 3), Height = 52 };
        dock.IsOn = _cfg.Dock.Enabled; dock.Subtitle = dock.IsOn ? "On - from the next overview" : "Off";
        dock.Clicked += () => { bool on = !_cfg.Dock.Enabled; _cfg.Dock.Enabled = on; _cfg.Save(); dock.IsOn = on; dock.Subtitle = on ? "On - from the next overview" : "Off"; };
        body.Children.Add(dock);

        void Action(string text, string sub, Action act) => body.Children.Add(Row(text, sub, false, act));
        Action("Edit settings file", "config.json - restart Spread to apply", () => { Popover.CloseAny(); try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.FilePath}\"") { UseShellExecute = true }); } catch { } });
        Action("Open settings folder", "config, usage, log", () => { Popover.CloseAny(); try { Process.Start(new ProcessStartInfo("explorer.exe", Config.Dir) { UseShellExecute = true }); } catch { } });
        Action("Open the log", LogSizeText(), () => { Popover.CloseAny(); try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{System.IO.Path.Combine(Config.Dir, "spread.log")}\"") { UseShellExecute = true }); } catch { } });
        if (_cfg.TaskbarManaged) Action("Restore the Windows taskbar", "keep it visible until Spread starts again", () => { Popover.CloseAny(); TaskbarControl.Restore(force: true); });
        Action("Restart Spread", "applies changes made in the settings file", () => { Popover.CloseAny(); Program.Restart(); });
        body.Children.Add(Confirm("Quit Spread", true, Program.Quit));
        var ver = QsUi.Label($"Spread - settings v{_cfg.Version}", 11, QsUi.Faint);
        ver.Margin = new Thickness(8, 4, 0, 0);
        body.Children.Add(ver);

        var header = new Border { Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(18), Background = QsUi.TileOff, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 0, 0) };
        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hl = QsUi.Label("Spread settings", 13.5, null, FontWeights.SemiBold);
        var chev = QsUi.Glyph("\uE70D", 11); Grid.SetColumn(chev, 1);
        hg.Children.Add(hl); hg.Children.Add(chev);
        header.Child = hg;
        header.MouseEnter += (_, _) => header.Background = QsUi.TileOffHover;
        header.MouseLeave += (_, _) => header.Background = QsUi.TileOff;
        header.MouseLeftButtonUp += (_, _) => { bool open = body.Visibility != Visibility.Visible; QsUi.SetOpen(body, open); chev.Text = open ? "\uE70E" : "\uE70D"; };

        var sp = new StackPanel();
        sp.Children.Add(header); sp.Children.Add(body);
        return sp;
    }

    static string LogSizeText()
    {
        try { var f = new System.IO.FileInfo(Log.FilePath); return f.Exists ? $"spread.log - {f.Length / 1024} KB, rotates at {Log.MaxKb} KB" : "spread.log"; }
        catch { return "spread.log"; }
    }

    // ---------------- power menu ----------------

    void BuildPowerMenu()
    {
        _powerMenu.Children.Add(Confirm("Sleep", false, SessionCtl.Sleep));
        _powerMenu.Children.Add(Confirm("Sign out", true, SessionCtl.SignOut));
        _powerMenu.Children.Add(Confirm("Restart", true, SessionCtl.Restart));
        _powerMenu.Children.Add(Confirm("Shut down", true, SessionCtl.ShutDown));
    }

    /// <summary>First click asks, the second one (within 4 s) does it - unless ConfirmPowerActions is off.</summary>
    Border Confirm(string text, bool needsConfirm, Action action)
    {
        var label = QsUi.Label(text, 13.5);
        var b = new Border { Child = label, Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(12), Background = QsUi.TileOff, Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand };
        DateTime armedAt = DateTime.MinValue;
        b.MouseEnter += (_, _) => { if (armedAt == DateTime.MinValue) b.Background = QsUi.TileOffHover; };
        b.MouseLeave += (_, _) => { if (armedAt == DateTime.MinValue) b.Background = QsUi.TileOff; };
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (needsConfirm && _q.ConfirmPowerActions && (DateTime.Now - armedAt).TotalSeconds > 4)
            {
                armedAt = DateTime.Now; label.Text = $"Click again to {text.ToLowerInvariant()}"; b.Background = QsUi.Red;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (_, _) => { t.Stop(); armedAt = DateTime.MinValue; label.Text = text; b.Background = QsUi.TileOff; };
                t.Start();
                return;
            }
            Popover.CloseAny();
            action();
        };
        return b;
    }

    // ---------------- helpers for tests ----------------

    internal static int Bars(int quality) => WifiCtl.Bars(quality);
}

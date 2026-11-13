using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Spread;

/// <summary>
/// The HUD: a transparent window on top of the StageWindow that owns all UI and input and drives the
/// DWM thumbnails on the stage. Behaviour and constants follow GNOME Shell's window overview
/// (workspace.js / windowPreview.js / overviewControls.js); see the plan in the repo notes.
/// </summary>
internal sealed class OverlayWindow : Window
{
    // GNOME constants
    const double OpenMs = 250;            // Overview ANIMATION_TIME
    const double ReshuffleMs = 200;       // window slot changes
    const double OverlayFadeMs = 200;     // WINDOW_OVERLAY_FADE_TIME / WINDOW_SCALE_TIME
    const double OverlayIdleHideMs = 750; // WINDOW_OVERLAY_IDLE_HIDE_TIMEOUT
    const double RepositionDelayMs = 750; // WINDOW_REPOSITIONING_DELAY
    const double ActiveSizeInc = 5;       // WINDOW_ACTIVE_SIZE_INC
    const double IconSize = 64, IconOverlap = 0.7, IconTitleSpacing = 6;
    const double SearchFadeMs = 200;
    const double CloseFadeMs = 100;        // wallpaper fade-out AFTER the clones are home (nothing moves, so nothing can ghost)
    const double WallpaperRamp = 0.06;     // wallpaper is fully opaque by this much progress (hides the real windows)
    const int HotkeyId = 0x5350, GestureSelectId = 0x5352, GestureLeftId = 0x5353, GestureRightId = 0x5354;
    bool _gestureKeys, _gestureNav;
    readonly HashSet<string> _gestureKeyWarned = new(StringComparer.OrdinalIgnoreCase);

    enum State { Hidden, Opening, Open, Closing }

    const double RaiseDwellMs = 120, RaiseReleaseMs = 700;
    readonly List<StackInfo> _stacks = new();
    Item? _candidate;
    double _candidateSince;
    Point _mouseDip, _raisePos;

    sealed class Item
    {
        public WinInfo W = null!;
        public IntPtr Thumb;
        public double SrcW, SrcH;
        public bool SrcChecked; public double SrcSx = 1, SrcSy = 1;   // thumbnail source pixels per window-rect pixel (differs for DPI-scaled or resized-since-cached windows)
        public Rect Real;                 // visible frame, px, monitor-relative
        public Rect SlotFrom, SlotTo;     // slot animation (reshuffles)
        public double SlotT0;
        public int Row, Col;
        public bool Closing;              // close requested, waiting for the window to go away
        public bool OverlayShown;         // title + close button + hover scale
        public double Ov;                 // 0..1, eased when applied
        public double LeaveAt = -1;
        public Rect Cur, CurScaled;       // last drawn (px)
        public Image? Icon;
        public Grid? Title;
        public Button? Close;
        public Border? Card;
        public TranslateTransform? IconT, TitleT, CloseT;   // positioned via transforms: no layout pass per frame
        public MatrixTransform? CardT;
        public double TitleW = -1;
        public bool CloseHit;
        public Rect DrawFrom;                                // where a minimized window's card starts (it has no on-screen window)
        public Rect CardBase;                                // slot rect the card is laid out at, scaled by CardT
        public StackInfo? Stack;                             // photo stack this window belongs to
        public bool StackHidden;                             // deeper than the visible strips: faded out behind the last card
        public double LastVis;                               // last thumbnail opacity (to restore it after a re-registration)
    }

    /// <summary>Windows of one stackable app folded into a single layout box (see <see cref="StackLayout"/>).</summary>
    sealed class StackInfo
    {
        public string Key = "";
        public readonly List<Item> Members = new();     // most recently used first
        public Item? FrontItem;                          // the card in front (the wheel changes it)
        public Item? Raised;                             // a lower card brought forward by the pointer (z-order only)
        public double LeftAt = -1;
        public Border Badge = null!;
        public TextBlock BadgeText = null!;
        public TranslateTransform BadgeT = null!;
        public double ArmedUntil;                        // "Close all" confirmation window
        public double StripPx;                           // width of the strip a lower card shows (screen px)
        public List<Item> Live => Members.Where(m => !m.Closing).ToList();
        public Item? Shown => Raised is { Closing: false } r ? r : (FrontItem is { Closing: false } f ? f : Live.FirstOrDefault());
        public int Depth(Item it) { var l = Live; int n = l.Count, i = l.IndexOf(it), f = Math.Max(0, l.IndexOf(FrontItem!)); return i < 0 ? 0 : StackLayout.Depth(i, f, n); }
        /// <summary>Back to front: deepest first, the raised card last.</summary>
        public List<Item> ZOrdered()
        {
            var l = Live;
            var ordered = l.OrderByDescending(Depth).ToList();
            if (Raised != null && ordered.Remove(Raised)) ordered.Add(Raised);
            return ordered.Concat(Members.Where(m => m.Closing)).ToList();
        }
    }

    readonly Config _cfg;
    readonly AppCatalog _apps;
    readonly WindowCache _cache;
    readonly SnapshotCache _snaps;
    readonly StageWindow _stage = new();
    readonly Grid _root = new();
    readonly Canvas _canvas = new();
    readonly Border _bar = new();
    readonly TextBox _search = new();
    readonly TextBlock _hint = new();
    readonly Border _frame = new();
    readonly StackPanel _resultsPanel = new() { Orientation = Orientation.Vertical };
    readonly Border _resultsHost = new();
    readonly DockBar _dock = new();
    bool _pickerOpen;
    List<DockEntry> _dockEntries = new();
    bool _dockImportTried;
    readonly Dictionary<IntPtr, (uint pid, string? aumid)> _aumids = new();
    readonly List<Item> _items = new();
    readonly List<Border> _tiles = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    List<SearchItem> _results = new();                       // the single line of icons: apps, settings, tools
    readonly SearchEngine _engine;
    EverythingClient? _everything;
    readonly ScrollViewer _stripScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _stripPanel = new() { Orientation = Orientation.Horizontal };
    readonly Border _filesBox = new();
    readonly StackPanel _filesPanel = new();
    readonly TextBlock _keyHint = new() { Foreground = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)), FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(8, 8, 0, 0), IsHitTestVisible = false };
    readonly List<Border> _fileRows = new();
    List<EverythingHit> _files = new();
    int _fileTotal, _fileSel = -1, _fileQueryId;
    bool _hoverArmed;           // while searching, results react to the pointer only after it has really moved
    Point _searchMousePos;
    EverythingState _fileState = EverythingState.Unknown;
    string _fileText = "";
    readonly System.Windows.Threading.DispatcherTimer _fileTimer = new();

    IntPtr _hwnd;
    RECT _mon, _view;                 // monitor, and the part of it the overview covers
    string _source = "edge";
    bool SliverPark => _cfg.ParkMode.Equals("sliver", StringComparison.OrdinalIgnoreCase);
    bool MonitorMode => _cfg.OverlayArea.Equals("monitor", StringComparison.OrdinalIgnoreCase);
    double _scale = 1;
    State _state = State.Hidden;
    double _p, _pFrom, _pTo, _pT0;        // overview progress (GNOME's stateAdjustment)
    double _searchT, _searchTarget;       // 0 = window picker, 1 = search results
    double _lastTick;
    bool _ticking, _suppressText;
    int _sel = -1;
    Item? _hovered;
    DateTime _shownAt;
    double _repositionAt = -1;
    IntPtr _activateOnClose;
    int _columns = 6;

    // timing instrumentation (logged per open)
    long _trigTs;
    readonly Stopwatch _openSw = new();
    string _openTrace = "";
    bool _awaitFirstFrame;
    readonly System.Text.StringBuilder _trace = new();
    double _firstFrameAt;
    int _statFrames, _statSlow; double _statMax, _statSum, _statEnd, _tickSum, _tickMax;

    double Now => _clock.Elapsed.TotalMilliseconds;
    public bool IsOverlayVisible => _state != State.Hidden;

    public OverlayWindow(Config cfg, AppCatalog apps, WindowCache cache, SnapshotCache snaps, SearchEngine engine)
    {
        _cfg = cfg; _apps = apps; _cache = cache; _snaps = snaps; _engine = engine;
        Title = "Spread overview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        // Not a layered window: with ~11 megapixels (5120x2160) UpdateLayeredWindow copies ~44 MB per frame in
        // software. Per-pixel alpha comes from the DWM glass frame instead, like the StageWindow.
        Focusable = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));   // near-invisible but hit-testable
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1), CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000; Top = -20000; Width = 200; Height = 200;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        BuildUi();
    }

    // ---------- setup ----------

    public void Init()
    {
        _stage.Init();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var src = HwndSource.FromHwnd(_hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);
        _cache.Attach(_hwnd, () => _state != State.Hidden);
        _snaps.Attach(() => _state != State.Hidden);
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, _stage.Hwnd);   // owned window: always above the stage

        if (HotkeyParser.TryParse(_cfg.Hotkey, out uint mods, out uint vk) &&
            !Native.RegisterHotKey(_hwnd, HotkeyId, mods | 0x4000 /*NOREPEAT*/, vk))
            Log.Write($"hotkey '{_cfg.Hotkey}' could not be registered (already in use?)");

        ComputePark();
        ShowActivated = false; Show(); ShowActivated = true;
        ParkWindows();   // windows stay shown (surfaces stay allocated); opening is just a move onto the monitor
    }

    void ComputePark()
    {
        var pmi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*primary*/), ref pmi);
        var pv = MonitorMode ? pmi.rcMonitor : pmi.rcWork;
        int pw = pv.Width - (MonitorMode ? Math.Max(0, _cfg.OverlayInsetPx) : 0), ph = pv.Height;
        int px0 = SliverPark ? pmi.rcMonitor.Right - 1 : -30000, py0 = SliverPark ? pmi.rcMonitor.Bottom - 1 : -30000;
        _park = new RECT { Left = px0, Top = py0, Right = px0 + pw, Bottom = py0 + ph };
    }

    /// <summary>The usable area changed (top bar, taskbar, display): park with the new size so opening never has to resize.</summary>
    public void RefreshPark()
    {
        if (_state != State.Hidden) return;
        ComputePark();
        ParkWindows();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)Native.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyId) { Toggle(keyboard: true); handled = true; }
            else if (id == GestureSelectId) { GestureSelect(); handled = true; }
            else if (id == GestureLeftId) { GestureMove(-1); handled = true; }
            else if (id == GestureRightId) { GestureMove(1); handled = true; }
        }
        return IntPtr.Zero;
    }

    void BuildUi()
    {
        _root.Background = Brushes.Transparent;
        _root.Children.Add(_canvas);

        _frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x84, 0xE4));
        _frame.BorderThickness = new Thickness(3);
        _frame.CornerRadius = new CornerRadius(12);
        _frame.Visibility = Visibility.Collapsed;
        _frame.IsHitTestVisible = false;
        _canvas.Children.Add(_frame);

        _bar.Width = 640; _bar.Height = 56;
        _bar.CornerRadius = new CornerRadius(28);
        _bar.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        _bar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        _bar.BorderThickness = new Thickness(1);
        _bar.HorizontalAlignment = HorizontalAlignment.Center;
        _bar.VerticalAlignment = VerticalAlignment.Top;
        _bar.Margin = new Thickness(0, 36, 0, 0);
        var g = new Grid();
        _search.Background = Brushes.Transparent; _search.BorderThickness = new Thickness(0);
        _search.Foreground = Brushes.White; _search.CaretBrush = Brushes.White;
        _search.FontSize = 22; _search.FontFamily = new FontFamily("Segoe UI");
        _search.VerticalContentAlignment = VerticalAlignment.Center; _search.Padding = new Thickness(24, 0, 24, 0);
        _hint.Text = "Type to search";
        _hint.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
        _hint.FontSize = 20; _hint.FontFamily = new FontFamily("Segoe UI");
        _hint.VerticalAlignment = VerticalAlignment.Center; _hint.Margin = new Thickness(25, 0, 0, 0); _hint.IsHitTestVisible = false;
        g.Children.Add(_hint); g.Children.Add(_search);
        _bar.Child = g;
        _root.Children.Add(_bar);

        _stripScroll.Content = _stripPanel;
        _filesBox.Child = _filesPanel;
        _filesBox.Visibility = Visibility.Collapsed;
        _filesBox.Margin = new Thickness(0, 14, 0, 0); _filesBox.Padding = new Thickness(10, 8, 10, 8); _filesBox.CornerRadius = new CornerRadius(16);
        _filesBox.Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        _resultsPanel.Children.Add(_stripScroll); _resultsPanel.Children.Add(_filesBox); _resultsPanel.Children.Add(_keyHint);
        _resultsHost.Child = _resultsPanel;
        _fileTimer.Tick += (_, _) => { _fileTimer.Stop(); RunFileSearch(); };
        _resultsHost.VerticalAlignment = VerticalAlignment.Top; _resultsHost.HorizontalAlignment = HorizontalAlignment.Center;
        _resultsHost.Margin = new Thickness(0, 150, 0, 0);
        _resultsHost.Opacity = 0; _resultsHost.Visibility = Visibility.Collapsed;
        _root.Children.Add(_resultsHost);
        _dock.Margin = new Thickness(0, 0, 0, 18); _dock.Opacity = 0; _dock.Visibility = Visibility.Collapsed;
        _dock.Activated += DockActivate; _dock.ContextRequested += DockContext; _dock.AddRequested += DockAddPicker;
        _root.Children.Add(_dock);

        Content = _root;

        _search.TextChanged += OnTextChanged;
        PreviewKeyDown += OnKey;
        PreviewKeyDown += (_, _) => UpdateKeyHint();
        PreviewKeyUp += (_, _) => UpdateKeyHint();
        PreviewTextInput += OnTextInput;
        _root.MouseMove += OnMouseMove;
        _root.MouseDown += OnMouseDown;
        _root.PreviewMouseWheel += OnWheel;
        Activated += (_, _) =>
        {
            TraceMark("activated"); InputLog("window activated");
            if (_state == State.Hidden)   // activated while parked: it must not keep the keyboard
                Dispatcher.BeginInvoke(new Action(ReleaseKeyboardFromParked), System.Windows.Threading.DispatcherPriority.Background);
        };
        Deactivated += (_, _) =>
        {
            InputLog("window deactivated");
            if (_state is State.Opening or State.Open && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 500)
                Dispatcher.BeginInvoke(new Action(() => BeginClose(IntPtr.Zero)));
        };
    }

    // ---------- show / hide ----------

    public void Toggle(bool keyboard = false)
    {
        if (_state == State.Hidden) ShowOverlay(0, keyboard, keyboard ? "hotkey" : "tray");
        else if (_state is State.Opening or State.Open) BeginClose(IntPtr.Zero);
    }

    /// <summary>Called when the pointer approaches the hot edge: have the window list ready.</summary>
    public void Prewarm()
    {
        _snaps.Pause(1500);
        _cache.Prewarm();
        if (FilesEnabled) { _everything ??= new EverythingClient(_cfg.Search.Everything); _everything.Warm(); }   // the IPC thread is ready before the first keystroke
        Native.GetCursorPos(out var pt);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (Native.GetMonitorInfo(Native.MonitorFromPoint(pt, 2), ref mi)) Wallpaper.Refresh(mi.rcMonitor);   // decodes once, then cached
    }

    double SinceTriggerMs() => (Stopwatch.GetTimestamp() - _trigTs) * 1000.0 / Stopwatch.Frequency;
    void Mark(string name) => _openTrace += $"{name} {SinceTriggerMs():0.0} | ";

    RECT _park;                       // where the windows wait, far off-screen but still "shown"
    IntPtr _prevForeground;           // who had the keyboard before the overview opened
    Native.DWM_TIMING_INFO _dwm0; bool _dwm0ok;
    bool _focusedEarly, _firstMouseLogged;
    int _inputLogBudget;

    void InputLog(string what)
    {
        if (_inputLogBudget <= 0) return;
        _inputLogBudget--;
        Log.Debug($"input +{SinceTriggerMs():0} ms: {what}");
    }

    static readonly Dictionary<uint, string> ProcNames = new();

    static string FgName()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return "none";
        var cls = new System.Text.StringBuilder(64); Native.GetClassName(h, cls, cls.Capacity);
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (!ProcNames.TryGetValue(pid, out var n))
        {
            n = "";
            try { n = Process.GetProcessById((int)pid).ProcessName; } catch { }
            ProcNames[pid] = n;
        }
        return n + "/" + cls;
    }

    /// <param name="keyboard">Opened from the keyboard: take focus before the first frame so typing works at once.
    /// Otherwise (hot edge, tray) focus is taken when the animation ends: switching the foreground window stalls
    /// the compositor for ~80 ms, which is invisible once nothing moves but shows up as dropped frames mid-animation.</param>
    public void ShowOverlay(long triggerTimestamp = 0, bool keyboard = false, string source = "edge")
    {
        if (_state != State.Hidden) return;
        try
        {
            _source = source;
            _trigTs = triggerTimestamp != 0 ? triggerTimestamp : Stopwatch.GetTimestamp();
            _openTrace = "";
            _prevForeground = Native.GetForegroundWindow();
            if (_prevForeground == _hwnd || _prevForeground == _stage.Hwnd) _prevForeground = TopListableWindow();   // the parked overlay had stolen focus
            StartProbe();
            SetNoActivate(false);   // parked windows can't take focus; while open they can (TakeFocus)
            _dwm0ok = TryDwm(out _dwm0, out _);

            Native.GetCursorPos(out var pt);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var hmonTarget = Native.MonitorFromPoint(pt, 2);
            Native.GetMonitorInfo(hmonTarget, ref mi);
            _mon = mi.rcMonitor;
            _view = MonitorMode ? InsetView(mi.rcMonitor) : mi.rcWork;   // work area: the taskbar is left alone

            var all = _cache.Get(out bool cached);
            var wins = all.Where(w =>
            {
                int cx = (w.Bounds.Left + w.Bounds.Right) / 2, cy = (w.Bounds.Top + w.Bounds.Bottom) / 2;
                return cx >= _mon.Left && cx < _mon.Right && cy >= _mon.Top && cy < _mon.Bottom;
            }).ToList();
            Mark(cached ? "windows(cached)" : "windows(enumerated)");

            _suppressText = true; _search.Text = ""; _suppressText = false;
            _hint.Visibility = Visibility.Visible;
            _results = new List<SearchItem>(); ClearFiles(); RebuildResults();
            _gestureNav = false; _searchT = 0; _searchTarget = 0; _sel = -1; _hovered = null; _repositionAt = -1; _activateOnClose = IntPtr.Zero;
            ClearItems();

            uint dpi = 96;
            try { if (Native.GetDpiForMonitor(hmonTarget, 0, out uint dx, out _) == 0) dpi = dx; } catch { }
            _scale = Math.Max(1.0, dpi / 96.0);
            _columns = Math.Max(3, (int)Math.Min(8, (_mon.Width / _scale * 0.6) / 140));

            BuildItems(wins);
            Mark("items");
            RebuildDock();
            Relayout(animate: false);
            Mark("layout");

            _shownAt = DateTime.UtcNow;
            _state = State.Opening; _p = 0; _pFrom = 0; _pTo = 1; _awaitFirstFrame = true;   // the clock starts on the first frame
            SetGestureKeys(true);
            _stage.SetWallpaper(Wallpaper.Refresh(_mon), _mon, _view, _scale);
            _stage.SetBackdrop(_cfg.BackdropFadeMs <= 0 ? 1 : 0);
            // The clones must already sit exactly on top of the real windows when the opaque wallpaper appears. Their
            // properties used to be set only on the first animation frame (12-55 ms later): for those frames the real
            // windows were covered and no clone was visible yet, i.e. a flash of empty wallpaper.
            foreach (var it in _items) { it.Cur = it.Real; it.CurScaled = it.Real; UpdateThumb(it, it.Real, 1, true); }
            var view = _view;
            _stage.ShowAt(view);
            // Mouse-driven opens must not activate the overlay while it animates: WPF's Show() and SWP_SHOWWINDOW both
            // activate by default, and that foreground switch is what stalled the compositor a few frames in.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, view.Left, view.Top, view.Width, view.Height,
                Native.SWP_SHOWWINDOW | (keyboard ? 0u : Native.SWP_NOACTIVATE));
            _inputLogBudget = 12; _firstMouseLogged = false;
            InputLog("foreground at open: " + FgName());
            // Take the keyboard right away, for every kind of open: whatever you type the instant the overview appears goes into
            // the search box. (Mouse-driven opens used to take focus only when the animation ended, ~300 ms later, so early
            // keystrokes went to the app underneath.)
            _focusedEarly = true;
            Native.ForceForeground(_hwnd);
            Activate();
            _search.Focus(); Keyboard.Focus(_search);
            Mark("shown");
            _logCount = wins.Count;
            StartTicking();
        }
        catch (Exception e)
        {
            Log.Write("show failed: " + e);
            FinishHide();
        }
    }

    int _logCount;

    bool TryDwm(out Native.DWM_TIMING_INFO t, out int hr)
    {
        t = new Native.DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<Native.DWM_TIMING_INFO>() };
        hr = Native.DwmGetCompositionTimingInfo(_hwnd, ref t);
        if (hr != 0)
        {
            t = new Native.DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<Native.DWM_TIMING_INFO>() };
            int hr2 = Native.DwmGetCompositionTimingInfo(IntPtr.Zero, ref t);   // desktop-level timing
            if (hr2 == 0) hr = 0;
        }
        return hr == 0;
    }

    string DwmDelta()
    {
        if (!_dwm0ok) return "dwm n/a";
        if (!TryDwm(out var t, out int hr)) return $"dwm n/a (hr=0x{hr:X8})";
        double hz = t.rateRefreshDen == 0 ? 0 : t.rateRefreshNum / (double)t.rateRefreshDen;
        return $"dwm {hz:0.#} Hz: refreshes +{t.cRefresh - _dwm0.cRefresh}, composed +{t.cFrame - _dwm0.cFrame}, late +{t.cFramesLate - _dwm0.cFramesLate}, dropped +{t.cFramesDropped - _dwm0.cFramesDropped}, missed +{t.cFramesMissed - _dwm0.cFramesMissed}";
    }

    // Diagnostic: DwmFlush() returns once per compositor pass. If a gap shows up here, the *system compositor* stalled;
    // if the gaps are only in our frame list, the stall is in this process.
    readonly List<(double at, double gap)> _probe = new();
    volatile bool _probeRun;

    void StartProbe()
    {
        var list = _probe; lock (list) list.Clear();
        long t0 = _trigTs; _probeRun = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            long last = Stopwatch.GetTimestamp();
            while (_probeRun)
            {
                Native.DwmFlush();
                long now = Stopwatch.GetTimestamp();
                double gap = (now - last) * 1000.0 / Stopwatch.Frequency, at = (now - t0) * 1000.0 / Stopwatch.Frequency;
                last = now;
                lock (list) list.Add((at, gap));
                if (at > 700) break;
            }
        });
    }

    string ProbeSummary()
    {
        _probeRun = false;
        lock (_probe)
        {
            if (_probe.Count == 0) return "dwmflush n/a";
            var big = _probe.Where(p => p.gap > 25).Select(p => $"{p.gap:0}@{p.at:0}");
            return $"dwmflush: n={_probe.Count} avg={_probe.Average(p => p.gap):0.0} max={_probe.Max(p => p.gap):0} ms, gaps>25ms (gap@time since trigger): {string.Join(' ', big)}";
        }
    }

    void TraceMark(string name) { if (_statEnd > 0) _trace.Append($"[{name}@{Now - _firstFrameAt:0}] "); }

    /// <summary>Typing before the search box has focus (it is focused after the animation) still starts a search.</summary>
    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_state is State.Hidden or State.Closing || _search.IsKeyboardFocused || _pickerOpen) return;
        _search.Focus(); Keyboard.Focus(_search);
        _search.Text += e.Text;
        _search.CaretIndex = _search.Text.Length;
        e.Handled = true;
    }

    /// <summary>Runs right after the first rendered frame: take keyboard focus without delaying the animation.</summary>
    void TakeFocus()
    {
        if (_state is State.Hidden or State.Closing) return;
        var h = _hwnd;
        System.Threading.Tasks.Task.Run(() =>
        {
            Native.ForceForeground(h);   // Alt tap + AttachThreadInput can stall; keep it off the UI thread
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_state is State.Hidden or State.Closing) return;
                Activate();
                _search.Focus(); Keyboard.Focus(_search);
                TraceMark("focus");
            }));
        });
    }

    /// <summary>Starts the reverse animation; optionally activates a window first (so it is raised underneath).</summary>
    void BeginClose(IntPtr activate)
    {
        if (_state is State.Hidden or State.Closing) return;
        if (activate != IntPtr.Zero)
        {
            _activateOnClose = activate;   // activated after the animation: a foreground switch mid-motion drops frames
            RaiseForClose(activate);
        }
        _state = State.Closing;
        foreach (var it in _items) it.OverlayShown = false;   // clones end at exactly the real size
        _pFrom = _p; _pTo = 0; _pT0 = Now;
        StartTicking();
    }

    /// <summary>Immediate teardown (used on exit).</summary>
    public void HideOverlay() => FinishHide();

    /// <summary>
    /// GNOME raises the clicked window's clone at once. Thumbnails stack in registration order, so re-register that one
    /// (last = on top). The real window is raised too (no activation, hidden under the overview) so that when the overview
    /// goes away nothing pops to the front, and a minimized window is restored underneath.
    /// </summary>
    void RaiseForClose(IntPtr hwnd)
    {
        try
        {
            var it = _items.FirstOrDefault(i => i.W.Hwnd == hwnd);
            if (it != null)
            {
                if (it.Thumb != IntPtr.Zero)
                {
                    Native.DwmUnregisterThumbnail(it.Thumb); it.Thumb = IntPtr.Zero;
                    if (Native.DwmRegisterThumbnail(_stage.Hwnd, hwnd, out var th) == 0) { it.Thumb = th; UpdateThumb(it, it.CurScaled, 1, true); }
                }
                _items.Remove(it); _items.Add(it);   // topmost last, also for hit-testing
            }
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
            int before = NonTopmostAbove(hwnd);
            bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            InputLog($"raise real window: ok={ok}, non-topmost windows above: {before} -> {NonTopmostAbove(hwnd)}");
        }
        catch (Exception e) { Log.Write("raise for close: " + e.Message); }
    }

    /// <summary>Topmost normal window (z-order, minimized ones skipped), ignoring our own.</summary>
    IntPtr TopListableWindow()
    {
        try { foreach (var w in _cache.Get(out _)) if (!w.Minimized && w.Hwnd != _hwnd && w.Hwnd != _stage.Hwnd) return w.Hwnd; }
        catch { }
        return IntPtr.Zero;
    }

    void ReleaseKeyboardFromParked()
    {
        if (_state != State.Hidden || Native.GetForegroundWindow() != _hwnd) return;
        var top = TopListableWindow();
        Log.Debug("parked overlay was activated; keyboard handed back to the top window");
        if (top != IntPtr.Zero) Native.ForceForeground(top);
    }

    void FinishHide()
    {
        _snaps.Pause(1500);   // no PrintWindow captures while the desktop is being revealed
        _probeRun = false;
        StopTicking();
        _state = State.Hidden; _p = 0;
        SetGestureKeys(false);
        bool hadFocus = Native.GetForegroundWindow() == _hwnd;
        var target = _activateOnClose; _activateOnClose = IntPtr.Zero;

        // 1. Activate the chosen window while the overview still covers the desktop. Activation takes tens of ms (input
        //    attach, the app repainting); doing it after revealing the desktop showed the old stacking for that long and the
        //    window then "appeared suddenly" in front.
        bool activated = false;
        if (target != IntPtr.Zero && Native.IsWindow(target))
        {
            if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
            Native.SetForegroundWindow(target);   // we are the foreground process: no Alt-tap hack needed
            for (int i = 0; i < 8 && Native.GetForegroundWindow() != target; i++) System.Threading.Thread.Sleep(5);   // <= 40 ms
            if (Native.GetForegroundWindow() != target)
            {
                Native.ForceForeground(target);   // fallback with the input-attach trick
                for (int i = 0; i < 12 && Native.GetForegroundWindow() != target; i++) System.Threading.Thread.Sleep(5);   // <= 60 ms more
            }
            activated = Native.GetForegroundWindow() == target;
            InputLog($"activate before reveal: foreground={(activated ? "target" : "other")}, non-topmost windows above target: {NonTopmostAbove(target)}");
        }

        // 2. Now reveal the real windows (already in their final stacking), then drop the clones.
        try { ParkWindows(); } catch { }
        ClearItems();

        if (target == IntPtr.Zero && hadFocus && _prevForeground != IntPtr.Zero && Native.IsWindow(_prevForeground) && !Native.IsIconic(_prevForeground))
            Native.ForceForeground(_prevForeground);   // a parked (still shown) overlay must not keep the keyboard
    }

    /// <summary>How many visible, non-topmost top-level windows are above this one (0 = it is in front of all normal windows).</summary>
    static int NonTopmostAbove(IntPtr h)
    {
        int n = 0;
        for (var p = Native.GetWindow(h, 3 /*GW_HWNDPREV*/); p != IntPtr.Zero; p = Native.GetWindow(p, 3))
            if (Native.IsWindowVisible(p) && (Native.GetWindowLongPtr(p, Native.GWL_EXSTYLE).ToInt64() & 0x8 /*WS_EX_TOPMOST*/) == 0) n++;
        return n;
    }

    RECT InsetView(RECT mon) => new RECT { Left = mon.Left, Top = mon.Top, Right = mon.Right - Math.Max(0, _cfg.OverlayInsetPx), Bottom = mon.Bottom };

    void SetNoActivate(bool on)
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = on ? (ex | Native.WS_EX_NOACTIVATE) : (ex & ~Native.WS_EX_NOACTIVATE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    void ParkWindows()
    {
        if (SliverPark) _stage.SetBackdrop(0);   // a parked sliver must be fully transparent
        SetNoActivate(true);   // when the foreground window closes, Windows must not hand the keyboard to a parked overlay
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _park.Left, _park.Top, _park.Width, _park.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _stage.Park(_park);
    }

    void ClearItems()
    {
        foreach (var it in _items) if (it.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(it.Thumb);
        _items.Clear(); _stacks.Clear(); _candidate = null;
        _canvas.Children.Clear();
        _canvas.Children.Add(_frame);
        _frame.Visibility = Visibility.Collapsed;
    }

    // ---------- items ----------

    void BuildItems(List<WinInfo> wins)
    {
        // EnumWindows is top-to-bottom; DWM stacks thumbnails in registration order, so register bottom-to-top
        // to keep the real stacking (also makes the last item the topmost one for hit-testing).
        foreach (var w in Enumerable.Reverse(wins))
        {
            var it = new Item { W = w };
            it.Real = new Rect(w.Bounds.Left - _view.Left, w.Bounds.Top - _view.Top, w.W, w.H);
            it.SrcW = w.W; it.SrcH = w.H;
            if (!w.Minimized && !_cfg.DebugNoThumbnails && Native.DwmRegisterThumbnail(_stage.Hwnd, w.Hwnd, out var th) == 0) it.Thumb = th;

            // Icon (always visible), title and close button (shown with the overlay on hover/focus)
            it.Icon = new Image { Width = IconSize, Height = IconSize, Source = w.Icon, IsHitTestVisible = false, Opacity = 0 };
            RenderOptions.SetBitmapScalingMode(it.Icon, BitmapScalingMode.HighQuality);
            it.Title = BuildTitle(w.Title);
            it.Close = BuildCloseButton(it);
            // invisible until hovered: Collapsed elements cost nothing at open (no layout, no text shaping, no render data)
            it.Title.Visibility = Visibility.Collapsed; it.Close.Visibility = Visibility.Collapsed;
            it.IconT = new TranslateTransform(); it.Icon.RenderTransform = it.IconT;
            it.TitleT = new TranslateTransform(); it.Title.RenderTransform = it.TitleT;
            it.CloseT = new TranslateTransform(); it.Close.RenderTransform = it.CloseT;
            _canvas.Children.Add(it.Icon); _canvas.Children.Add(it.Title); _canvas.Children.Add(it.Close);

            if (it.Thumb == IntPtr.Zero)
            {
                // Minimized (or thumbnail unavailable): show the snapshot taken when it was minimized, else an icon card.
                var snap = _snaps.Get(w.Hwnd);
                if (snap != null)
                {
                    var brush = new ImageBrush(snap) { Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                    it.Card = new Border { Background = brush, CornerRadius = new CornerRadius(10), IsHitTestVisible = false, Opacity = 0 };
                    // dim slightly so it reads as "not live"
                    it.Card.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)), CornerRadius = new CornerRadius(10) };
                    var a = snap.PixelWidth / (double)snap.PixelHeight;
                    if (a > 0.2 && a < 8) { it.SrcW = it.Real.Width; it.SrcH = it.Real.Width / a; it.Real = new Rect(it.Real.X, it.Real.Y, it.SrcW, it.SrcH); }
                }
                else
                {
                    var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    if (w.Icon != null) inner.Children.Add(new Image { Source = w.Icon, Width = 48, Height = 48, Margin = new Thickness(0, 0, 0, 8) });
                    inner.Children.Add(new TextBlock
                    {
                        Text = w.Minimized ? "Minimized" : "Preview unavailable", Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)),
                        FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center
                    });
                    it.Card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), CornerRadius = new CornerRadius(10), Child = inner, IsHitTestVisible = false, Opacity = 0 };
                }
                it.CardT = new MatrixTransform(); it.Card.RenderTransform = it.CardT;
                _canvas.Children.Insert(1, it.Card);
            }
            _items.Add(it);
        }
        BuildStacks();
    }

    // ---------- touchpad gestures (as shortcut keys) ----------

    /// <summary>The gesture keys exist only while the overview is open, so a swipe in other programs does nothing special.</summary>
    void SetGestureKeys(bool on)
    {
        if (on == _gestureKeys || _hwnd == IntPtr.Zero) return;
        _gestureKeys = on;
        foreach (var (id, text) in new[] { (GestureSelectId, _cfg.GestureSelectHotkey), (GestureLeftId, _cfg.GestureLeftHotkey), (GestureRightId, _cfg.GestureRightHotkey) })
        {
            if (!on) { Native.UnregisterHotKey(_hwnd, id); continue; }
            if (string.IsNullOrWhiteSpace(text) || !HotkeyParser.TryParse(text, out uint mods, out uint vk)) continue;
            // registered at every open: say it once, not every time (e.g. graphics drivers own Ctrl+Alt+arrows)
            if (!Native.RegisterHotKey(_hwnd, id, mods | 0x4000, vk) && _gestureKeyWarned.Add(text)) Log.Write($"gesture key '{text}' could not be registered (already in use?)");
        }
    }

    /// <summary>Swipe down: open what the pointer is on (a window, or the selected search result after a left/right swipe); on empty space close the overview.</summary>
    void GestureSelect()
    {
        if (_state is not (State.Open or State.Opening)) return;
        if (SearchMode)
        {
            if (_fileSel >= 0) { if (_fileSel < _files.Count) OpenFile(_files[_fileSel], ModifierKeys.None); else ShowAllInEverything(); }
            else if (_sel >= 0 && _sel < _results.Count) OpenItem(_results[_sel], false);
            return;
        }
        var hit = HitItem(Mouse.GetPosition(_root));
        if (hit != null) { ActivateWindow(hit); return; }
        if (_gestureNav && SelectedItem() is { } sel) { ActivateWindow(sel); return; }
        BeginClose(IntPtr.Zero);
    }

    /// <summary>Swipe left/right: move the selection.</summary>
    void GestureMove(int dir)
    {
        if (_state is not (State.Open or State.Opening)) return;
        _gestureNav = true;
        if (SearchMode)
        {
            if (_fileSel < 0 && _results.Count > 0) { _sel = Math.Clamp(_sel + dir, 0, _results.Count - 1); UpdateSelection(); }
            return;
        }
        Arrow(dir, 0);
    }

    // ---------- dock ----------

    double DockReserve => _dock.Visibility == Visibility.Visible ? (DockBar.HeightFor(_dock.IconDip) + 24) * _scale : 0;

    /// <summary>Cached per window and process: Windows reuses window handles, and a reused one must not keep the old app's id.</summary>
    string? Aumid(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (!_aumids.TryGetValue(h, out var e) || e.pid != pid) _aumids[h] = e = (pid, WindowAumid.Get(h));
        return e.aumid;
    }

    void RebuildDock()
    {
        var d = _cfg.Dock;
        if (!d.Enabled || !d.Position.Equals("bottom", StringComparison.OrdinalIgnoreCase)) { _dockEntries = new(); _dock.Visibility = Visibility.Collapsed; return; }
        var sw = Stopwatch.StartNew();
        if (d.Pinned.Count == 0 && d.ImportTaskbarPins && !_dockImportTried && _apps.Count > 0)
        {
            _dockImportTried = true;
            try
            {
                var specs = DockImport.ReadTaskbar(_apps.All);
                if (specs.Count > 0) { d.Pinned = specs; d.ImportTaskbarPins = false; _cfg.Save(); Log.Write($"dock: imported {specs.Count} pinned apps from the taskbar"); }
            }
            catch (Exception e) { Log.Write("dock import failed: " + e.GetType().Name); }
        }
        if (_aumids.Count > 300) _aumids.Clear();
        var wins = _cache.Get(out _).Select(w => new DockWindow(w.Hwnd, w.Title, w.ExePath ?? "", Aumid(w.Hwnd), w.Minimized, w.Icon, w.ProcName)).ToList();
        _dockEntries = DockBuilder.Build(d.Pinned, _apps.All, wins, d, a => _engine.Usage.ScoreOfApp(a));
        _dock.Rebuild(_dockEntries, d, _view.Width / _scale);
        _dock.Visibility = _dockEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Log.Debug($"dock: {_dockEntries.Count} items built in {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    void StartExe(string path)
    {
        try
        {
            var before = new HashSet<IntPtr>(_cache.Get(out _).Select(w => w.Hwnd));
            BeginClose(IntPtr.Zero);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            BringNewWindowToFront(before);
        }
        catch (Exception e) { Log.Write("dock start failed: " + e.GetType().Name); }
    }

    /// <summary>Click: the app's latest window (the next one when the app was already in front); not running: start it. Shift/middle: a new instance.</summary>
    void DockActivate(DockEntry e, bool newInstance)
    {
        if (_state != State.Open && _state != State.Opening) return;
        if (newInstance)
        {
            if (e.App != null) LaunchApp(e.App, true); else if (!string.IsNullOrEmpty(e.ExePath)) StartExe(e.ExePath);
            return;
        }
        var live = e.Windows.Where(Native.IsWindow).ToList();
        if (live.Count > 0) { BeginClose(DockBuilder.NextWindow(live, _prevForeground)); return; }
        if (e.App != null) LaunchApp(e.App); else if (!string.IsNullOrEmpty(e.ExePath)) StartExe(e.ExePath);
    }

    bool PinMatches(string pin, DockEntry e)
    {
        var app = DockBuilder.ResolvePin(pin, _apps.All);
        if (app != null) return e.Key == "id:" + app.AppId;
        return pin.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) && string.Equals("exe:" + pin[4..].Trim(), e.Key, StringComparison.OrdinalIgnoreCase);
    }

    void DockPin(DockEntry e, bool pin)
    {
        var d = _cfg.Dock;
        if (pin) { if (e.Spec != null && !d.Pinned.Any(p => PinMatches(p, e))) d.Pinned.Add(e.Spec); }
        else d.Pinned.RemoveAll(p => PinMatches(p, e));
        _cfg.Save();
        RebuildDock();
        Relayout(animate: true);
    }

    bool OnThisScreen(IntPtr h)
    {
        if (!Native.GetWindowRect(h, out var r)) return true;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return cx >= _mon.Left && cx < _mon.Right && cy >= _mon.Top && cy < _mon.Bottom;
    }

    /// <summary>Moves every window of the app that is not on this screen into this screen's work area (size kept, maximized windows stay maximized), then goes to the first one.</summary>
    void DockBringHere(DockEntry e)
    {
        IntPtr first = IntPtr.Zero; int n = 0;
        foreach (var h in e.Windows.Where(Native.IsWindow))
        {
            try
            {
                bool max = Native.IsZoomed(h), min = Native.IsIconic(h);
                if (OnThisScreen(h) && !min) { if (first == IntPtr.Zero) first = h; continue; }
                if (max || min) Native.ShowWindow(h, Native.SW_RESTORE);
                Native.GetWindowRect(h, out var r);
                int w = Math.Min(r.Width, _view.Width), hh = Math.Min(r.Height, _view.Height);
                int off = (n++ % 6) * (int)(28 * _scale);
                int x = Math.Min(_view.Left + (_view.Width - w) / 2 + off, _view.Right - w), y = Math.Min(_view.Top + (_view.Height - hh) / 2 + off, _view.Bottom - hh);
                Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, Native.SWP_NOACTIVATE);
                if (max) Native.ShowWindow(h, Native.SW_MAXIMIZE);
                if (first == IntPtr.Zero) first = h;
            }
            catch (Exception ex) { Log.Write("dock bring-here failed: " + ex.GetType().Name); }
        }
        Log.Write($"dock: brought {n} window(s) of one app to this screen");
        BeginClose(first);
    }

    /// <summary>The "+" button: a small box to type an app name; Enter or a click pins it.</summary>
    void DockAddPicker(FrameworkElement anchor)
    {
        var have = _cfg.Dock.Pinned;
        var box = new TextBox { Width = 280, FontSize = 14, Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)) };
        var list = new StackPanel();
        var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
        var hits = new List<AppEntry>();
        void Pick(AppEntry a) { pop.IsOpen = false; if (!d_Has(a)) { _cfg.Dock.Pinned.Add("id:" + a.AppId); _cfg.Save(); } RebuildDock(); Relayout(animate: true); }
        bool d_Has(AppEntry a) => _cfg.Dock.Pinned.Any(p => DockBuilder.ResolvePin(p, _apps.All) == a);
        void Refresh()
        {
            list.Children.Clear(); hits.Clear();
            string q = box.Text.Trim();
            var found = _apps.All.Where(a => !d_Has(a) && (q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                 .OrderByDescending(a => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(a => a.Name).Take(8).ToList();
            foreach (var a in found)
            {
                AppCatalog.EnsureIcon(a);
                var row = new Border { Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand, Background = hits.Count == 0 ? new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) : Brushes.Transparent };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (a.Icon != null) sp.Children.Add(new Image { Source = a.Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = a.Name, Foreground = Brushes.White, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                row.Child = sp; var app = a;
                row.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; Pick(app); };
                row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                list.Children.Add(row); hits.Add(a);
            }
        }
        box.TextChanged += (_, _) => Refresh();
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) { pop.IsOpen = false; ev.Handled = true; }
            else if (ev.Key == Key.Enter && hits.Count > 0) { Pick(hits[0]); ev.Handled = true; }
        };
        pop.Closed += (_, _) => _pickerOpen = false;
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Add an app to the dock", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(2, 0, 0, 4) }); panel.Children.Add(box); panel.Children.Add(list);
        pop.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), Child = panel };
        Refresh();
        _pickerOpen = true; pop.IsOpen = true;
        box.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); Keyboard.Focus(box); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    void DockContext(DockEntry e, FrameworkElement anchor)
    {
        var m = new ContextMenu();
        for (int i = 0; i < Math.Min(12, e.Windows.Count); i++)
        {
            var h = e.Windows[i]; string title = i < e.Titles.Count && e.Titles[i].Length > 0 ? e.Titles[i] : e.Name;
            var mi = new MenuItem { Header = title.Length > 60 ? title[..59] + "…" : title };
            mi.Click += (_, _) => BeginClose(h);
            m.Items.Add(mi);
        }
        if (e.Windows.Count > 0) m.Items.Add(new Separator());
        if (e.App != null || !string.IsNullOrEmpty(e.ExePath))
        {
            var nw = new MenuItem { Header = "New window" };
            nw.Click += (_, _) => DockActivate(e, true);
            m.Items.Add(nw);
        }
        if (e.Windows.Count > 0)
        {
            int away = e.Windows.Count(h => !OnThisScreen(h));
            var here = new MenuItem { Header = away > 0 ? $"Bring all windows here ({away} on other screens)" : "Bring all windows here" };
            here.Click += (_, _) => DockBringHere(e);
            m.Items.Add(here);
        }
        if (e.Windows.Count > 0)
        {
            var close = new MenuItem { Header = "Close all windows" };
            var yes = new MenuItem { Header = $"Yes, close {e.Windows.Count}" };
            yes.Click += (_, _) => { foreach (var h in e.Windows) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); };
            close.Items.Add(yes);
            m.Items.Add(close);
        }
        if (e.Spec != null)
        {
            var pin = new MenuItem { Header = e.Pinned ? "Unpin from dock" : "Pin to dock" };
            pin.Click += (_, _) => DockPin(e, !e.Pinned);
            m.Items.Add(pin);
        }
        m.PlacementTarget = anchor;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        m.IsOpen = true;
    }

    // ---------- photo stacks ----------

    void BuildStacks()
    {
        if (_cfg.StackApps.Length == 0 || _cfg.StackMinWindows < 2) return;
        // _items is back-to-front; a stack lists its windows most recent first
        foreach (var grp in Enumerable.Reverse(_items).Where(i => _cfg.StackApps.Any(a => string.Equals(a, i.W.ProcName, StringComparison.OrdinalIgnoreCase))).GroupBy(i => i.W.ProcName.ToLowerInvariant()))
        {
            var list = grp.ToList();
            if (list.Count < _cfg.StackMinWindows) continue;
            var st = new StackInfo { Key = grp.Key };
            st.Members.AddRange(list);
            st.FrontItem = list[0];
            foreach (var m in list) m.Stack = st;
            st.BadgeText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold };
            st.Badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x20)), CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 3, 11, 4),
                Child = st.BadgeText, Opacity = 0, IsHitTestVisible = false, Cursor = Cursors.Hand,
                ToolTip = "Click to close all of them (asks first)"
            };
            st.BadgeT = new TranslateTransform(); st.Badge.RenderTransform = st.BadgeT;
            st.Badge.MouseLeftButtonDown += (_, e) => { e.Handled = true; CloseAllClicked(st); };
            _canvas.Children.Add(st.Badge);
            _stacks.Add(st);
        }
        ApplyStackOrder(reregister: false);
    }

    /// <summary>Stacks with fewer windows left than the minimum dissolve into ordinary windows.</summary>
    void NormalizeStacks()
    {
        foreach (var st in _stacks.ToList())
        {
            var live = st.Live;
            if (live.Count < Math.Max(2, _cfg.StackMinWindows))
            {
                foreach (var m in st.Members) { m.Stack = null; m.StackHidden = false; }
                _canvas.Children.Remove(st.Badge);
                _stacks.Remove(st);
                continue;
            }
            if (st.FrontItem == null || !live.Contains(st.FrontItem)) st.FrontItem = live[0];
            if (st.Raised != null && !live.Contains(st.Raised)) st.Raised = null;
        }
    }

    /// <summary>
    /// _items is also the z-order of the thumbnails (DWM stacks them in registration order) and of hit-testing (last = topmost).
    /// Keeps each stack's windows together at the place of its lowest member, sorted back to front; re-registers a stack's thumbnails when its order changed.
    /// </summary>
    void ApplyStackOrder(bool reregister = true)
    {
        if (_stacks.Count == 0) return;
        var before = _stacks.ToDictionary(st => st, st => _items.Where(i => i.Stack == st).ToList());
        var result = new List<Item>(); var done = new HashSet<StackInfo>();
        foreach (var it in _items)
        {
            if (it.Stack == null) { result.Add(it); continue; }
            if (done.Add(it.Stack)) result.AddRange(it.Stack.ZOrdered());
        }
        _items.Clear(); _items.AddRange(result);
        if (!reregister) return;
        foreach (var st in _stacks)
        {
            var after = _items.Where(i => i.Stack == st).ToList();
            if (after.SequenceEqual(before[st])) continue;
            foreach (var m in after) if (m.Thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(m.Thumb); m.Thumb = IntPtr.Zero; }
            foreach (var m in after)
            {
                if (m.W.Minimized || _cfg.DebugNoThumbnails) continue;
                if (Native.DwmRegisterThumbnail(_stage.Hwnd, m.W.Hwnd, out var th) == 0) { m.Thumb = th; UpdateThumb(m, m.CurScaled.Width > 0 ? m.CurScaled : m.Real, m.LastVis, m.LastVis > 0.001 && !m.Closing); }
            }
        }
    }

    Item? ShownOf(Item it) => it.Stack == null ? it : it.Stack.Shown;

    /// <summary>
    /// Only the thin top/left strip of a lower card raises it. A lower card bigger than the one in front also sticks out to the right/bottom;
    /// crossing that part on the way to the small front card must not bring the big one forward.
    /// </summary>
    bool InStrip(Item c, Point pDip)
    {
        var st = c.Stack;
        if (st == null) return false;
        double x = pDip.X * _scale, y = pDip.Y * _scale, w = Math.Max(10 * _scale, st.StripPx * 1.15);
        var r = c.CurScaled;
        return x - r.Left < w || y - r.Top < w;
    }

    void RotateStack(StackInfo st, int dir)
    {
        var live = st.Live;
        if (live.Count < 2) return;
        int i = Math.Max(0, live.IndexOf(st.FrontItem!));
        st.FrontItem = live[((i + dir) % live.Count + live.Count) % live.Count];
        st.Raised = null;
        Relayout(animate: true);
        ApplyStackOrder();
        var shown = st.Shown;
        if (shown != null) { _hovered = shown; SelectItem(shown); }
    }

    void RaiseCard(Item it)
    {
        var st = it.Stack;
        if (st == null || st.Raised == it) return;
        st.Raised = it; st.LeftAt = -1; _raisePos = _mouseDip;
        ApplyStackOrder();
        _hovered = it; SelectItem(it);
        StartTicking();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state != State.Open) return;
        if (SearchMode)
        {
            if (_stripScroll.IsMouseOver) { _stripScroll.ScrollToHorizontalOffset(_stripScroll.HorizontalOffset - e.Delta); e.Handled = true; }
            return;
        }
        var it = HitItem(e.GetPosition(_root));
        if (it?.Stack == null) return;
        RotateStack(it.Stack, e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    void CloseAllClicked(StackInfo st)
    {
        double now = Now;
        if (now > st.ArmedUntil) { st.ArmedUntil = now + 3000; StartTicking(); return; }   // first click: ask
        st.ArmedUntil = 0;
        foreach (var m in st.Live) RequestClose(m);
        StartTicking();
    }

    static readonly Brush BadgeNormal = Frozen(Color.FromArgb(0xD8, 0x20, 0x20, 0x20)), BadgeArmed = Frozen(Color.FromArgb(0xF0, 0xC0, 0x1C, 0x28));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void UpdateBadges(double alpha)
    {
        double s = _scale, now = Now;
        foreach (var st in _stacks)
        {
            var shown = st.Shown;
            if (shown == null || alpha <= 0.01) { st.Badge.Opacity = 0; st.Badge.IsHitTestVisible = false; continue; }
            bool armed = now < st.ArmedUntil;
            int n = st.Live.Count;
            st.BadgeText.Text = armed ? $"Close all {n}?  Click again" : $"{n} windows";
            var bg = armed ? BadgeArmed : BadgeNormal;   // runs every frame: no new brush per frame
            if (!ReferenceEquals(st.Badge.Background, bg)) st.Badge.Background = bg;
            st.Badge.Opacity = alpha;
            st.Badge.IsHitTestVisible = alpha > 0.5;
            var r = shown.CurScaled;
            double w = st.Badge.ActualWidth > 0 ? st.Badge.ActualWidth : 90;
            st.BadgeT.X = r.Right / s - w - 8; st.BadgeT.Y = r.Top / s + 8;
            if (armed) { /* keep ticking so the confirmation expires */ }
        }
    }

    static Grid BuildTitle(string text)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = Brushes.White, FontSize = 13.5, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 4, 12, 5), Child = tb, HorizontalAlignment = HorizontalAlignment.Center
        };
        var host = new Grid { IsHitTestVisible = false, Opacity = 0 };
        host.Children.Add(pill);
        return host;
    }

    Button BuildCloseButton(Item it)
    {
        var x = new Path
        {
            Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = Brushes.White, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var circle = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), BorderThickness = new Thickness(1), Child = x
        };
        var b = new Button
        {
            Content = circle, Opacity = 0, IsHitTestVisible = false, Cursor = Cursors.Hand, Focusable = false,
            Template = CreateFlatTemplate()
        };
        b.MouseEnter += (_, _) => circle.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xC0, 0x1C, 0x28));
        b.MouseLeave += (_, _) => circle.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30));
        b.Click += (_, _) => RequestClose(it);
        return b;
    }

    static ControlTemplate CreateFlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    void UpdateThumb(Item it, Rect r, double opacity, bool visible)
    {
        if (it.Thumb == IntPtr.Zero) return;
        var w = it.W;
        if (!it.SrcChecked)
        {
            it.SrcChecked = true;
            // What DWM really has for this window may not be what the cached window rect says (the window was rescaled or resized since the list was
            // read, e.g. after un-docking, or an app that is not DPI-aware): scale the source rectangle so the clone shows exactly the same picture.
            if (Native.DwmQueryThumbnailSourceSize(it.Thumb, out var ss) == 0 && ss.cx > 0 && ss.cy > 0 && w.WinW > 0 && w.WinH > 0)
            {
                double sx = ss.cx / (double)w.WinW, sy = ss.cy / (double)w.WinH;
                if (Math.Abs(sx - 1) > 0.004 || Math.Abs(sy - 1) > 0.004)
                {
                    it.SrcSx = sx; it.SrcSy = sy;
                    Log.Debug($"thumbnail source differs from the cached window size ({w.ProcName}): {ss.cx}x{ss.cy} vs {w.WinW}x{w.WinH}");
                }
            }
        }
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top), Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom) },
            rcSource = new RECT { Left = (int)Math.Round(w.CropL * it.SrcSx), Top = (int)Math.Round(w.CropT * it.SrcSy), Right = (int)Math.Round((w.WinW - w.CropR) * it.SrcSx), Bottom = (int)Math.Round((w.WinH - w.CropB) * it.SrcSy) },
            opacity = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255), fVisible = visible, fSourceClientAreaOnly = false
        };
        Native.DwmUpdateThumbnailProperties(it.Thumb, ref p);
    }

    // ---------- layout (GNOME's UnalignedLayoutStrategy) ----------

    List<Item> Layoutable() => _items.Where(i => !i.Closing).ToList();

    void Relayout(bool animate)
    {
        NormalizeStacks();
        var m = Layoutable();
        if (m.Count == 0) return;
        double now = Now;

        // layout units: ordinary windows, and ONE box per stack
        var units = new List<(Item? it, StackInfo? st)>();
        var seen = new HashSet<StackInfo>();
        foreach (var i in m) { if (i.Stack == null) units.Add((i, null)); else if (seen.Add(i.Stack)) units.Add((null, i.Stack)); }
        var boxes = new List<LBox>();
        var geo = new Dictionary<StackInfo, (LBox[] rel, double bw, double bh, List<Item> live)>();
        foreach (var (it, st) in units)
        {
            if (it != null) { boxes.Add(new LBox(it.Real.X, it.Real.Y, it.Real.Width, it.Real.Height)); continue; }
            var live = st!.Live;
            var sizes = live.Select(x => (x.Real.Width, x.Real.Height)).ToList();
            int front = Math.Max(0, live.IndexOf(st.FrontItem!));
            var rel = StackLayout.Cards(sizes, front, _cfg.StackVisible, _cfg.StackOffsetPct, out double bw, out double bh);
            geo[st] = (rel, bw, bh, live);
            double off = StackLayout.Offset(sizes, _cfg.StackOffsetPct);
            int vv = StackLayout.Visible(live.Count, _cfg.StackVisible);
            var anchor = live[0].Real;   // where the most recent window is
            boxes.Add(new LBox(Math.Max(0, anchor.X - (vv - 1) * off), Math.Max(0, anchor.Y - (vv - 1) * off), bw, bh));
        }

        LSlot[] slots;
        if (_cfg.Layout.Equals("gnome", StringComparison.OrdinalIgnoreCase))
        {
            // GNOME 40+ rows. Area: below the search entry, above the bottom margin (no dash yet).
            double top = 110 * _scale, side = 60 * _scale, bottom = 80 * _scale + DockReserve;
            // spacing 20 + the window chrome oversize ((1 - ICON_OVERLAP) * ICON_SIZE + WINDOW_ACTIVE_SIZE_INC)
            double spacing = (20 + (1 - IconOverlap) * IconSize + ActiveSizeInc) * _scale;
            slots = new GnomeLayout(_view.Height, spacing, spacing)
                .Compute(boxes, new LArea(side, top, _view.Width - 2 * side, Math.Max(100, _view.Height - top - bottom)));
        }
        else
        {
            // Natural: windows keep their relative desktop positions. Each box = frame + chrome (close button above,
            // icon + title below) + gap; the work area starts under the search entry.
            double s = _scale;
            var area = new LArea(40 * s, 100 * s, _view.Width - 80 * s, Math.Max(100, _view.Height - 124 * s - DockReserve));
            slots = new NaturalLayout(_view.Width, _view.Height,
                marginTop: (14 + ActiveSizeInc) * s, marginBottom: ((1 - IconOverlap) * IconSize + IconTitleSpacing + 26) * s,
                marginSide: ActiveSizeInc * s, gap: 20 * s, maxScale: _cfg.MaxScale, minLong: 220 * s).Compute(boxes, area);
        }

        void Assign(Item it, Rect to, int row, int col)
        {
            it.SlotFrom = animate && it.SlotTo.Width > 0 ? SlotNow(it, now) : to;
            it.SlotTo = to; it.SlotT0 = animate ? now : now - ReshuffleMs;
            it.Row = row; it.Col = col;
            if (it.Card != null) { it.Card.Width = to.Width / _scale; it.Card.Height = to.Height / _scale; it.CardBase = to; }
            if (it.W.Minimized) it.DrawFrom = ScaleAround(to, 0.75);
        }

        for (int k = 0; k < units.Count; k++)
        {
            var slot = slots[k];
            if (units[k].it is { } it) { it.StackHidden = false; Assign(it, new Rect(slot.X, slot.Y, slot.Width, slot.Height), slot.Row, slot.Col); continue; }
            var st = units[k].st!;
            var (rel, bw, bh, live) = geo[st];
            double kScale = Math.Min(slot.Width / bw, slot.Height / bh);
            st.StripPx = StackLayout.Offset(live.Select(x => (x.Real.Width, x.Real.Height)).ToList(), _cfg.StackOffsetPct) * kScale;
            int vv = StackLayout.Visible(live.Count, _cfg.StackVisible);
            for (int j = 0; j < live.Count; j++)
            {
                var c = live[j];
                c.StackHidden = st.Depth(c) >= vv;
                Assign(c, new Rect(slot.X + rel[j].X * kScale, slot.Y + rel[j].Y * kScale, rel[j].Width * kScale, rel[j].Height * kScale), slot.Row, slot.Col);
            }
        }
        StartTicking();
    }

    Rect SlotNow(Item it, double now)
    {
        double t = Math.Clamp((now - it.SlotT0) / ReshuffleMs, 0, 1);
        return Lerp(it.SlotFrom, it.SlotTo, EaseOutQuad(t));
    }

    static double EaseOutQuad(double t) => 1 - (1 - t) * (1 - t);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static Rect Lerp(Rect a, Rect b, double t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Width, b.Width, t), Lerp(a.Height, b.Height, t));
    static double Step(double v, double target, double d) => target > v ? Math.Min(target, v + d) : Math.Max(target, v - d);

    // ---------- animation loop ----------

    void StartTicking()
    {
        if (_ticking) return;
        _ticking = true; _lastTick = Now;
        CompositionTarget.Rendering += OnRender;
    }

    void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnRender;
    }

    void OnRender(object? sender, EventArgs e)
    {
        try { Tick(); } catch (Exception ex) { Log.Write("tick: " + ex); StopTicking(); }
    }

    void Tick()
    {
        long tickStart = Stopwatch.GetTimestamp();
        double now = Now, dt = Math.Max(0, now - _lastTick); _lastTick = now;
        bool busy = false;
        var selected = SelectedItem();   // once per frame

        if (_awaitFirstFrame)
        {
            // The animation starts when the first frame is actually on its way, so no frames are skipped by setup work.
            _awaitFirstFrame = false;
            _pT0 = now; dt = 0;
            Mark("first frame");
            _trace.Clear(); _firstFrameAt = now;
            _statFrames = 0; _statSlow = 0; _statMax = 0; _statSum = 0; _tickSum = 0; _tickMax = 0; _statEnd = now + 350;
        }
        else if (_statEnd > 0)
        {
            if (_statFrames < 26) _trace.Append(dt.ToString("0")).Append(' ');
            _statFrames++; _statSum += dt; _statMax = Math.Max(_statMax, dt); if (dt > 25) _statSlow++;
            if (now >= _statEnd)
            {
                _statEnd = 0;
                Log.Debug($"open: {_logCount} windows | src={_source} area={(MonitorMode ? "monitor" : "workarea")} thumbs={(_cfg.DebugNoThumbnails ? "off" : "on")} park={_cfg.ParkMode} | trigger->(ms) {_openTrace}| animation frames {_statFrames}, avg {_statSum / Math.Max(1, _statFrames):0.0} ms, worst {_statMax:0.0} ms, >25ms: {_statSlow} | our tick cpu avg {_tickSum / Math.Max(1, _statFrames + 1):0.00} ms, max {_tickMax:0.0} ms | frame ms: {_trace}| {ProbeSummary()}");
            }
        }

        // overview progress (0 = windows where they really are, 1 = spread out)
        double closeWall = 1;   // wallpaper opacity while closing (stays opaque until the clones are home)
        double t = Math.Clamp((now - _pT0) / OpenMs, 0, 1);
        _p = Lerp(_pFrom, _pTo, EaseOutQuad(t));
        if (t < 1) busy = true;
        else if (_state == State.Opening)
        {
            _state = State.Open;
            TraceMark("open-end");
            InputLog($"open-end: foreground {FgName()}, active={IsActive}");
            if (_focusedEarly)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_state == State.Open && !_search.IsKeyboardFocused) { _search.Focus(); Keyboard.Focus(_search); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            else
                TakeFocus();
        }
        else if (_state == State.Closing)
        {
            double fadeMs = _cfg.BackdropFadeMs <= 0 ? 0 : CloseFadeMs;
            double tf = fadeMs <= 0 ? 1 : Math.Clamp((now - _pT0 - OpenMs) / fadeMs, 0, 1);
            if (tf >= 1) { FinishHide(); return; }
            busy = true;
            closeWall = 1 - tf;
        }
        // The wallpaper hides the real windows almost at once, so the clones appear to *move into* the overview.
        _stage.SetBackdrop(_cfg.BackdropFadeMs <= 0 ? 1 : (_state == State.Closing ? closeWall : Math.Min(1, _p / WallpaperRamp)));

        // search fade: windows out, results in
        double ns = Step(_searchT, _searchTarget, dt / SearchFadeMs);
        if (ns != _searchTarget) busy = true;
        _searchT = ns;
        double winAlpha = 1 - EaseOutQuad(_searchT);
        double ui = Fade01(_p / 0.6);   // 0 -> 1 as the overview opens, 1 -> 0 as it closes
        _bar.Opacity = ui;
        if (_dock.Visibility == Visibility.Visible)
        {
            double da = ui * (_cfg.Dock.HideWhenSearching ? 1 - EaseOutQuad(_searchT) : 1);
            _dock.Opacity = da; _dock.Slide.Y = (1 - ui) * 30; _dock.IsHitTestVisible = da > 0.5;
        }
        _resultsHost.Opacity = EaseOutQuad(_searchT) * ui;
        if (_searchT <= 0 && _resultsHost.Visibility == Visibility.Visible && _results.Count == 0 && _filesBox.Visibility != Visibility.Visible) _resultsHost.Visibility = Visibility.Collapsed;

        // delayed relayout after closing windows
        if (_repositionAt >= 0)
        {
            if (now >= _repositionAt) ResolveClosings(); else busy = true;
        }

        foreach (var it in _items)
        {
            // slot animation
            double st = (now - it.SlotT0) / ReshuffleMs;
            if (st < 1) busy = true;
            var slot = SlotNow(it, now);
            var r = Lerp(it.W.Minimized && it.DrawFrom.Width > 0 ? it.DrawFrom : it.Real, slot, _p);

            // overlay (title + close button + scale-up) with GNOME's idle-hide delay
            if (it.OverlayShown && it != _hovered && it != selected && it.LeaveAt >= 0 && now - it.LeaveAt >= OverlayIdleHideMs && !IsPointerOnChrome(it))
                it.OverlayShown = false;
            else if (it.OverlayShown && it != _hovered && it != selected && it.LeaveAt >= 0) busy = true;
            double ovTarget = it.OverlayShown && !it.Closing ? 1 : 0;
            if (it.Ov != ovTarget) busy = true;
            it.Ov = Step(it.Ov, ovTarget, dt / OverlayFadeMs);
            double ov = EaseOutQuad(it.Ov);

            // uniform scale-up around the centre: (max(w,h) + 2 * ACTIVE_SIZE_INC) / max(w,h)
            double big = Math.Max(r.Width, r.Height);
            double k = big > 0 ? 1 + (2 * ActiveSizeInc * _scale / big) * ov : 1;
            var rs = new Rect(r.X + r.Width / 2 - r.Width * k / 2, r.Y + r.Height / 2 - r.Height * k / 2, r.Width * k, r.Height * k);
            it.Cur = r; it.CurScaled = rs;

            double vis = it.Closing ? 0 : winAlpha;
            if (it.StackHidden) vis *= 1 - Fade01((_p - 0.25) / 0.4);   // cards deeper than the strips melt into the stack
            it.LastVis = vis;
            UpdateThumb(it, rs, vis, vis > 0.001 && !it.Closing);   // clones replace the real windows at full opacity, like GNOME
            bool chromeOn = it.Stack == null || it == it.Stack.Shown;
            PositionChrome(it, r, rs, vis, chromeOn ? ov : 0, chromeOn);
        }

        // photo stacks: badge, pointer dwell (raise a lower card), release of a raised card
        if (_stacks.Count > 0)
        {
            UpdateBadges(_state == State.Closing ? 0 : Fade01((_p - 0.45) / 0.4) * (1 - _searchT));
            var ptPx = new Point(_mouseDip.X * _scale, _mouseDip.Y * _scale);
            if (_candidate != null)
            {
                busy = true;
                if (_candidate.Stack == null || _candidate.Closing || _state != State.Open) _candidate = null;
                else if (now - _candidateSince >= RaiseDwellMs)
                {
                    var c = _candidate; _candidate = null;
                    bool moved = c.Stack!.Raised == null || (_mouseDip - _raisePos).Length >= 6;
                    if (moved && HitItem(_mouseDip) == c && InStrip(c, _mouseDip)) RaiseCard(c);
                }
            }
            foreach (var st in _stacks)
            {
                if (now < st.ArmedUntil) busy = true;
                else if (st.ArmedUntil > 0) { st.ArmedUntil = 0; }
                if (st.Raised == null) continue;
                busy = true;
                if (st.Live.Any(x => x.CurScaled.Contains(ptPx))) st.LeftAt = -1;
                else if (st.LeftAt < 0) st.LeftAt = now;
                else if (now - st.LeftAt >= RaiseReleaseMs) { st.Raised = null; st.LeftAt = -1; ApplyStackOrder(); }
            }
        }

        UpdateSelectionFrame(selected);
        if (_statEnd > 0) { double ms = (Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency; _tickSum += ms; _tickMax = Math.Max(_tickMax, ms); }
        if (!busy) { if (_state == State.Open) StopTicking(); }
    }

    bool IsPointerOnChrome(Item it) => it.Close?.IsMouseOver == true;

    static double Fade01(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

    static Rect ScaleAround(Rect r, double k) =>
        new(r.X + r.Width * (1 - k) / 2, r.Y + r.Height * (1 - k) / 2, r.Width * k, r.Height * k);

    void PositionChrome(Item it, Rect r, Rect rs, double vis, double ov, bool chromeOn = true)
    {
        double s = _scale, a = chromeOn ? vis * Fade01((_p - 0.45) / 0.4) : 0, ca = vis * Fade01((_p - 0.2) / 0.5);   // icons only once the windows have spread
        if (it.Card != null && it.CardT != null && it.CardBase.Width > 0)
        {
            // laid out once at slot size; scaled/moved by a render transform
            double sx = rs.Width / it.CardBase.Width, sy = rs.Height / it.CardBase.Height;
            it.CardT.Matrix = new Matrix(sx, 0, 0, sy, rs.Left / s, rs.Top / s);
            it.Card.Opacity = ca;
        }
        if (it.Icon != null && it.IconT != null)
        {
            // the icon's 70% line sits on the preview's bottom edge, centred horizontally
            it.IconT.X = (r.X + r.Width / 2) / s - IconSize / 2;
            it.IconT.Y = r.Bottom / s - IconOverlap * IconSize;
            it.Icon.Opacity = a;
        }
        // title + close button are invisible until hovered: skip all work for them while hidden
        bool chromeVisible = ov > 0.001;
        if (it.Title != null && it.TitleT != null && (chromeVisible || it.Title.Opacity > 0))
        {
            if (it.Title.Visibility != Visibility.Visible) it.Title.Visibility = Visibility.Visible;
            double hostW = Math.Max(it.SlotTo.Width / s, 220);
            if (Math.Abs(hostW - it.TitleW) > 0.5)
            {
                it.TitleW = hostW; it.Title.Width = hostW;
                ((TextBlock)((Border)it.Title.Children[0]).Child).MaxWidth = hostW - 28;
            }
            it.TitleT.X = (r.X + r.Width / 2) / s - hostW / 2;
            it.TitleT.Y = r.Bottom / s + (1 - IconOverlap) * IconSize + IconTitleSpacing;
            it.Title.Opacity = ov * a;
            if (ov <= 0.001 && it.Title.Opacity <= 0.001) it.Title.Visibility = Visibility.Collapsed;
        }
        if (it.Close != null && it.CloseT != null && (chromeVisible || it.Close.Opacity > 0))
        {
            // centred on the preview's top-right corner
            if (it.Close.Visibility != Visibility.Visible) it.Close.Visibility = Visibility.Visible;
            it.CloseT.X = rs.Right / s - 14; it.CloseT.Y = rs.Top / s - 14;
            it.Close.Opacity = ov * a;
            if (ov <= 0.001 && it.Close.Opacity <= 0.001) it.Close.Visibility = Visibility.Collapsed;
            bool hit = ov > 0.5 && a > 0.5;
            if (hit != it.CloseHit) { it.CloseHit = hit; it.Close.IsHitTestVisible = hit; }
        }
    }

    void UpdateSelectionFrame(Item? it)
    {
        if (it == null || _searchT > 0.05 || it.Closing || _state == State.Closing) { _frame.Visibility = Visibility.Collapsed; return; }
        double pad = 4 * _scale, s = _scale;
        var r = it.CurScaled;
        Canvas.SetLeft(_frame, (r.Left - pad) / s); Canvas.SetTop(_frame, (r.Top - pad) / s);
        _frame.Width = (r.Width + 2 * pad) / s; _frame.Height = (r.Height + 2 * pad) / s;
        _frame.Opacity = Math.Min(1, _p * 2) * (1 - _searchT);
        _frame.Visibility = Visibility.Visible;
    }

    // ---------- search (GNOME: the window picker fades out, results take its place) ----------

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressText || _state == State.Hidden) return;
        string q = _search.Text.Trim();
        _hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var swSearch = Stopwatch.StartNew();
        _results = q.Length == 0 ? new List<SearchItem>() : _engine.Query(q);
        _fileSel = -1;
        _hoverArmed = false; _searchMousePos = Mouse.GetPosition(_root);   // the pointer may rest on the spot where results appear
        RebuildResults();
        if (q.Length > 0) Log.Debug($"search ({q.Length} chars): {swSearch.Elapsed.TotalMilliseconds:0.0} ms, {_results.Count} results ({_results.Count(r => r.Kind != SearchKind.App)} settings/tools)");
        _searchTarget = q.Length > 0 ? 1 : 0;
        _sel = q.Length > 0 && _results.Count > 0 ? 0 : -1;
        if (q.Length == 0) { _hovered = null; }
        StartFileSearch(q);
        StartTicking();
        UpdateSelection();
    }

    // ----- icon line -----

    void RebuildResults()
    {
        double room = Math.Max(400, _view.Width / _scale - 200);
        _stripScroll.MaxWidth = Math.Min(1300, room);
        _filesBox.Width = Math.Min(980, room);
        _stripPanel.Children.Clear(); _tiles.Clear();
        foreach (var item in _results)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            if (item.App?.Icon != null) sp.Children.Add(new Image { Source = item.App.Icon, Width = 56, Height = 56, Margin = new Thickness(0, 4, 0, 8) });
            else if (item.Kind == SearchKind.App) sp.Children.Add(new TextBlock { Text = item.Title.Substring(0, 1).ToUpperInvariant(), FontSize = 36, Foreground = Brushes.White, Height = 56, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8) });
            else
            {
                var badge = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 4, 0, 8), Background = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)),
                    Child = new TextBlock { Text = item.Kind == SearchKind.Tool ? "\uE756" : "\uE713", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 28, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                sp.Children.Add(badge);
            }
            sp.Children.Add(new TextBlock
            {
                Text = item.Title, Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (item.Subtitle.Length > 0) sp.Children.Add(new TextBlock { Text = item.Subtitle, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), FontSize = 11, FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Center });
            var tile = new Border { Width = 124, Padding = new Thickness(6, 8, 6, 8), Margin = new Thickness(3), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Child = sp, Tag = item, Cursor = Cursors.Hand };
            var it2 = item;
            tile.MouseEnter += (_, _) => { if (!_hoverArmed) return; _fileSel = -1; _sel = _results.IndexOf(it2); UpdateSelection(); };
            tile.MouseLeftButtonUp += (_, _) => OpenItem(it2, NewWin(Keyboard.Modifiers));
            _tiles.Add(tile); _stripPanel.Children.Add(tile);
        }
        _stripScroll.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _resultsHost.Visibility = _results.Count > 0 || _filesBox.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _stripScroll.ScrollToHorizontalOffset(0);
    }

    void UpdateSelection()
    {
        object? cur = _searchTarget > 0 && _fileSel < 0 && _sel >= 0 && _sel < _results.Count ? _results[_sel] : null;
        foreach (var t in _tiles)
            t.Background = ReferenceEquals(t.Tag, cur) ? new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)) : Brushes.Transparent;
        for (int i = 0; i < _fileRows.Count; i++)
            _fileRows[i].Background = i == _fileSel ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : Brushes.Transparent;
        if (cur != null) { int i = _results.IndexOf((SearchItem)cur); if (i >= 0 && i < _tiles.Count) _tiles[i].BringIntoView(); }
        if (_fileSel >= 0 && _fileSel < _fileRows.Count) _fileRows[_fileSel].BringIntoView();
        UpdateKeyHint();
    }

    bool NewWin(ModifierKeys m) => SearchKeys.Resolve(KeyTarget.App, m, _cfg.Search.Keys) == SearchAction.NewWindow;

    /// <summary>The line under the results: what Enter / click does with the selected result, and with the key that is held right now.</summary>
    void UpdateKeyHint()
    {
        if (!_cfg.Search.Keys.ShowHint || !SearchMode) { _keyHint.Visibility = Visibility.Collapsed; return; }
        bool file = _fileSel >= 0 && _fileSel < _files.Count;
        bool any = file || (_sel >= 0 && _sel < _results.Count);
        if (!any) { _keyHint.Visibility = Visibility.Collapsed; return; }
        // a Windows setting or tool has no "new window" etc.: Ctrl/Shift/Alt do nothing there, so the hint must not offer them
        if (!file && _results[_sel].Kind != SearchKind.App) { _keyHint.Text = "Enter / click: open"; _keyHint.Visibility = Visibility.Visible; return; }
        var target = file ? KeyTarget.File : KeyTarget.App;
        _keyHint.Text = SearchKeys.Hint(target, Keyboard.Modifiers, _cfg.Search.Keys);
        _keyHint.Visibility = Visibility.Visible;
    }

    // ----- files (Everything) -----

    bool FilesEnabled => _cfg.Search.Everything.Enabled;
    int FileRowCount => _fileRows.Count;

    void ClearFiles()
    {
        _fileTimer.Stop(); _fileQueryId++;
        _files = new(); _fileTotal = 0; _fileSel = -1; _fileText = "";
        _filesPanel.Children.Clear(); _fileRows.Clear();
        _filesBox.Visibility = Visibility.Collapsed;
    }

    void StartFileSearch(string q)
    {
        if (!FilesEnabled || q.Length == 0) { ClearFiles(); return; }
        _fileText = q;
        _fileTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_cfg.Search.Everything.DebounceMs, 0, 1000));
        _fileTimer.Stop(); _fileTimer.Start();
    }

    async void RunFileSearch()
    {
        string text = _fileText;
        if (text.Length == 0) return;
        _everything ??= new EverythingClient(_cfg.Search.Everything);
        int id = ++_fileQueryId;
        var sw = Stopwatch.StartNew();
        EverythingResult res;
        try { res = await _everything.QueryAsync(text); }
        catch (Exception) { res = new EverythingResult(EverythingState.Error, 0, new(), 0); }
        if (id != _fileQueryId || _state == State.Hidden) return;   // a newer keystroke (or closing) made this answer obsolete
        _files = res.Hits; _fileTotal = res.Total; _fileState = res.State;
        if (_fileSel >= _files.Count + (_fileTotal > _files.Count ? 1 : 0)) _fileSel = -1;
        RebuildFiles();
        Log.Debug($"files: {res.State}, {res.Hits.Count}/{res.Total} in {res.Ms} ms (round trip {sw.ElapsedMilliseconds} ms)");
        // nothing in the icon line: start in the file list
        if (_results.Count == 0 && _files.Count > 0 && _fileSel < 0) { _fileSel = 0; UpdateSelection(); }
    }

    static IEnumerable<(string text, bool hit)> Pieces(string text, string[] words)
    {
        int i = 0;
        while (i < text.Length)
        {
            int best = -1, bestLen = 0;
            foreach (var w in words)
            {
                int p = text.IndexOf(w, i, StringComparison.OrdinalIgnoreCase);
                if (p >= 0 && (best < 0 || p < best)) { best = p; bestLen = w.Length; }
            }
            if (best < 0) { yield return (text[i..], false); yield break; }
            if (best > i) yield return (text[i..best], false);
            yield return (text.Substring(best, bestLen), true);
            i = best + bestLen;
        }
    }

    TextBlock Highlighted(string text, string[] words, double size, Brush normal)
    {
        var tb = new TextBlock { FontSize = size, FontFamily = new FontFamily("Segoe UI"), Foreground = normal, TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var (t, hit) in Pieces(text, words)) tb.Inlines.Add(new System.Windows.Documents.Run(t) { FontWeight = hit ? FontWeights.Bold : FontWeights.Normal, Foreground = hit ? Brushes.White : normal });
        return tb;
    }

    static string Size(long b) => b < 0 ? "" : b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    void RebuildFiles()
    {
        _filesPanel.Children.Clear(); _fileRows.Clear();
        if (!FilesEnabled || _search.Text.Trim().Length == 0) { _filesBox.Visibility = Visibility.Collapsed; return; }
        _filesBox.Visibility = Visibility.Visible;
        _resultsHost.Visibility = Visibility.Visible;
        var dim = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255));
        var words = _fileText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains(':') && !w.StartsWith("-") && !w.StartsWith("!")).Select(w => w.Trim('"', '*')).Where(w => w.Length > 0).ToArray();

        var head = new Grid { Margin = new Thickness(6, 0, 6, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string title = _fileState == EverythingState.Ready ? (_fileTotal == 0 ? "Files · nothing found" : $"Files · {_fileTotal:N0} result{(_fileTotal == 1 ? "" : "s")}") : "Files";
        head.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xFF)), FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") });
        _filesPanel.Children.Add(head);

        if (_fileState != EverythingState.Ready && _fileState != EverythingState.Unknown)
        {
            string msg = _fileState switch
            {
                EverythingState.NotRunning => "Everything is not running, so files cannot be searched.",
                EverythingState.Blocked => "Windows blocks Spread from talking to Everything (is Everything running as administrator?).",
                _ => "Everything did not answer."
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 4) };
            row.Children.Add(new TextBlock { Text = msg, Foreground = dim, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center });
            if (_fileState == EverythingState.NotRunning && _cfg.Search.Everything.OfferStart)
            {
                var btn = new Border { Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = "Start Everything", Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI") } };
                btn.MouseLeftButtonUp += (_, _) => { _everything ??= new EverythingClient(_cfg.Search.Everything); _everything.Start(); _fileTimer.Interval = TimeSpan.FromMilliseconds(1800); _fileTimer.Stop(); _fileTimer.Start(); };
                row.Children.Add(btn);
            }
            _filesPanel.Children.Add(row);
            return;
        }

        for (int i = 0; i < _files.Count; i++)
        {
            var h = _files[i]; int idx = i;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            var icon = new Image { Width = 18, Height = 18, Source = FileIcons.Get(h.Name, h.IsFolder), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            var text = new StackPanel();
            text.Children.Add(Highlighted(h.Name, words, 14, Brushes.White));
            text.Children.Add(new TextBlock { Text = h.Dir, Foreground = dim, FontSize = 11.5, FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis });
            var date = new TextBlock { Text = h.Modified > DateTime.MinValue ? (h.Modified.Date == DateTime.Today ? h.Modified.ToString("HH:mm") : h.Modified.ToString("d MMM yyyy")) : "", Foreground = dim, FontSize = 12, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            var size = new TextBlock { Text = h.IsFolder ? "" : Size(h.Size), Foreground = dim, FontSize = 12, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(text, 1); Grid.SetColumn(date, 2); Grid.SetColumn(size, 3);
            g.Children.Add(icon); g.Children.Add(text); g.Children.Add(date); g.Children.Add(size);
            var row = new Border { Child = g, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = h.Full };
            row.MouseEnter += (_, _) => { if (!_hoverArmed) return; _fileSel = idx; UpdateSelection(); };
            row.MouseLeftButtonUp += (_, _) => OpenFile(h, Keyboard.Modifiers);
            _fileRows.Add(row); _filesPanel.Children.Add(row);
        }
        if (_fileTotal > _files.Count)
        {
            var more = new Border { Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = $"Show all {_fileTotal:N0} in Everything…", Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xFF)), FontSize = 13, FontFamily = new FontFamily("Segoe UI") } };
            int idx = _fileRows.Count;
            more.MouseEnter += (_, _) => { if (!_hoverArmed) return; _fileSel = idx; UpdateSelection(); };
            more.MouseLeftButtonUp += (_, _) => ShowAllInEverything();
            _fileRows.Add(more); _filesPanel.Children.Add(more);
        }
        UpdateSelection();
    }

    void ShowAllInEverything()
    {
        _everything ??= new EverythingClient(_cfg.Search.Everything);
        string text = _search.Text.Trim();
        BeginClose(IntPtr.Zero);
        _everything.ShowAll(text);
    }

    void OpenFile(EverythingHit h, ModifierKeys mods)
    {
        try
        {
            var action = SearchKeys.Resolve(KeyTarget.File, mods, _cfg.Search.Keys);
            if (action == SearchAction.CopyPath) { Clipboard.SetText(h.Full); return; }
            var before = new HashSet<IntPtr>(_cache.Get(out _).Select(w => w.Hwnd));
            BeginClose(IntPtr.Zero);
            switch (action)
            {
                case SearchAction.Reveal:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{h.Full}\"") { UseShellExecute = true });
                    break;
                case SearchAction.OpenFolder:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{(h.IsFolder ? h.Full : h.Dir)}\"") { UseShellExecute = true });
                    break;
                case SearchAction.OpenWith when !h.IsFolder:
                    try { Process.Start(new ProcessStartInfo(h.Full) { UseShellExecute = true, Verb = "openas" }); }
                    catch (Exception e1)
                    {
                        Log.Write("open with (openas) failed: " + e1.GetType().Name + ", trying the OpenAs dialog");
                        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {h.Full}") { UseShellExecute = true });
                    }
                    break;
                default:
                    Process.Start(new ProcessStartInfo(h.Full) { UseShellExecute = true, WorkingDirectory = h.IsFolder ? h.Full : h.Dir });
                    break;
            }
            BringNewWindowToFront(before);
        }
        catch (Exception e) { Log.Write("open file failed: " + e.GetType().Name); }
    }

    /// <summary>Keys while a search is active: Left/Right in the icon line, Up/Down between the line and the file list.</summary>
    bool SearchKey(KeyEventArgs e)
    {
        if (!SearchMode) return false;
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool inFiles = _fileSel >= 0;
        int rows = FileRowCount;
        switch (k)
        {
            case Key.Left: if (!inFiles && _results.Count > 0) { _sel = Math.Max(0, _sel - 1); UpdateSelection(); } return true;
            case Key.Right: if (!inFiles && _results.Count > 0) { _sel = Math.Min(_results.Count - 1, _sel + 1); UpdateSelection(); } return true;
            case Key.Down:
                if (!inFiles) { if (rows > 0) { _fileSel = 0; UpdateSelection(); } }
                else { _fileSel = Math.Min(rows - 1, _fileSel + 1); UpdateSelection(); }
                return true;
            case Key.Up:
                if (inFiles) { _fileSel = _fileSel == 0 && _results.Count > 0 ? -1 : Math.Max(0, _fileSel - 1); if (_sel < 0 && _results.Count > 0) _sel = 0; UpdateSelection(); }
                return true;
            case Key.PageDown: if (rows > 0) { _fileSel = Math.Min(rows - 1, Math.Max(0, _fileSel) + 5); UpdateSelection(); } return true;
            case Key.PageUp: if (inFiles) { _fileSel = Math.Max(0, _fileSel - 5); UpdateSelection(); } return true;
            case Key.Tab:
                if (inFiles && _results.Count > 0) { _fileSel = -1; if (_sel < 0) _sel = 0; }
                else if (!inFiles && rows > 0) _fileSel = 0;
                UpdateSelection(); return true;
            case Key.Enter:
                if (inFiles)
                {
                    if (_fileSel < _files.Count) OpenFile(_files[_fileSel], mods);
                    else ShowAllInEverything();
                }
                else if (_sel >= 0 && _sel < _results.Count) OpenItem(_results[_sel], NewWin(mods));
                return true;
            default: return false;
        }
    }

    /// <summary>Choose an entry of the icon line: an app (go to it or start it) or a settings page / tool.</summary>
    void OpenItem(SearchItem item, bool forceNew)
    {
        _engine.Usage.Record(item.UsageKey, 1);
        if (item.Kind == SearchKind.App && item.App != null) { LaunchApp(item.App, forceNew); return; }
        var s = item.Setting;
        if (s == null) return;
        try
        {
            var before = new HashSet<IntPtr>(_cache.Get(out _).Select(w => w.Hwnd));
            BeginClose(IntPtr.Zero);
            if (s.Uri.Length > 0) Process.Start(new ProcessStartInfo(s.Uri) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(s.Exe, s.Args) { UseShellExecute = true });
            BringNewWindowToFront(before);
        }
        catch (Exception e) { Log.Write("open setting failed: " + e.GetType().Name); }
    }

    // ---------- selection & input ----------

    bool SearchMode => _searchTarget > 0;

    /// <summary>Windows in reading order (coarse horizontal bands, then left to right) for Tab.</summary>
    /// <summary>Windows the keyboard can select: a stack counts once (its shown card).</summary>
    List<Item> Navigable() => Layoutable().Where(i => i.Stack == null || i == i.Stack.Shown).ToList();

    List<Item> Ordered() => Navigable()
        .OrderBy(i => Math.Round((i.SlotTo.Y + i.SlotTo.Height / 2) / (_view.Height / 5.0)))
        .ThenBy(i => i.SlotTo.X).ToList();

    static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Arrow keys: the nearest window in that direction (works for any layout).</summary>
    void Arrow(int dx, int dy)
    {
        if (SearchMode) { Move(dy, dx); return; }
        var cur = SelectedItem();
        if (cur == null) { Move(0, dx < 0 || dy < 0 ? -1 : 1); return; }
        var c = Center(cur.SlotTo);
        Item? best = null; double bestCost = double.MaxValue;
        foreach (var o in Navigable())
        {
            if (o == cur) continue;
            var oc = Center(o.SlotTo);
            double vx = oc.X - c.X, vy = oc.Y - c.Y;
            double along = dx != 0 ? vx * dx : vy * dy, perp = dx != 0 ? Math.Abs(vy) : Math.Abs(vx);
            if (along <= 1) continue;
            double cost = along + 2 * perp;
            if (cost < bestCost) { bestCost = cost; best = o; }
        }
        if (best != null) SelectItem(best);
    }

    Item? SelectedItem()
    {
        if (SearchMode || _sel < 0) return null;
        var o = Ordered();
        return _sel < o.Count ? o[_sel] : null;
    }

    void SelectItem(Item? it)
    {
        var prev = SelectedItem();
        _sel = it == null ? -1 : Ordered().IndexOf(it);
        if (prev != null && prev != it) { prev.LeaveAt = Now; }
        if (it != null) ShowOverlayFor(it);
        StartTicking();
    }

    void ShowOverlayFor(Item it) { it.OverlayShown = true; it.LeaveAt = -1; StartTicking(); }

    void Move(int dRow, int dCol)
    {
        if (SearchMode)
        {
            if (_results.Count == 0) return;
            int n = _results.Count;
            _sel = _sel < 0 ? 0 : dRow != 0 ? Math.Clamp(_sel + dRow * _columns, 0, n - 1) : (_sel + dCol + n) % n;
            UpdateSelection();
            return;
        }
        var o = Ordered();
        if (o.Count == 0) return;
        var cur = SelectedItem();
        if (cur == null) { SelectItem(dCol < 0 || dRow < 0 ? o[^1] : o[0]); return; }
        if (dCol != 0)
        {
            int i = (o.IndexOf(cur) + dCol + o.Count) % o.Count;
            SelectItem(o[i]);
        }
        else
        {
            int targetRow = cur.Row + dRow;
            var inRow = o.Where(x => x.Row == targetRow).ToList();
            if (inRow.Count == 0) return;
            double cx = cur.SlotTo.X + cur.SlotTo.Width / 2;
            SelectItem(inRow.OrderBy(x => Math.Abs(x.SlotTo.X + x.SlotTo.Width / 2 - cx)).First());
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_state is State.Closing or State.Hidden) { e.Handled = true; return; }
        if (_pickerOpen) return;   // the app picker of the dock owns the keyboard
        if (SearchMode && e.Key != Key.Escape && SearchKey(e)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                InputLog($"Esc received (state {_state}, search empty={_search.Text.Length == 0})");
                if (_search.Text.Length > 0) _search.Text = ""; else BeginClose(IntPtr.Zero);
                e.Handled = true; break;
            case Key.Enter: ActivateSelection(); e.Handled = true; break;
            case Key.Right: Arrow(1, 0); e.Handled = true; break;
            case Key.Left: Arrow(-1, 0); e.Handled = true; break;
            case Key.Down: Arrow(0, 1); e.Handled = true; break;
            case Key.Up: Arrow(0, -1); e.Handled = true; break;
            case Key.PageDown: if (SelectedItem()?.Stack is { } sd) RotateStack(sd, 1); e.Handled = true; break;
            case Key.PageUp: if (SelectedItem()?.Stack is { } su) RotateStack(su, -1); e.Handled = true; break;
            case Key.Tab: Move(0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control:
                if (SelectedItem() is { } it) RequestClose(it);
                e.Handled = true; break;
        }
    }

    Item? HitItem(Point pDip)
    {
        double x = pDip.X * _scale, y = pDip.Y * _scale;
        var pt = new Point(x, y);
        // topmost-last; include the icon strip hanging off the bottom edge so the overlay doesn't flicker
        return Layoutable().LastOrDefault(i => !i.StackHidden && (
            i.CurScaled.Contains(pt) ||
            ((i.Stack == null || i == i.Stack.Shown) && new Rect(i.Cur.X + i.Cur.Width / 2 - IconSize * _scale / 2, i.Cur.Bottom - IconOverlap * IconSize * _scale, IconSize * _scale, IconSize * _scale).Contains(pt))));
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_firstMouseLogged && _state != State.Hidden)
        {
            _firstMouseLogged = true;
            InputLog($"first mouse move (state {_state}, captured={Mouse.Captured?.GetType().Name ?? "none"}, active={IsActive})");
        }
        if (SearchMode && !_hoverArmed && (e.GetPosition(_root) - _searchMousePos).Length >= 3) _hoverArmed = true;
        if (_gestureNav && (e.GetPosition(_root) - _mouseDip).Length >= 3) _gestureNav = false;
        if (_state != State.Open || SearchMode) return;
        _mouseDip = e.GetPosition(_root);
        var hit = HitItem(_mouseDip);
        // the strip of a lower card in a stack: dwell on it and the card comes forward; the frame/chrome stay on the shown card
        Item? it = hit;
        Item? cand = null;
        if (hit?.Stack != null && hit != hit.Stack.Shown) { it = hit.Stack.Shown; if (InStrip(hit, _mouseDip)) cand = hit; }
        if (cand != _candidate) { _candidate = cand; _candidateSince = Now; if (cand != null) StartTicking(); }
        if (it == _hovered) return;
        if (_hovered != null) _hovered.LeaveAt = Now;
        _hovered = it;
        if (it != null) { SelectItem(it); }
        StartTicking();
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state != State.Open) return;
        if (e.OriginalSource is DependencyObject d && (IsInside(d, _bar) || IsInside(d, _resultsHost) || IsInside(d, _dock))) return;
        if (e.OriginalSource is DependencyObject d2 && (Layoutable().Any(i => i.Close != null && IsInside(d2, i.Close)) || _stacks.Any(st => IsInside(d2, st.Badge)))) return;
        if (SearchMode) { if (e.ChangedButton == MouseButton.Left) _search.Text = ""; return; }
        var it = HitItem(e.GetPosition(_root));
        if (e.ChangedButton == MouseButton.Left) { if (it != null) ActivateWindow(it); else BeginClose(IntPtr.Zero); }
        else if (e.ChangedButton == MouseButton.Middle && it != null) RequestClose(it);
    }

    static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (DependencyObject? c = d; c != null; c = (c is Visual or System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
            if (ReferenceEquals(c, ancestor)) return true;
        return false;
    }

    // ---------- actions ----------

    void ActivateSelection()
    {
        if (SearchMode)
        {
            int i = _sel >= 0 ? _sel : 0;
            if (i < _results.Count) OpenItem(_results[i], NewWin(Keyboard.Modifiers));   // Ctrl/Shift+Enter: new instance (config Search.Keys)
        }
        else if (SelectedItem() is { } it) ActivateWindow(it);
    }

    void ActivateWindow(Item it) => BeginClose(it.W.Hwnd);

    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// The topmost window of a running app that matches this start-menu entry, by the dock's rules (AppUserModelID, exe hints, then name): by
    /// process/app name alone a running Edge (msedge), Terminal (WindowsTerminal) or Teams (ms-teams) was not found and got started again.
    /// </summary>
    IntPtr FindRunningWindow(AppEntry app)
    {
        if (_aumids.Count > 300) _aumids.Clear();
        var wins = _cache.Get(out _).Select(w => new DockWindow(w.Hwnd, w.Title, w.ExePath ?? "", Aumid(w.Hwnd), w.Minimized, w.Icon, w.ProcName)).ToList();   // front-to-back order
        return DockBuilder.FindWindow(app, wins, _apps.All);
    }

    void LaunchApp(AppEntry app, bool forceNew = false)
    {
        var running = forceNew ? IntPtr.Zero : FindRunningWindow(app);
        if (running != IntPtr.Zero) { BeginClose(running); return; }   // like GNOME: already running -> go to its window
        var before = new HashSet<IntPtr>(_cache.Get(out _).Select(w => w.Hwnd));
        BeginClose(IntPtr.Zero);
        AppCatalog.Launch(app);
        BringNewWindowToFront(before);
    }

    /// <summary>Windows often puts a freshly started program behind the others (the launch goes through Explorer): bring the first new window forward.</summary>
    void BringNewWindowToFront(HashSet<IntPtr> before)
    {
        long until = Environment.TickCount64 + 8000;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            try
            {
                if (Environment.TickCount64 > until) { t.Stop(); return; }
                if (_state != State.Hidden) return;   // still in the overview
                foreach (var w in _cache.Get(out _))
                {
                    if (before.Contains(w.Hwnd)) continue;
                    t.Stop();
                    if (Native.GetForegroundWindow() != w.Hwnd) Native.ForceForeground(w.Hwnd);
                    Log.Debug($"launch: brought new window of {w.ProcName} to the front");
                    return;
                }
            }
            catch { t.Stop(); }
        };
        t.Start();
    }

    /// <summary>GNOME: the preview disappears at once; the others only reshuffle after a short delay so the layout does not jump under the pointer.</summary>
    void RequestClose(Item it)
    {
        if (it.Closing) return;
        Native.PostMessage(it.W.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        it.Closing = true; it.OverlayShown = false;
        if (_hovered == it) _hovered = null;
        _sel = -1;
        _repositionAt = Now + RepositionDelayMs;
        StartTicking();
    }

    void ResolveClosings()
    {
        _repositionAt = -1;
        foreach (var it in _items.Where(i => i.Closing).ToList())
        {
            bool gone = !Native.IsWindow(it.W.Hwnd) || !Native.IsWindowVisible(it.W.Hwnd);
            if (!gone) { it.Closing = false; continue; }   // the app refused to close (e.g. "save changes?")
            if (it.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(it.Thumb);
            foreach (UIElement? el in new UIElement?[] { it.Icon, it.Title, it.Close, it.Card }) if (el != null) _canvas.Children.Remove(el);
            _items.Remove(it);
            if (it.Stack != null) { it.Stack.Members.Remove(it); if (it.Stack.Raised == it) it.Stack.Raised = null; }
        }
        Relayout(animate: true);
        ApplyStackOrder();
    }
}

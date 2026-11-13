using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// The panel under the clock: time with seconds, month calendar, world clocks on the left; Agenda + Mail, Teams + Notifications
/// as four sections that are all visible at once. The data is already warm (see <see cref="PanelData"/>), then refreshed on open.
/// </summary>
internal sealed class ClockPanel
{
    static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));
    static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)));
    static readonly Brush Faint = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)));
    static readonly Brush HoverBg = Frozen(new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255)));
    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>A titled, scrollable block with small actions on the right of its title.</summary>
    sealed class Section
    {
        public readonly StackPanel List = new();
        public readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        public readonly TextBlock Title = new() { Foreground = Accent, FontFamily = BarUi.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        public readonly FrameworkElement View;
        public string Sig = "";

        public Section(double height)
        {
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Actions, 1);
            head.Children.Add(Title); head.Children.Add(Actions);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = height, Content = List };
            var sp = new StackPanel();
            sp.Children.Add(head);
            sp.Children.Add(new Border { Height = 1, Background = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255))), Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(scroll);
            View = sp;
        }

        public void Begin(string title, params (string text, Action click)[] actions)
        {
            Title.Text = title;
            List.Children.Clear();
            Actions.Children.Clear();
            foreach (var (t, c) in actions) Actions.Children.Add(Link(t, c, 12));
        }

        public void Note(string text) => List.Children.Add(new TextBlock { Text = text, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, Margin = new Thickness(2, 6, 2, 6), TextWrapping = TextWrapping.Wrap });
    }

    readonly BarContext _ctx;
    readonly Config _cfg;
    public FrameworkElement Root { get; }
    readonly TextBlock _time = BarUi.Txt("", 40, Brushes.White, FontWeights.Light);
    readonly TextBlock _date = BarUi.Txt("", 13, BarUi.Dim, FontWeights.Normal);
    readonly TextBlock _monthTitle = BarUi.Txt("", 14);
    readonly Grid _grid = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly Section _agenda = new(222), _mail = new(176), _teams = new(150), _notes = new(248);
    readonly List<(TextBlock label, TimeZoneInfo tz)> _zones = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<int, Dictionary<DateTime, string>> _holidays = new();
    DateTime _month, _selected;
    bool _closed, _showIgnored, _toastBusy;
    List<MeetingItem> _meetings = new();
    string _calStatus = "Loading…";
    DateTime _loadedFrom = DateTime.MaxValue, _loadedTo = DateTime.MinValue;
    InboxResult? _inbox;
    int _toastTick;

    public ClockPanel(BarContext ctx)
    {
        _ctx = ctx; _cfg = ctx.Cfg;
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);

        // ----- left column -----
        var left = new StackPanel { Width = 304 };
        left.Children.Add(_time);
        left.Children.Add(_date);
        left.Children.Add(new Border { Height = 10 });

        var nav = new Grid();
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prev = Link("‹", () => ShiftMonth(-1), 18); var next = Link("›", () => ShiftMonth(1), 18);
        var today = Link("Today", GoToday, 12);
        _monthTitle.HorizontalAlignment = HorizontalAlignment.Left; _monthTitle.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(prev, 0); Grid.SetColumn(_monthTitle, 1); Grid.SetColumn(today, 2); Grid.SetColumn(next, 3);
        nav.Children.Add(prev); nav.Children.Add(_monthTitle); nav.Children.Add(today); nav.Children.Add(next);
        left.Children.Add(nav);
        left.Children.Add(_grid);

        var extra = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var z in _cfg.WorldClocks)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(z.TimeZoneId);
                var t = BarUi.Txt("", 13);
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(BarUi.Txt(z.Name, 13, BarUi.Dim, FontWeights.Normal));
                t.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(t);
                extra.Children.Add(row);
                _zones.Add((t, tz));
            }
            catch { }
        }
        var sun = WeatherProbe.LoadCache();
        if (sun?.Sunrise != null && sun.Sunset != null && sun.Sunrise.Value.Date == DateTime.Today)
            extra.Children.Add(BarUi.Txt($"☀ {sun.Sunrise:HH:mm}   🌙 {sun.Sunset:HH:mm}", 12, BarUi.Dim, FontWeights.Normal));
        left.Children.Add(extra);

        // ----- two columns of sections -----
        var mid = new StackPanel { Width = 390 };
        mid.Children.Add(_agenda.View); mid.Children.Add(new Border { Height = 14 }); mid.Children.Add(_mail.View);
        var right = new StackPanel { Width = 390 };
        right.Children.Add(_teams.View); right.Children.Add(new Border { Height = 14 }); right.Children.Add(_notes.View);

        var root = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(26), GridLength.Auto, new GridLength(22), GridLength.Auto })
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Grid.SetColumn(left, 0); Grid.SetColumn(mid, 2); Grid.SetColumn(right, 4);
        root.Children.Add(left); root.Children.Add(mid); root.Children.Add(right);
        Root = root;

        // what the background refresh already knows is shown at once; then everything is refreshed
        if (PanelData.Calendar is { Status: "ok" } c)
        {
            var (f, t) = PanelData.DefaultRange();
            _meetings = c.Items; _calStatus = "ok"; _loadedFrom = f; _loadedTo = t;
        }
        _inbox = PanelData.Inbox;
        UpdateClock();
        RebuildMonth();
        RenderAll();
        LoadCalendar(true);
        PanelData.RefreshMail(_cfg);
        PanelData.Changed += OnData;
        LoadToasts();
        _tick.Tick += (_, _) => { UpdateClock(); if (_confirmIgnore != null && Environment.TickCount64 > _confirmUntil) { _confirmIgnore = null; RenderNotifications(); } if (++_toastTick % 8 == 0) LoadToasts(); };
        _tick.Start();
    }

    public void Stop() { _closed = true; _tick.Stop(); PanelData.Changed -= OnData; }

    void OnData()
    {
        if (_closed) return;
        bool any = false;
        if (PanelData.Inbox != null && PanelData.Inbox != _inbox) { _inbox = PanelData.Inbox; any = true; }
        if (any) RenderMail();
        string ts = string.Join(",", (PanelData.Toasts ?? new()).Select(t => t.Id));
        if (ts != _notes.Sig) { _notes.Sig = ts; RenderNotifications(); RenderTeams(); }
    }

    void RenderAll() { RenderAgenda(); RenderMail(); RenderTeams(); RenderNotifications(); }

    // ---------------- clock ----------------

    void UpdateClock()
    {
        var n = DateTime.Now;
        _time.Text = n.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        _date.Text = n.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) + $"  ·  week {ISOWeek.GetWeekOfYear(n)}";
        foreach (var (label, tz) in _zones)
        {
            var t = TimeZoneInfo.ConvertTime(n, tz);
            double diff = (tz.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow)).TotalHours;
            string d = diff == 0 ? "" : $"  ({(diff > 0 ? "+" : "")}{diff:0.#} h)";
            label.Text = t.ToString("HH:mm", CultureInfo.CurrentCulture) + d + (t.Date != n.Date ? (t.Date > n.Date ? "  +1 d" : "  −1 d") : "");
        }
    }

    // ---------------- month grid ----------------

    Dictionary<DateTime, string> HolidaysOf(int year)
    {
        if (!_holidays.TryGetValue(year, out var h)) _holidays[year] = h = Holidays.For(_cfg.HolidayCountry, year);
        return h;
    }

    void ShiftMonth(int delta)
    {
        _month = _month.AddMonths(delta);
        RebuildMonth();
        LoadCalendar(false);
    }

    void GoToday()
    {
        _selected = DateTime.Today; _month = new DateTime(_selected.Year, _selected.Month, 1);
        RebuildMonth(); LoadCalendar(false); RenderAgenda();
    }

    void RebuildMonth()
    {
        _monthTitle.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        for (int c = 0; c < 8; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(28) : new GridLength(39) });
        for (int r = 0; r < 7; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r == 0 ? 22 : 31) });

        var ci = CultureInfo.CurrentCulture;
        var order = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        Put(BarUi.Txt("W", 11, Faint, FontWeights.Normal), 0, 0);
        for (int c = 0; c < 7; c++) Put(BarUi.Txt(ci.DateTimeFormat.GetShortestDayName(order[c]), 11, Faint, FontWeights.Normal), 0, c + 1);

        var first = new DateTime(_month.Year, _month.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        for (int r = 0; r < 6; r++)
        {
            Put(BarUi.Txt(ISOWeek.GetWeekOfYear(start.AddDays(r * 7)).ToString(), 11, Faint, FontWeights.Normal), r + 1, 0);
            for (int c = 0; c < 7; c++)
            {
                var day = start.AddDays(r * 7 + c);
                var cell = DayCell(day, day.Month == _month.Month);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                _grid.Children.Add(cell);
            }
        }
    }

    void Put(FrameworkElement e, int row, int col)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center; e.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(e, row); Grid.SetColumn(e, col); _grid.Children.Add(e);
    }

    bool OnDay(MeetingItem m, DateTime day) => m.Start.Date <= day.Date && (m.AllDay ? m.End.Date > day.Date : m.End.Date >= day.Date);

    Border DayCell(DateTime day, bool inMonth)
    {
        bool today = day.Date == DateTime.Today, sel = day.Date == _selected.Date;
        HolidaysOf(day.Year).TryGetValue(day.Date, out var hol);
        Brush fg = today ? Brushes.White : !inMonth ? Faint : (hol != null || day.DayOfWeek == DayOfWeek.Sunday) ? Red : Brushes.White;
        var tb = BarUi.Txt(day.Day.ToString(), 13, fg, today ? FontWeights.Bold : FontWeights.Normal);
        tb.HorizontalAlignment = HorizontalAlignment.Center; tb.VerticalAlignment = VerticalAlignment.Center;
        var g = new Grid(); g.Children.Add(tb);
        if (_meetings.Any(m => OnDay(m, day)))
            g.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = today ? Brushes.White : Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        var b = new Border
        {
            Margin = new Thickness(1), CornerRadius = new CornerRadius(8), Child = g, Cursor = Cursors.Hand,
            Background = today ? Accent : Brushes.Transparent,
            BorderBrush = sel && !today ? new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)) : Brushes.Transparent, BorderThickness = new Thickness(1),
            ToolTip = hol
        };
        if (!today) { b.MouseEnter += (_, _) => b.Background = HoverBg; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent; }
        b.MouseLeftButtonUp += (_, _) =>
        {
            _selected = day.Date;
            if (day.Month != _month.Month) { _month = new DateTime(day.Year, day.Month, 1); LoadCalendar(false); }
            RebuildMonth(); RenderAgenda();
        };
        return b;
    }

    // ---------------- small helpers ----------------

    static FrameworkElement Link(string text, Action click, double size)
    {
        var b = new Border { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = BarUi.Txt(text, size) };
        b.MouseEnter += (_, _) => b.Background = HoverBg; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static Border Row(UIElement child, Action? click)
    {
        var row = new Border { Child = child, Padding = new Thickness(4, 5, 4, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        if (click != null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = HoverBg; row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => click();
        }
        return row;
    }

    static void OpenUrl(string url) { Popover.CloseAny(); try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    // ---------------- agenda ----------------

    void LoadCalendar(bool force)
    {
        var from = _month.AddDays(-8); var to = _month.AddMonths(1).AddDays(8);
        if (!force && from >= _loadedFrom && to <= _loadedTo) return;
        OutlookData.Calendar(from, to, force, res => _ctx.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            string sig = res.Status + string.Join(",", res.Items.Select(m => m.Id + m.Start.Ticks));
            bool changed = sig != _agenda.Sig;
            _agenda.Sig = sig;
            _meetings = res.Items; _calStatus = res.Status;
            if (res.Status == "ok") { _loadedFrom = from; _loadedTo = to; }
            if (changed) { RebuildMonth(); RenderAgenda(); RenderTeams(); }
        })));
    }

    IEnumerable<MeetingItem> MeetingsOn(DateTime day) => _meetings.Where(m => OnDay(m, day)).OrderBy(m => !m.AllDay).ThenBy(m => m.Start);

    FrameworkElement MeetingRow(MeetingItem m)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bool now = !m.AllDay && m.Start <= DateTime.Now && m.End > DateTime.Now;
        var time = BarUi.Txt(m.AllDay ? "All day" : $"{m.Start:HH:mm}–{m.End:HH:mm}", 12.5, now ? Accent : BarUi.Dim, now ? FontWeights.SemiBold : FontWeights.Normal);
        time.VerticalAlignment = VerticalAlignment.Top; time.Margin = new Thickness(2, 2, 0, 0);
        var what = new StackPanel();
        what.Children.Add(new TextBlock { Text = m.Title, Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(m.Location) && m.JoinUrl == null) what.Children.Add(new TextBlock { Text = m.Location, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(time, 0); Grid.SetColumn(what, 1);
        g.Children.Add(time); g.Children.Add(what);
        if (m.JoinUrl != null)
        {
            var url = m.JoinUrl;
            var join = BarUi.Button("Join", () => OpenUrl(url));
            join.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(join, 2); g.Children.Add(join);
        }
        return Row(g, () => { OutlookData.Open(m.Id); Popover.CloseAny(); });
    }

    void RenderAgenda()
    {
        string day = _selected.Date == DateTime.Today ? "Today · " + _selected.ToString("d MMM", CultureInfo.CurrentCulture) : _selected.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        _agenda.Begin("Agenda · " + day, ("+ Meeting", () => { OutlookData.NewMeeting(); Popover.CloseAny(); }));
        if (_calStatus != "ok") _agenda.Note(_calStatus == "Loading…" ? "Loading…" : _calStatus + ". The agenda needs classic Outlook to be running.");
        else
        {
            var items = MeetingsOn(_selected).ToList();
            if (items.Count == 0) _agenda.Note("Nothing planned.");
            foreach (var m in items) _agenda.List.Children.Add(MeetingRow(m));
        }
    }

    // ---------------- mail ----------------

    void RenderMail()
    {
        var res = _inbox;
        string title = res == null ? "Mail" : res.Status != "ok" ? "Mail" : res.Unread == 0 ? "Mail · no unread" : $"Mail · {res.Unread} unread";
        _mail.Begin(title, ("+ Mail", () => { OutlookData.NewMail(); Popover.CloseAny(); }), ("Refresh", () => PanelData.RefreshMail(_cfg)));
        if (res == null) { _mail.Note("Loading…"); return; }
        if (res.Status != "ok") { _mail.Note(res.Status + ". Mail needs classic Outlook to be running."); return; }
        foreach (var m in res.Items)
        {
            var sp = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var from = new TextBlock { Text = m.From, Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var when = BarUi.Txt(m.Received.Date == DateTime.Today ? m.Received.ToString("HH:mm") : m.Received.ToString("d MMM"), 12, BarUi.Dim, FontWeights.Normal);
            when.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(when, 1); top.Children.Add(from); top.Children.Add(when);
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = m.Subject, Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            if (m.Preview.Length > 0) sp.Children.Add(new TextBlock { Text = m.Preview, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34 });
            var id = m.Id;
            _mail.List.Children.Add(Row(sp, () => { OutlookData.Open(id); Popover.CloseAny(); PanelData.RefreshMailSoon(_cfg); }));
        }
        if (res.Items.Count == 0) _mail.Note("Nothing unread.");
        else if (res.Unread > res.Items.Count) _mail.Note($"…and {res.Unread - res.Items.Count} more.");
    }

    // ---------------- notifications + Teams ----------------

    void LoadToasts()
    {
        if (_toastBusy) return;
        _toastBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            List<ToastItem>? list = null;
            try { list = NotificationProbe.List(); } catch (Exception e) { Log.Write("notifications: " + e.GetType().Name); }
            _ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                _toastBusy = false;
                if (_closed || list == null) return;
                PanelData.SetToasts(list);
                string sig = string.Join(",", list.Select(t => t.Id));
                if (sig != _notes.Sig || _notes.List.Children.Count == 0) { _notes.Sig = sig; RenderNotifications(); RenderTeams(); }
            }));
        });
    }

    FrameworkElement ToastRow(ToastItem t)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = t.Title.Length > 0 ? t.Title : t.App, Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (t.Body.Length > 0) sp.Children.Add(new TextBlock { Text = t.Body, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 50 });
        sp.Children.Add(BarUi.Txt(t.Time.Date == DateTime.Today ? t.Time.ToString("HH:mm") : t.Time.ToString("d MMM HH:mm"), 11.5, Faint, FontWeights.Normal));
        var x = Link("✕", () => { NotificationProbe.Remove(t.Id); PanelData.RemoveToast(t.Id); RenderNotifications(); RenderTeams(); }, 12);
        x.VerticalAlignment = VerticalAlignment.Top; x.ToolTip = "Dismiss";
        Grid.SetColumn(x, 1); g.Children.Add(sp); g.Children.Add(x);
        return Row(g, null);
    }

    void RenderTeams()
    {
        var all = PanelData.VisibleToasts(_cfg);
        var msgs = all?.Where(NotificationProbe.IsTeams).ToList();
        var actions = new List<(string, Action)> { ("Open Teams", () => OpenUrl("msteams:")) };
        if (msgs != null && msgs.Count > 0) actions.Add(("Clear", () => { foreach (var t in msgs) { NotificationProbe.Remove(t.Id); PanelData.RemoveToast(t.Id); } RenderNotifications(); RenderTeams(); }));
        _teams.Begin(msgs is { Count: > 0 } ? $"Teams · {msgs.Count} message{(msgs.Count == 1 ? "" : "s")}" : "Teams", actions.ToArray());
        if (msgs == null) _teams.Note("Loading…");
        else if (msgs.Count == 0) _teams.Note("No new Teams notifications.");
        else foreach (var t in msgs.Take(5)) _teams.List.Children.Add(ToastRow(t));
        var today = MeetingsOn(DateTime.Today).Where(m => m.JoinUrl != null && m.End > DateTime.Now).Take(3).ToList();
        foreach (var m in today) _teams.List.Children.Add(MeetingRow(m));
    }

    string? _confirmIgnore;
    long _confirmUntil;

    void RenderNotifications()
    {
        bool on = DndProbe.ToastsEnabled();
        var all = PanelData.VisibleToasts(_cfg);
        var ignored = _cfg.IgnoredNotificationApps;
        int hidden = (PanelData.Toasts?.Count ?? 0) - (all?.Count ?? 0);
        var actions = new List<(string, Action)>();
        if (all is { Count: > 0 }) actions.Add(("Clear all", () => { foreach (var t in all.ToList()) { NotificationProbe.Remove(t.Id); PanelData.RemoveToast(t.Id); } RenderNotifications(); RenderTeams(); }));
        actions.Add((on ? "Do not disturb" : "Allow again", () => { DndProbe.Set(!on); RenderNotifications(); }));
        if (ignored.Length > 0) actions.Add(((_showIgnored ? "▾ " : "") + $"Ignored ({ignored.Length})", () => { _showIgnored = !_showIgnored; RenderNotifications(); }));
        _notes.Begin((on ? "Notifications" : "Notifications · OFF") + (all is { Count: > 0 } ? $" · {all.Count}" : ""), actions.ToArray());

        // the list of ignored apps, opened from the header: un-ignore with one click
        if (_showIgnored && ignored.Length > 0)
        {
            var box = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            box.Children.Add(BarUi.Txt("Ignored apps (hidden here, still shown by Windows)", 11.5, BarUi.Dim, FontWeights.Normal));
            foreach (var name in ignored)
            {
                var r = new Grid { Margin = new Thickness(8, 2, 0, 0) };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(BarUi.Txt(name, 13, Brushes.White, FontWeights.Normal));
                var un = Link("Show again", () => UnignoreApp(name), 12); Grid.SetColumn(un, 1); r.Children.Add(un);
                box.Children.Add(r);
            }
            _notes.List.Children.Add(new Border { Child = box, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = HoverBg, Margin = new Thickness(0, 0, 0, 4) });
        }

        if (all == null) _notes.Note("Loading…");
        else if (all.Count == 0) _notes.Note(hidden > 0 ? $"Nothing waiting. ({hidden} from ignored apps hidden.)" : "Nothing waiting.");
        else
            foreach (var grp in all.GroupBy(t => t.App).OrderByDescending(g => g.Max(t => t.Time)))
            {
                string app = grp.Key;
                var head = new Grid { Margin = new Thickness(4, 8, 0, 2) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bool asking = string.Equals(_confirmIgnore, app, StringComparison.OrdinalIgnoreCase);
                if (asking)
                {
                    head.Children.Add(new TextBlock { Text = $"Ignore {app}?", Foreground = Red, FontFamily = BarUi.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                    var btns = new StackPanel { Orientation = Orientation.Horizontal };
                    btns.Children.Add(Link("Yes, ignore", () => IgnoreApp(app), 12));
                    btns.Children.Add(Link("Cancel", () => { _confirmIgnore = null; RenderNotifications(); }, 12));
                    Grid.SetColumn(btns, 1); head.Children.Add(btns);
                }
                else
                {
                    head.Children.Add(new TextBlock { Text = $"{app}  ({grp.Count()})", Foreground = Accent, FontFamily = BarUi.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                    var ign = Link("Ignore app…", () => { _confirmIgnore = app; _confirmUntil = Environment.TickCount64 + 6000; RenderNotifications(); }, 11);
                    ign.ToolTip = $"Hide notifications from {app} in this panel (Windows still shows them). You can undo it under Ignored.";
                    Grid.SetColumn(ign, 1); head.Children.Add(ign);
                }
                _notes.List.Children.Add(head);
                foreach (var t in grp.Take(5)) _notes.List.Children.Add(ToastRow(t));
                if (grp.Count() > 5) _notes.Note($"…and {grp.Count() - 5} more from {grp.Key}.");
            }
        if (hidden > 0 && all is { Count: > 0 }) _notes.List.Children.Add(Link($"{hidden} hidden from ignored apps", () => { _showIgnored = true; RenderNotifications(); }, 11));
    }

    void IgnoreApp(string app)
    {
        _confirmIgnore = null;
        _cfg.IgnoredNotificationApps = _cfg.IgnoredNotificationApps.Append(app).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _cfg.Save();
        RenderNotifications(); RenderTeams();
    }

    void UnignoreApp(string app)
    {
        _cfg.IgnoredNotificationApps = _cfg.IgnoredNotificationApps.Where(a => !string.Equals(a, app, StringComparison.OrdinalIgnoreCase)).ToArray();
        _cfg.Save();
        if (_cfg.IgnoredNotificationApps.Length == 0) _showIgnored = false;
        RenderNotifications(); RenderTeams();
    }
}

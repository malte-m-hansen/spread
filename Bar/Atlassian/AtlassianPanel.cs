using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// The Jira/Confluence panel: sprint pulse, OKRs, my work, needs attention, changes since the last look, Confluence activity and quick open/search.
/// All sections are visible at once. Read-only: rows only open the browser.
/// </summary>
internal sealed class AtlassianPanel
{
    static readonly Brush Accent = Fz(Color.FromRgb(0x4C, 0x8D, 0xFF));
    static readonly Brush Red = Fz(Color.FromRgb(0xFF, 0x8A, 0x80));
    static readonly Brush Amber = Fz(Color.FromRgb(0xFF, 0xB3, 0x40));
    static readonly Brush Green = Fz(Color.FromRgb(0x4C, 0xD9, 0x64));
    static readonly Brush Faint = Fz(Color.FromArgb(0x60, 255, 255, 255));
    static readonly Brush HoverBg = Fz(Color.FromArgb(0x28, 255, 255, 255));
    static readonly Brush Track = Fz(Color.FromArgb(0x30, 255, 255, 255));
    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    sealed class Section
    {
        public readonly StackPanel List = new();
        public readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        public readonly TextBlock Title = new() { Foreground = Accent, FontFamily = BarUi.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        public readonly FrameworkElement View;
        public Section(double height)
        {
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Actions, 1);
            head.Children.Add(Title); head.Children.Add(Actions);
            var sp = new StackPanel();
            sp.Children.Add(head);
            sp.Children.Add(new Border { Height = 1, Background = Track, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = height, Content = List });
            View = sp;
        }
        public void Begin(string title) { Title.Text = title; List.Children.Clear(); Actions.Children.Clear(); }
        public void Note(string t, Brush? fg = null) => List.Children.Add(new TextBlock { Text = t, Foreground = fg ?? BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, Margin = new Thickness(2, 5, 2, 5), TextWrapping = TextWrapping.Wrap });
        public void Sub(string t) => List.Children.Add(new TextBlock { Text = t, Foreground = Faint, FontFamily = BarUi.Text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 2) });
    }

    readonly BarContext _ctx;
    readonly Config _cfg;
    readonly AtlassianConfig _a;
    public FrameworkElement Root { get; }
    readonly ContentControl _host = new();
    readonly Section _sprint = new(330), _okr = new(250), _work = new(262), _attn = new(318), _changes = new(250), _conf = new(205), _foryou = new(140), _results = new(150);
    readonly TextBox _search = new();
    readonly TextBlock _statusText = BarUi.Txt("", 12, BarUi.Dim, FontWeights.Normal);
    readonly DateTime _openedAt = DateTime.Now, _prevLook;
    string _sig = "";
    bool _closed, _settingsOpen;
    int _searchSeq;
    PasswordBox? _tokenBox;
    string? _columnFilter;
    TextBlock? _connectMsg;

    public AtlassianPanel(BarContext ctx)
    {
        _ctx = ctx; _cfg = ctx.Cfg; _a = _cfg.Atlassian;
        _prevLook = AtlassianData.LastLook;
        Root = _host;
        _results.View.Visibility = Visibility.Collapsed;   // once: a rebuild for new data keeps search results the user is reading
        Build();
        AtlassianData.Changed += OnData;
        AtlassianData.RefreshIfOlder(60);
    }

    public void Closed()
    {
        _closed = true;
        AtlassianData.Changed -= OnData;
        if ((DateTime.Now - _openedAt).TotalSeconds >= 2 && AtlassianData.Status == "ok") AtlassianData.MarkLooked();
    }

    void OnData()
    {
        if (_closed) return;
        if (_settingsOpen) return;   // a rebuild would throw away the settings being edited; closing them rebuilds with the newest data
        string sig = Signature();
        if (sig == _sig) { UpdateStatusText(); return; }
        Build();
    }

    string Signature() =>
        AtlassianData.Status + "|" + (AtlassianData.Sprint?.Issues.Count ?? -1) + "|" + (AtlassianData.Sprint?.Issues.Count(i => i.Done) ?? -1) + "|" + AtlassianData.MyWork.Count + "|" +
        string.Join(",", AtlassianData.MyWork.Select(w => w.Key + w.Status)) + "|" + AtlassianData.Changes.Count + "|" + (AtlassianData.Changes.FirstOrDefault()?.When.Ticks ?? 0) + "|" +
        (AtlassianData.Okr?.Objectives.Sum(o => o.KeyResults.Sum(k => k.Children * 100 + k.ChildrenDone)) ?? 0) + "|" + AtlassianData.ConfPages.Count + (AtlassianData.ConfPages.FirstOrDefault()?.When.Ticks ?? 0) + "|" +
        AtlassianData.ConfComments.Count + (AtlassianData.ConfComments.FirstOrDefault()?.When.Ticks ?? 0) + "|" + AtlassianData.ConfMentions.Count + "|" + string.Join(",", (AtlassianData.Sprint?.Issues ?? new()).Select(i => i.Status.Length + i.Key.Length + (i.AssigneeId?.Length ?? 0)));

    // ---------------- helpers ----------------

    static FrameworkElement Link(string text, Action click, double size = 12)
    {
        var b = new Border { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = BarUi.Txt(text, size) };
        b.MouseEnter += (_, _) => b.Background = HoverBg; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static Border Row(UIElement child, string? url, Brush? bar = null)
    {
        var row = new Border { Child = child, Padding = new Thickness(5, 4, 5, 4), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = bar ?? Brushes.Transparent };
        if (url != null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = HoverBg; row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => OpenUrl(url);
        }
        return row;
    }

    string IssueUrl(string key) => $"https://{AtlassianData.Site}/browse/{key}";

    static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
            Popover.CloseAny();
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
    }

    static Brush ColumnColor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("done")) return Green;
        if (n.Contains("blocked")) return Red;
        if (n.Contains("review")) return Fz(Color.FromRgb(0xB3, 0x8A, 0xFF));
        if (n.Contains("qa")) return Fz(Color.FromRgb(0xFF, 0xD1, 0x66));
        if (n.Contains("dev")) return Fz(Color.FromRgb(0x4D, 0xD0, 0xE1));
        if (n.Contains("progress")) return Accent;
        return Fz(Color.FromRgb(0x9A, 0xA0, 0xA6));
    }

    static FrameworkElement Bar(double fraction, double width, Brush fill, double height = 6, double? marker = null)
    {
        var g = new Grid { Width = width, Height = height };
        g.Children.Add(new Border { Background = Track, CornerRadius = new CornerRadius(height / 2) });
        g.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(0, Math.Min(1, fraction)) * width });
        if (marker != null) g.Children.Add(new Border { Background = Brushes.White, Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Math.Clamp(marker.Value, 0, 1) * width - 1, -2, 0, -2) });
        return g;
    }

    TextBlock T(string s, double size = 13, Brush? fg = null, FontWeight? w = null, bool trim = true)
    {
        var t = new TextBlock { Text = s, Foreground = fg ?? Brushes.White, FontFamily = BarUi.Text, FontSize = size, FontWeight = w ?? FontWeights.Normal };
        if (trim) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    static string When(DateTime d) => d.Date == DateTime.Today ? d.ToString("HH:mm") : d.Date == DateTime.Today.AddDays(-1) ? "yesterday" : d.ToString("d MMM");

    // ---------------- building ----------------

    void Build()
    {
        _sig = Signature();
        if (AtlassianData.Status is "not connected" or "reconnect") { _results.View.Visibility = Visibility.Collapsed; BuildConnect(); return; }   // no old results after connecting again

        // The section views live as long as the panel: take them out of the previous layout first (Build can run twice in a row, e.g. when new data
        // arrives while a settings view is closing - WPF refuses an element that still has a parent).
        foreach (var e in new FrameworkElement[] { _sprint.View, _okr.View, _work.View, _attn.View, _changes.View, _conf.View, _foryou.View, _results.View, _search, _statusText })
        {
            if (e.Parent is Panel pp) pp.Children.Remove(e);
            else if (e.Parent is ContentControl cc) cc.Content = null;
            else if (e.Parent is Decorator dd) dd.Child = null;
        }

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = BarUi.Txt($"Jira · {_a.Project}   Confluence · {_a.ConfluenceSpace}", 15);
        title.Margin = new Thickness(0, 0, 18, 0);
        _search.Width = 380; _search.Height = 28; _search.Padding = new Thickness(8, 3, 8, 3); _search.FontSize = 13; _search.FontFamily = BarUi.Text;
        _search.Background = Fz(Color.FromRgb(0x2A, 0x2A, 0x2A)); _search.Foreground = Brushes.White; _search.CaretBrush = Brushes.White; _search.BorderBrush = Track;
        _search.HorizontalAlignment = HorizontalAlignment.Left; _search.ToolTip = "Type PROJ-123 (or just 123) and press Enter to open it, or text to search Jira and Confluence";
        _search.PreviewMouseDown -= SearchFocus; _search.PreviewMouseDown += SearchFocus;
        _search.KeyDown -= SearchKey; _search.KeyDown += SearchKey;
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_statusText);
        _statusText.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(Link("Settings", ShowSettings));
        right.Children.Add(Link("Refresh", () => { AtlassianData.RefreshNow(); }));
        right.Children.Add(Link("Open Jira board", () => OpenUrl($"https://{AtlassianData.Site}/jira/software/c/projects/{_a.Project}/boards/{_a.BoardId}")));
        right.Children.Add(Link("Disconnect", () => { AtlassianData.Disconnect(); Build(); }));
        Grid.SetColumn(_search, 1); Grid.SetColumn(right, 2);
        header.Children.Add(title); header.Children.Add(_search); header.Children.Add(right);

        var col1 = new StackPanel { Width = 410 }; col1.Children.Add(_sprint.View); col1.Children.Add(Gap()); col1.Children.Add(_okr.View);
        var col2 = new StackPanel { Width = 410 }; col2.Children.Add(_work.View); col2.Children.Add(Gap()); col2.Children.Add(_attn.View);
        var col3 = new StackPanel { Width = 410 }; col3.Children.Add(_changes.View); col3.Children.Add(Gap()); col3.Children.Add(_conf.View); col3.Children.Add(Gap()); col3.Children.Add(_foryou.View);
        var cols = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(22), GridLength.Auto, new GridLength(22), GridLength.Auto }) cols.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Grid.SetColumn(col1, 0); Grid.SetColumn(col2, 2); Grid.SetColumn(col3, 4);
        cols.Children.Add(col1); cols.Children.Add(col2); cols.Children.Add(col3);

        var all = new StackPanel();
        all.Children.Add(header);
        all.Children.Add(_results.View);
        all.Children.Add(cols);
        _host.Content = all;

        UpdateStatusText();
        RenderSprint(); RenderOkr(); RenderWork(); RenderAttention(); RenderChanges(); RenderConfluence(); RenderForYou();
    }

    static FrameworkElement Gap() => new Border { Height = 14 };

    void UpdateStatusText()
    {
        _statusText.Text = AtlassianData.HealthText(_cfg);
        bool warn = AtlassianData.Status != "ok" || AtlassianData.ExpiryWarning(_cfg) != null;
        _statusText.Foreground = AtlassianData.Status is "reconnect" or "error" ? Red : warn ? Amber : BarUi.Dim;
    }

    void ShowSettings()
    {
        _settingsOpen = true;
        _host.Content = AtlassianSettings.Build(_ctx, _cfg, () => { _settingsOpen = false; Build(); });
    }

    // ---------------- connect ----------------

    void BuildConnect()
    {
        var sp = new StackPanel { Width = 470 };
        sp.Children.Add(BarUi.Txt(AtlassianData.Status == "reconnect" ? "Atlassian rejected the token - connect again" : "Connect Jira and Confluence", 16));
        sp.Children.Add(T("Spread only reads: it sends GET requests, nothing else. Use an API token with read-only scopes (id.atlassian.com → Security → API tokens → Create API token with scopes). The token is stored in Windows Credential Manager.", 12, BarUi.Dim, null, false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 6, 0, 10); }));
        TextBox Field(string label, string value)
        {
            sp.Children.Add(T(label, 12, BarUi.Dim));
            var box = new TextBox { Text = value, Height = 28, Padding = new Thickness(8, 3, 8, 3), FontSize = 13, Background = Fz(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Track, Margin = new Thickness(0, 2, 0, 8) };
            box.PreviewMouseDown += (_, _) => FocusLater(box);
            sp.Children.Add(box);
            return box;
        }
        var site = Field("Site", string.IsNullOrWhiteSpace(_a.Site) ? "yourcompany.atlassian.net" : _a.Site);
        var email = Field("Atlassian e-mail", AtlassianData.KnownEmail(_cfg));
        if (email.Text.Length == 0) FillEmailLater(email);
        sp.Children.Add(T("API token", 12, BarUi.Dim));
        _tokenBox = new PasswordBox { Height = 28, Padding = new Thickness(8, 3, 8, 3), FontSize = 13, Background = Fz(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Track, Margin = new Thickness(0, 2, 0, 8) };
        _tokenBox.PreviewMouseDown += (_, _) => FocusLater(_tokenBox);
        sp.Children.Add(_tokenBox);
        _connectMsg = T("", 12, Red, null, false);
        sp.Children.Add(_connectMsg);
        sp.Children.Add(BarUi.Button("Connect", async () =>
        {
            _connectMsg!.Text = "Checking…"; _connectMsg.Foreground = BarUi.Dim;
            string? err = await AtlassianData.ConnectAsync(_cfg, site.Text, email.Text, _tokenBox!.Password);
            if (err != null) { _connectMsg.Text = err; _connectMsg.Foreground = Red; return; }
            _tokenBox.Clear();
            Build();
        }));
        _host.Content = sp;
    }

    /// <summary>The Windows UPN comes from whoami (up to 2 s): looked up in the background instead of freezing the panel, filled in unless the user typed one.</summary>
    async void FillEmailLater(TextBox box)
    {
        string upn = await System.Threading.Tasks.Task.Run(() => AtlassianData.EmailFor(_cfg));
        if (!_closed && upn.Length > 0 && box.Text.Length == 0) box.Text = upn;
    }

    void FocusLater(Control c)
    {
        Popover.RequestFocus();
        _ctx.Dispatcher.BeginInvoke(new Action(() => { c.Focus(); Keyboard.Focus(c); }), DispatcherPriority.Input);
    }

    // ---------------- sprint ----------------

    void RenderSprint()
    {
        var s = AtlassianData.Sprint;
        _sprint.Begin(s == null ? "Sprint" : s.Sprint.Name);
        if (s == null) { _sprint.Note(AtlassianData.Status == "ok" ? "No active sprint on the board." : "Loading…"); return; }
        var now = DateTime.Now;
        int total = s.Issues.Count, done = s.Issues.Count(i => i.Done), undone = total - done;
        _sprint.List.Children.Add(T($"{s.Sprint.Start:d MMM} – {s.Sprint.End:d MMM}  ·  {AtlassianLogic.DaysLeftText(s.Sprint.End, now)} left", 12, BarUi.Dim));

        // board columns as a stacked bar + legend
        var counts = s.Columns.Select(c => (c.Name, n: s.Issues.Count(i => AtlassianLogic.ColumnOf(i, s.Columns) == c.Name))).ToList();
        foreach (var extra in s.Issues.Where(i => !s.Columns.Any(c => c.StatusIds.Contains(i.StatusId))).GroupBy(i => i.Status)) counts.Add((extra.Key, extra.Count()));
        var stack = new Grid { Height = 12, Margin = new Thickness(0, 8, 0, 6) };
        double sum = Math.Max(1, counts.Sum(c => c.n));
        var star = new List<ColumnDefinition>();
        foreach (var (name, n) in counts.Where(c => c.n > 0)) stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(n / sum, GridUnitType.Star) });
        int ci = 0;
        foreach (var (name, n) in counts.Where(c => c.n > 0))
        {
            var seg = new Border { Background = ColumnColor(name), Margin = new Thickness(0, 0, 1, 0), ToolTip = $"{name}: {n} (click to list)", Cursor = Cursors.Hand, Opacity = _columnFilter == null || _columnFilter == name ? 1 : 0.4 };
            string segName = name; seg.MouseLeftButtonUp += (_, _) => ToggleColumn(segName);
            Grid.SetColumn(seg, ci++); stack.Children.Add(seg);
        }
        _sprint.List.Children.Add(stack);
        var legend = new WrapPanel();
        foreach (var (name, n) in counts)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = ColumnColor(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            item.Children.Add(T($"{name} {n}", 12, n == 0 ? Faint : Brushes.White));
            var chip = new Border { Child = item, Padding = new Thickness(5, 1, 7, 1), Margin = new Thickness(0, 0, 4, 2), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = _columnFilter == name ? HoverBg : Brushes.Transparent, ToolTip = "Click to list these issues" };
            string cn = name;
            chip.MouseEnter += (_, _) => chip.Background = HoverBg;
            chip.MouseLeave += (_, _) => chip.Background = _columnFilter == cn ? HoverBg : Brushes.Transparent;
            chip.MouseLeftButtonUp += (_, _) => ToggleColumn(cn);
            legend.Children.Add(chip);
        }
        _sprint.List.Children.Add(legend);
        AddColumnList(s);

        // points
        double pts = s.Issues.Sum(i => i.Points ?? 0), ptsDone = s.Issues.Where(i => i.Done).Sum(i => i.Points ?? 0);
        int unest = s.Issues.Count(i => i.Points == null && !i.IsSubtask && i.Type is "Story" or "Task" or "Bug");
        _sprint.List.Children.Add(T($"Points {ptsDone:0}/{pts:0}   ·   {unest} issue{(unest == 1 ? "" : "s")} without estimate", 12.5, BarUi.Dim).With(t => t.Margin = new Thickness(0, 6, 0, 0)));

        // goal
        if (!string.IsNullOrWhiteSpace(s.Sprint.Goal))
            _sprint.List.Children.Add(new TextBlock { Text = "Goal: " + Regex.Replace(s.Sprint.Goal, @"\s+", " ").Trim(), Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        // time vs done
        var (elapsed, doneFrac, ahead) = AtlassianLogic.TimeVsDone(s.Sprint.Start, s.Sprint.End, now, done, total);
        _sprint.List.Children.Add(T($"Time vs done:  {elapsed * 100:0}% of the time gone,  {doneFrac * 100:0}% done  ({(ahead >= 0 ? $"{ahead} ahead" : $"{-ahead} behind")})", 12.5, ahead >= 0 ? Green : Amber).With(t => t.Margin = new Thickness(0, 10, 0, 4)));
        _sprint.List.Children.Add(Bar(doneFrac, 380, ahead >= 0 ? Green : Amber, 8, elapsed));

        // undone target
        var fallback = _a.UndoneTarget.Length >= 2 ? (_a.UndoneTarget[0], _a.UndoneTarget[1]) : (3, 5);
        var (tmin, tmax) = AtlassianLogic.GoalTarget(s.Sprint.Goal, fallback);
        double daysLeft = Math.Max(0, (s.Sprint.End - now).TotalDays);
        double need = AtlassianLogic.NeededPerDay(undone, tmax, daysLeft);
        Brush tc = undone <= tmax ? Green : need <= 2 ? Amber : Red;
        string needText = undone <= tmax ? "target reached" : $"needs ~{need:0.0} done per day";
        _sprint.List.Children.Add(T($"Undone {undone}  (target {tmin}–{tmax})  ·  {needText}", 12.5, tc, FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 8, 0, 0)));
    }

    void ToggleColumn(string name) { _columnFilter = _columnFilter == name ? null : name; RenderSprint(); }

    void AddColumnList(SprintSnapshot s)
    {
        if (_columnFilter == null) return;
        var items = s.Issues.Where(i => AtlassianLogic.ColumnOf(i, s.Columns) == _columnFilter).OrderByDescending(i => i.Updated).ToList();
        var head = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = $"{_columnFilter} · {items.Count}", Foreground = ColumnColor(_columnFilter), FontFamily = BarUi.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var all = Link("Show all", () => { _columnFilter = null; RenderSprint(); }); Grid.SetColumn(all, 1); head.Children.Add(all);
        _sprint.List.Children.Add(head);
        if (items.Count == 0) _sprint.Note("No issues in this column.");
        foreach (var i in items.Take(12)) _sprint.List.Children.Add(IssueRow(i, true));
        if (items.Count > 12) _sprint.Note($"…and {items.Count - 12} more.");
        _sprint.List.Children.Add(new Border { Height = 1, Background = Track, Margin = new Thickness(0, 6, 0, 2) });
    }

    // ---------------- OKR ----------------

    void RenderOkr()
    {
        var o = AtlassianData.Okr;
        _okr.Begin($"OKR · Q{AtlassianLogic.CurrentQuarter(DateTime.Now)}");
        if (o == null) { _okr.Note("Loading…"); return; }
        foreach (var obj in o.Objectives)
        {
            int kids = obj.KeyResults.Sum(k => k.Children), kd = obj.KeyResults.Sum(k => k.ChildrenDone);
            string name = Regex.Replace(obj.Initiative.Summary, @"^\s*\[[^\]]*\]\s*", "");
            var head = new Grid { Margin = new Thickness(0, 6, 0, 2) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(T($"{(obj.Tag.Objective != null ? "O" + obj.Tag.Objective + " · " : "")}{name}", 13, Brushes.White, FontWeights.SemiBold));
            var pr = T($"{kd}/{kids}", 12, BarUi.Dim); Grid.SetColumn(pr, 1); head.Children.Add(pr);
            _okr.List.Children.Add(Row(head, IssueUrl(obj.Initiative.Key)));
            foreach (var kr in obj.KeyResults) _okr.List.Children.Add(KrRow(kr));
        }
        if (o.Other.Count > 0)
        {
            _okr.Sub("OUTSIDE THE OBJECTIVES");
            foreach (var kr in o.Other) _okr.List.Children.Add(KrRow(kr));
        }
        if (o.Objectives.Count == 0 && o.Other.Count == 0) _okr.Note("No open initiatives or epics.");
    }

    FrameworkElement KrRow(KeyResultNode kr)
    {
        var sp = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string text = Regex.Replace(kr.Epic.Summary, @"^(\s*(\([^)]*\)|\[[^\]]*\])\s*)+", "");
        string tag = kr.Tag.KeyResult != null ? $"KR{kr.Tag.KeyResult} · " : "";
        line.Children.Add(T(tag + text, 12.5));
        var stat = T(kr.Children == 0 ? "–" : $"{kr.ChildrenDone}/{kr.Children}" + (kr.Points > 0 ? $"  {kr.PointsDone:0}/{kr.Points:0} pt" : ""), 11.5, BarUi.Dim);
        stat.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(stat, 1); line.Children.Add(stat);
        sp.Children.Add(line);
        sp.Children.Add(Bar(kr.Children == 0 ? 0 : (double)kr.ChildrenDone / kr.Children, 360, kr.Children == 0 ? Faint : Green, 4).With(b => b.Margin = new Thickness(0, 3, 0, 0)));
        foreach (var f in kr.Flags) sp.Children.Add(T("⚠ " + f, 11.5, Amber));
        return Row(sp, IssueUrl(kr.Epic.Key), Brushes.Transparent).With(r => r.Margin = new Thickness(10, 0, 0, 0));
    }

    // ---------------- my work + attention ----------------

    FrameworkElement IssueRow(IssueLite i, bool showAssignee = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(T(i.Key, 12, BarUi.Dim));
        var mid = new StackPanel();
        mid.Children.Add(T(i.Summary, 13));
        var meta = new StackPanel { Orientation = Orientation.Horizontal };
        meta.Children.Add(T(i.Status, 11.5, ColumnColor(i.Status)));
        if (i.Due != null) meta.Children.Add(T($"   due {i.Due:d MMM}", 11.5, i.Due.Value.Date < DateTime.Today && !i.Done ? Red : (i.Due.Value.Date <= DateTime.Today.AddDays(2) ? Amber : BarUi.Dim)));
        if (AtlassianLogic.IsStale(i, DateTime.Now, _a.StaleDays)) meta.Children.Add(T($"   ⏱ no update {(DateTime.Now - i.Updated).TotalDays:0}d", 11.5, Amber));
        if (showAssignee) meta.Children.Add(T("   " + (i.Assignee ?? "unassigned"), 11.5, BarUi.Dim));
        mid.Children.Add(meta);
        if (AtlassianLogic.IsBlockedEffective(i)) mid.Children.Add(BlockLines(i));
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        return Row(g, IssueUrl(i.Key), AtlassianLogic.IsBlockedEffective(i) ? Red : Brushes.Transparent);
    }

    /// <summary>Why is it blocked: the blocking issues (with owner), or the newest comment when nothing is linked, and who can act.</summary>
    FrameworkElement BlockLines(IssueLite i)
    {
        var sp = new StackPanel();
        if (!AtlassianData.Blocks.TryGetValue(i.Key, out var info)) return sp;
        foreach (var b in info.Blockers)
            sp.Children.Add(T(b.Resolved ? $"✓ {b.Key} is {b.Status} – probably ready to unblock"
                                         : $"⛔ {b.Relation} {b.Key} ({b.Status}, {b.Assignee ?? "unassigned"})  {AtlassianLogic.Short(b.Summary, 46)}", 11.5, b.Resolved ? Green : Red));
        if (info.Blockers.Count == 0)
            sp.Children.Add(T(info.LastComment != null ? "no link · last comment – " + info.LastComment : "blocked, but no link or comment says why", 11.5, BarUi.Dim));
        string verdict = AtlassianLogic.BlockVerdict(info.Blockers, AtlassianData.MyId);
        if (verdict.Length > 0) sp.Children.Add(T(verdict, 11.5, verdict == "waiting on others" ? Amber : Green, FontWeights.SemiBold));
        return sp;
    }

    void RenderWork()
    {
        var w = AtlassianData.MyWork;
        _work.Begin($"My work · {w.Count}");
        if (AtlassianData.Status == "ok" && w.Count == 0) { _work.Note("Nothing assigned to you."); return; }
        if (w.Count == 0) { _work.Note("Loading…"); return; }
        foreach (var i in w.OrderBy(AtlassianLogic.WorkRank).ThenBy(i => i.Due ?? DateTime.MaxValue).ThenBy(i => i.Updated)) _work.List.Children.Add(IssueRow(i));
    }

    void RenderAttention()
    {
        var s = AtlassianData.Sprint;
        _attn.Begin("Needs attention · sprint");
        if (s == null) { _attn.Note(AtlassianData.Status == "ok" ? "No active sprint." : "Loading…"); return; }
        var open = s.Issues.Where(i => !i.Done).ToList();
        void Group(string title, List<IssueLite> items, bool assignee = false, Brush? c = null)
        {
            if (items.Count == 0) return;
            _attn.List.Children.Add(new TextBlock { Text = $"{title}  ({items.Count})", Foreground = c ?? Accent, FontFamily = BarUi.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 0, 2) });
            foreach (var i in items.Take(4)) _attn.List.Children.Add(IssueRow(i, assignee));
            if (items.Count > 4) _attn.Note($"…and {items.Count - 4} more.");
        }
        Group("Blocked / waiting", open.Where(AtlassianLogic.IsBlockedEffective).ToList(), true, Red);
        var review = open.Where(AtlassianLogic.IsReview).ToList();
        Group("In review / QA - assigned to me", review.Where(i => i.AssigneeId == AtlassianData.MyId).ToList());
        Group("In review / QA - others", review.Where(i => i.AssigneeId != AtlassianData.MyId).ToList(), true);
        Group("Overdue / due within 2 days", open.Where(i => i.Due != null && i.Due.Value.Date <= DateTime.Today.AddDays(2)).OrderBy(i => i.Due).ToList(), true, Amber);
        Group("Unassigned", open.Where(i => i.AssigneeId == null && !i.IsSubtask).ToList());
        Group("No estimate (Story/Task/Bug)", open.Where(i => i.Points == null && !i.IsSubtask && i.Type is "Story" or "Task" or "Bug").ToList(), true);
        Group("Label Unrefined", open.Where(i => i.Labels.Contains("Unrefined", StringComparer.OrdinalIgnoreCase)).ToList(), true);
        if (_attn.List.Children.Count == 0) _attn.Note("All clear.", Green);
    }

    // ---------------- changes, Confluence, for you ----------------

    static string Icon(string kind) => kind switch { "status" => "↔", "comment" => "💬", "mention" => "@", "assignee" => "👤", "created" => "＋", _ => "•" };

    FrameworkElement ChangeRow(ChangeEvent e)
    {
        bool isNew = e.When > _prevLook;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = Icon(e.Kind), Foreground = e.ForMe ? Amber : BarUi.Dim, FontFamily = BarUi.Emoji, FontSize = 13 });
        var mid = new StackPanel();
        mid.Children.Add(T($"{e.Key}  {e.Title}", 12.5));
        mid.Children.Add(T((e.Who.Length > 0 ? e.Who + ": " : "") + e.Detail, 11.5, e.ForMe ? Amber : BarUi.Dim));
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var t = T(When(e.When), 11.5, Faint); t.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(t, 2); g.Children.Add(t);
        return Row(g, e.Url, isNew ? Accent : Brushes.Transparent);
    }

    void RenderChanges()
    {
        var ch = AtlassianData.Changes;
        int fresh = ch.Count(e => e.When > _prevLook);
        _changes.Begin(fresh > 0 ? $"Changed since last look · {fresh} new" : "Changed since last look");
        if (ch.Count == 0) { _changes.Note(AtlassianData.Status == "ok" ? "Nothing changed." : "Loading…"); return; }
        foreach (var e in ch.Take(25)) _changes.List.Children.Add(ChangeRow(e));
    }

    FrameworkElement ConfRow(ConfItem c)
    {
        bool isNew = c.When > _prevLook;
        var sp = new StackPanel();
        sp.Children.Add(T(c.Title, 12.5));
        string meta = (c.Who.Length > 0 ? c.Who + " · " : "") + When(c.When) + (c.Kind == "comment" && c.Excerpt.Length > 0 ? " · " + c.Excerpt : "");
        sp.Children.Add(T(meta, 11.5, BarUi.Dim));
        return Row(sp, c.Url, isNew ? Accent : Brushes.Transparent);
    }

    void RenderConfluence()
    {
        _conf.Begin($"Confluence · {_a.ConfluenceSpace}");
        if (AtlassianData.ConfPages.Count == 0 && AtlassianData.ConfComments.Count == 0) { _conf.Note(AtlassianData.Status == "ok" ? "Nothing found." : "Loading…"); return; }
        _conf.Sub("LAST CHANGED PAGES");
        foreach (var c in AtlassianData.ConfPages.Take(5)) _conf.List.Children.Add(ConfRow(c));
        _conf.Sub("LATEST COMMENTS");
        foreach (var c in AtlassianData.ConfComments.Take(5)) _conf.List.Children.Add(ConfRow(c));
    }

    void RenderForYou()
    {
        _foryou.Begin("For you");
        var items = new List<(DateTime when, FrameworkElement row)>();
        foreach (var e in AtlassianData.Changes.Where(e => e.ForMe)) items.Add((e.When, ChangeRow(e)));
        foreach (var c in AtlassianData.ConfMentions.Take(6)) items.Add((c.When, ConfRow(c with { Title = "@ " + c.Title })));
        if (items.Count == 0) { _foryou.Note(AtlassianData.Status == "ok" ? "No mentions or assignments." : "Loading…"); return; }
        foreach (var (_, row) in items.OrderByDescending(i => i.when).Take(8)) _foryou.List.Children.Add(row);
        _foryou.List.Children.Add(T("Atlassian's own notification bell has no public API, so this lists mentions of you and assignments to you.", 11, Faint, null, false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(4, 6, 0, 0); }));
    }

    // ---------------- quick open / search ----------------

    void SearchFocus(object s, MouseButtonEventArgs e) => FocusLater(_search);

    static readonly Regex KeyRx = new(@"^[A-Za-z][A-Za-z0-9]*-\d+$", RegexOptions.Compiled);

    async void SearchKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { if (_search.Text.Length > 0) { _search.Text = ""; _results.View.Visibility = Visibility.Collapsed; e.Handled = true; } return; }
        if (e.Key != Key.Enter) return;
        string q = _search.Text.Trim();
        if (q.Length == 0) return;
        e.Handled = true;
        if (KeyRx.IsMatch(q)) { OpenUrl(IssueUrl(q.ToUpperInvariant())); return; }
        if (Regex.IsMatch(q, @"^\d{1,6}$")) { OpenUrl(IssueUrl($"{_a.Project}-{q}")); return; }
        if (AtlassianData.Client == null) return;
        int seq = ++_searchSeq;   // a slower earlier search must not overwrite the results of a later one
        _results.View.Visibility = Visibility.Visible;
        _results.Begin("Search results");
        _results.Note("Searching…");
        try
        {
            var (jira, conf) = await JiraData.QuickSearchAsync(AtlassianData.Client, AtlassianData.Site, q);
            if (_closed || seq != _searchSeq) return;
            _results.Begin($"Search results for \"{AtlassianLogic.Short(q, 30)}\"");
            _results.Actions.Children.Add(Link("Search in Jira", () => OpenUrl($"https://{AtlassianData.Site}/issues/?jql=" + Uri.EscapeDataString($"text ~ \"{JiraData.Esc(q)}*\" ORDER BY updated DESC"))));
            _results.Actions.Children.Add(Link("Search in Confluence", () => OpenUrl($"https://{AtlassianData.Site}/wiki/search?text=" + Uri.EscapeDataString(q))));
            if (jira.Count == 0 && conf.Count == 0) _results.Note("Nothing found.");
            foreach (var (key, summary, status) in jira)
            {
                var sp = new StackPanel(); sp.Children.Add(T($"{key}  {summary}", 12.5)); sp.Children.Add(T(status, 11.5, BarUi.Dim));
                _results.List.Children.Add(Row(sp, IssueUrl(key)));
            }
            foreach (var c in conf) _results.List.Children.Add(ConfRow(c));
        }
        catch (Exception ex) { if (seq != _searchSeq) return; _results.Begin("Search results"); _results.Note("Search failed (" + ex.GetType().Name + ")."); }
    }
}

internal static class UiExt
{
    public static T With<T>(this T x, Action<T> f) { f(x); return x; }
}

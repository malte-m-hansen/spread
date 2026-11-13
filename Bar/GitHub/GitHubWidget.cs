using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Spread;

/// <summary>Bar item: a shield and the number of open alerts (critical by default). Click = small menu. Read-only.</summary>
internal sealed class GitHubWidget : BarWidget
{
    public override string Id => "github";
    public override int Priority => 8;

    static readonly Brush Green = Fz(Color.FromRgb(0x4C, 0xD9, 0x64)), Red = Fz(Color.FromRgb(0xF0, 0x55, 0x5B)), Amber = Fz(Color.FromRgb(0xFF, 0xB3, 0x40));
    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    readonly Path _shield = new()
    {
        Data = Geometry.Parse("M8,1 L14,3.2 L14,8 C14,11.6 11.4,14.2 8,15.2 C4.6,14.2 2,11.6 2,8 L2,3.2 Z"),
        Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
    };
    readonly TextBlock _num = BarUi.Txt("", 13);

    protected override void Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_shield); row.Children.Add(_num);
        View = TopBarWindow.Pill(row, () => Popover.Toggle(View, () => GitHubMenu.Build(Ctx)), 10);
        Present = Ctx.Cfg.GitHub.Enabled;
        Update();
        GitHubData.Changed += Update;
    }

    public override void Tick() { GitHubData.Tick(Ctx.Cfg); }
    public override void Stop() => GitHubData.Changed -= Update;

    void Update()
    {
        var c = Ctx.Cfg.GitHub;
        string st = GitHubData.Status;
        if (st == "starting") { _shield.Stroke = BarUi.Dim; _shield.Fill = Brushes.Transparent; _num.Text = ""; View.ToolTip = "GitHub: checking…"; return; }
        int n = GitHubLogic.Count(GitHubData.Alerts, c.BarSeverities);
        bool bad = st != "ok";
        if (bad && GitHubData.LastOk == null)
        {
            _shield.Stroke = Amber; _shield.Fill = Brushes.Transparent; _num.Text = "!"; _num.Foreground = Amber;
            View.ToolTip = "GitHub: " + GitHubData.HealthText(Ctx.Cfg);
            return;
        }
        _shield.Stroke = n > 0 ? Red : bad ? Amber : Green;
        _shield.Fill = n > 0 ? Red : Brushes.Transparent;
        _num.Text = bad ? n + "!" : n.ToString();
        _num.Foreground = n > 0 ? Brushes.White : bad ? Amber : BarUi.Dim;
        _num.FontWeight = n > 0 ? FontWeights.Bold : FontWeights.SemiBold;
        int high = GitHubLogic.Count(GitHubData.Alerts, new[] { "high" }), crit = GitHubLogic.Count(GitHubData.Alerts, new[] { "critical" });
        string team = c.PropertyValue.Length > 0 ? c.PropertyValue : c.Org;
        View.ToolTip = $"{team}: {crit} critical, {high} high · {GitHubData.Repos.Count(r => !r.Archived)} repos · {GitHubData.HealthText(Ctx.Cfg)}";
    }
}

/// <summary>The small menu under the shield.</summary>
internal static class GitHubMenu
{
    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static readonly Brush Red = Fz(Color.FromRgb(0xF0, 0x55, 0x5B)), Orange = Fz(Color.FromRgb(0xFF, 0x9F, 0x40)), Hover = Fz(Color.FromArgb(0x30, 255, 255, 255));

    static void Open(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
            Popover.CloseAny();
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
    }

    static Border Chip(string text, Brush bg)
    {
        return new Border { Background = bg, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 3, 11, 4), Margin = new Thickness(0, 0, 8, 0), Child = BarUi.Txt(text, 13, Brushes.White, FontWeights.SemiBold) };
    }

    static Border Row(UIElement child, string? url)
    {
        var b = new Border { Child = child, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        if (url != null)
        {
            b.Cursor = Cursors.Hand;
            b.MouseEnter += (_, _) => b.Background = Hover; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) => Open(url);
        }
        return b;
    }

    /// <summary>The menu; the Settings button swaps its content for the settings view and back.</summary>
    public static FrameworkElement Build(BarContext ctx)
    {
        var host = new ContentControl();
        void ShowMain() => host.Content = BuildMain(ctx, () => host.Content = GitHubSettings.Build(ctx, ctx.Cfg, ShowMain));
        ShowMain();
        return host;
    }

    static FrameworkElement BuildMain(BarContext ctx, Action openSettings)
    {
        var c = ctx.Cfg.GitHub;
        string team = c.PropertyValue.Length > 0 ? c.PropertyValue : c.Org;
        var sp = new StackPanel { Width = 380 };
        sp.Children.Add(BarUi.Txt($"Dependabot · {team}", 15));
        bool bad = GitHubData.Status != "ok";
        sp.Children.Add(BarUi.Txt(GitHubData.HealthText(ctx.Cfg), 11.5, bad ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : BarUi.Dim, FontWeights.Normal));

        var alerts = GitHubData.Alerts;
        var crit = alerts.Where(a => a.Severity == "critical").ToList();
        var high = alerts.Where(a => a.Severity == "high").ToList();
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        chips.Children.Add(Chip($"Critical  {crit.Count}", crit.Count > 0 ? Red : Fz(Color.FromRgb(0x3A, 0x3A, 0x3C))));
        chips.Children.Add(Chip($"High  {high.Count}", high.Count > 0 ? Orange : Fz(Color.FromRgb(0x3A, 0x3A, 0x3C))));
        sp.Children.Add(chips);

        if (GitHubData.Status != "ok" && GitHubData.LastOk == null) { /* nothing to list yet */ }
        else if (crit.Count == 0) sp.Children.Add(BarUi.Txt("No open critical alerts.", 13, Brushes.White, FontWeights.SemiBold).With(t => t.Margin = new Thickness(2, 4, 0, 6)));
        else
            foreach (var a in crit.Take(8))
            {
                var g = new StackPanel();
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(new TextBlock { Text = $"{a.Package}  ·  {a.ShortRepo}", Foreground = Brushes.White, FontFamily = BarUi.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var age = BarUi.Txt(GitHubLogic.Age(a.Created, DateTime.Now), 12, c.SlaDays > 0 && (DateTime.Now - a.Created).TotalDays > c.SlaDays ? Red : BarUi.Dim, FontWeights.Normal);
                age.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(age, 1); top.Children.Add(age);
                g.Children.Add(top);
                g.Children.Add(new TextBlock { Text = a.Summary, Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34 });
                sp.Children.Add(Row(g, a.Url));
            }
        if (crit.Count > 8) sp.Children.Add(BarUi.Txt($"…and {crit.Count - 8} more", 12, BarUi.Dim, FontWeights.Normal).With(t => t.Margin = new Thickness(6, 2, 0, 4)));

        // high alerts: just where they are
        if (high.Count > 0)
        {
            sp.Children.Add(BarUi.Txt("High, per repository", 11.5, BarUi.Dim, FontWeights.SemiBold).With(t => t.Margin = new Thickness(2, 8, 0, 3)));
            foreach (var grp in high.GroupBy(a => a.Repo).OrderByDescending(x => x.Count()).Take(5))
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(BarUi.Txt(grp.First().ShortRepo, 13, Brushes.White, FontWeights.Normal));
                var n = BarUi.Txt(grp.Count().ToString(), 13, Orange, FontWeights.SemiBold); Grid.SetColumn(n, 1); g.Children.Add(n);
                sp.Children.Add(Row(g, $"https://github.com/{grp.Key}/security/dependabot?q=is%3Aopen+severity%3Ahigh"));
            }
            int more = high.GroupBy(a => a.Repo).Count() - 5;
            if (more > 0) sp.Children.Add(BarUi.Txt($"…and {more} more repositories", 12, BarUi.Dim, FontWeights.Normal).With(t => t.Margin = new Thickness(6, 2, 0, 0)));
        }

        var foot = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(BarUi.Button("Open on GitHub", () => Open(GitHubLogic.WebUrl(c))));
        foot.Children.Add(BarUi.Button("Refresh", () => GitHubData.RefreshNow()));
        foot.Children.Add(BarUi.Button("Settings", openSettings));
        sp.Children.Add(foot);
        return sp;
    }
}

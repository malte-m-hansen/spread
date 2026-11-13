using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Spread;

/// <summary>Bar item: progress ring + "S27 · 23/47 · 6d". Click opens the Jira/Confluence panel. Read-only.</summary>
internal sealed class AtlassianWidget : BarWidget
{
    public override string Id => "jira";
    public override int Priority => 8;

    readonly Grid _ring = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
    readonly Path _arc = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)), StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Ellipse _track = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x45, 255, 255, 255)), StrokeThickness = 2.6, Margin = new Thickness(1.3) };
    readonly Ellipse _blocked = new() { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    readonly TextBlock _warn = new() { Text = "!", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)), FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)), AmberB = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40));
    readonly TextBlock _txt = BarUi.Txt("", 13);
    readonly Border _new = new() { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x40)), CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly TextBlock _newText = new() { Foreground = Brushes.Black, FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = BarUi.Text };
    AtlassianPanel? _panel;
    string _lastText = "";

    protected override void Build()
    {
        _ring.Children.Add(_track); _ring.Children.Add(_arc); _ring.Children.Add(_warn); _ring.Children.Add(_blocked);
        _new.Child = _newText;
        var row = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_ring); row.Children.Add(_txt); row.Children.Add(_new);
        View = TopBarWindow.Pill(row, Open, 10);
        Present = Ctx.Cfg.Atlassian.Enabled;
        Update();
        AtlassianData.Changed += Update;
    }

    void Open()
    {
        Popover.Toggle(View, () => { _panel = new AtlassianPanel(Ctx); return _panel.Root; }, () => { _panel?.Closed(); _panel = null; }, center: false);
    }

    public override void Tick()
    {
        AtlassianData.Tick(Ctx.Cfg);
        if (Every(30_000)) Update();   // days left changes slowly
    }

    public override void Stop() => AtlassianData.Changed -= Update;

    void SetArc(double f)
    {
        f = Math.Clamp(f, 0, 1);
        if (f < 0.005) { _arc.Data = Geometry.Empty; return; }
        if (f > 0.995) { _arc.Data = new EllipseGeometry(new Point(9, 9), 7.7, 7.7); return; }
        double r = 7.7, ang = f * 2 * Math.PI;
        var end = new Point(9 + r * Math.Sin(ang), 9 - r * Math.Cos(ang));
        var fig = new PathFigure { StartPoint = new Point(9, 9 - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        _arc.Data = new PathGeometry(new[] { fig });
    }

    void Update()
    {
        string st = AtlassianData.Status;
        var s = AtlassianData.Sprint;
        bool bad = st is "offline" or "error" or "reconnect" || AtlassianData.ExpiryWarning(Ctx.Cfg) != null;
        _arc.Stroke = bad ? AmberB : Green;
        _warn.Visibility = bad && st != "not connected" ? Visibility.Visible : Visibility.Collapsed;
        if (s == null)
        {
            SetArc(0);
            _txt.Text = st switch { "not connected" => "Jira: connect", "reconnect" => "Jira: reconnect", "offline" => "Jira: offline", "error" => "Jira: error", _ => "Jira…" };
            _blocked.Visibility = Visibility.Collapsed; _new.Visibility = Visibility.Collapsed;
            View.ToolTip = st == "not connected" ? "Click to connect Jira and Confluence (read-only)" : AtlassianData.HealthText(Ctx.Cfg);
            return;
        }
        int total = s.Issues.Count, done = s.Issues.Count(i => i.Done);
        string num = AtlassianLogic.SprintNumber(s.Sprint.Name);
        _txt.Text = $"{(num.Length > 0 ? "S" + num : "Sprint")} · {done}/{total} · {AtlassianLogic.DaysLeftText(s.Sprint.End, DateTime.Now)}";
        _txt.Opacity = st is "offline" or "error" ? 0.6 : 1;
        SetArc(total == 0 ? 0 : (double)done / total);
        int blocked = s.Issues.Count(AtlassianLogic.IsBlockedEffective);
        _blocked.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        int fresh = AtlassianData.NewCount;
        _new.Visibility = fresh > 0 ? Visibility.Visible : Visibility.Collapsed;
        _newText.Text = fresh > 99 ? "99+" : fresh.ToString();
        string tip = (bad ? AtlassianData.HealthText(Ctx.Cfg) + "\n" : "") + $"{s.Sprint.Name}: {done}/{total} done" + (blocked > 0 ? $", {blocked} blocked" : "") + (fresh > 0 ? $", {fresh} new since your last look" : "");
        if (tip != _lastText) { _lastText = tip; View.ToolTip = tip + (string.IsNullOrWhiteSpace(s.Sprint.Goal) ? "" : "\nGoal: " + s.Sprint.Goal); }
    }
}

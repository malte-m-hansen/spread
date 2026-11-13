using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Spread;

// GNOME-style controls for the quick-settings panel: big round slider, pill toggle with an optional chevron, round icon button, collapsible region.

internal static class QsUi
{
    public static readonly Brush Accent = Fz(Color.FromRgb(0x4C, 0x8D, 0xFF));
    public static readonly Brush TileOff = Fz(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C));
    public static readonly Brush TileOffHover = Fz(Color.FromArgb(0xFF, 0x48, 0x48, 0x4B));
    public static readonly Brush Track = Fz(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4D));
    public static readonly Brush Faint = Fz(Color.FromArgb(0x99, 255, 255, 255));
    public static readonly Brush Red = Fz(Color.FromRgb(0xFF, 0x6B, 0x6B));
    public static readonly Brush Hover = Fz(Color.FromArgb(0x30, 255, 255, 255));
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Emoji = new("Segoe UI Emoji");
    public static readonly FontFamily Text = new("Segoe UI");

    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static TextBlock Glyph(string g, double size, bool emoji = false, Brush? fg = null) => new()
    {
        Text = g, FontFamily = emoji ? Emoji : Icons, FontSize = size, Foreground = fg ?? Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    public static TextBlock Label(string t, double size = 13, Brush? fg = null, FontWeight? w = null) => new()
    {
        Text = t, FontFamily = Text, FontSize = size, Foreground = fg ?? Brushes.White, FontWeight = w ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis
    };

    /// <summary>A round (or pill) button with an icon; click runs <paramref name="onClick"/>.</summary>
    public static Border RoundButton(string glyph, Action onClick, double size = 36, bool emoji = false, string? tip = null)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = TileOff, Cursor = Cursors.Hand, Child = Glyph(glyph, size * 0.45, emoji), ToolTip = tip };
        b.MouseEnter += (_, _) => b.Background = TileOffHover;
        b.MouseLeave += (_, _) => b.Background = TileOff;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    /// <summary>Shows/hides a region with a short fade + vertical grow, like GNOME's submenus.</summary>
    public static void SetOpen(FrameworkElement region, bool open)
    {
        if (open == (region.Visibility == Visibility.Visible)) return;
        if (!open) { region.Visibility = Visibility.Collapsed; return; }
        var st = new ScaleTransform(1, 0.01);
        region.LayoutTransform = st;
        region.Opacity = 0;
        region.Visibility = Visibility.Visible;
        var d = TimeSpan.FromMilliseconds(150);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.01, 1, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        region.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d));
    }
}

/// <summary>Round slider: 0..1. <see cref="Changed"/> fires only for changes made by the user.</summary>
internal sealed class SliderBar : Grid
{
    readonly Border _track = new() { Height = 8, CornerRadius = new CornerRadius(4), Background = QsUi.Track, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _fill = new() { Height = 8, CornerRadius = new CornerRadius(4), Background = QsUi.Accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Ellipse _thumb = new() { Width = 20, Height = 20, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    double _value;
    bool _drag;

    public event Action<double>? Changed;
    public event Action? Released;

    public SliderBar()
    {
        Height = 32; Background = Brushes.Transparent; Cursor = Cursors.Hand; Focusable = false;
        Margin = new Thickness(0, 0, 0, 0);
        Children.Add(_track); Children.Add(_fill); Children.Add(_thumb);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); FromMouse(e); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag) FromMouse(e); };
        MouseLeftButtonUp += (_, e) => { if (_drag) { _drag = false; ReleaseMouseCapture(); Released?.Invoke(); } e.Handled = true; };
    }

    public bool Dragging => _drag;

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Layout(); }
    }

    void FromMouse(MouseEventArgs e)
    {
        double w = Math.Max(1, ActualWidth - 20);
        double v = Math.Clamp((e.GetPosition(this).X - 10) / w, 0, 1);
        _value = v; Layout();
        Changed?.Invoke(v);
    }

    void Layout()
    {
        double w = Math.Max(0, ActualWidth - 20);
        _fill.Width = Math.Max(0, _value * w + 10);
        _thumb.Margin = new Thickness(_value * w, 0, 0, 0);
    }
}

/// <summary>Pill toggle: icon circle, title, small subtitle, optional chevron that opens a detail region.</summary>
internal sealed class TogglePill : Border
{
    readonly Border _iconBox;
    readonly TextBlock _icon, _title, _sub;
    readonly Border? _chev;
    bool _on;

    public event Action? Clicked;
    public event Action? ChevronClicked;

    public TogglePill(string glyph, bool emoji, string title, bool chevron)
    {
        Height = 56; CornerRadius = new CornerRadius(28); Cursor = Cursors.Hand; Margin = new Thickness(4); Background = QsUi.TileOff;
        _icon = QsUi.Glyph(glyph, 17, emoji);
        _iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), Margin = new Thickness(10, 0, 8, 0), Child = _icon, VerticalAlignment = VerticalAlignment.Center };
        _title = QsUi.Label(title, 13.5, null, FontWeights.SemiBold);
        _sub = QsUi.Label("", 11.5, QsUi.Faint);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title); text.Children.Add(_sub);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        g.Children.Add(_iconBox); g.Children.Add(text);
        if (chevron)
        {
            _chev = new Border { Width = 34, Height = 56, Background = Brushes.Transparent, Child = QsUi.Glyph("", 12) };   // chevron right
            Grid.SetColumn(_chev, 2); g.Children.Add(_chev);
            _chev.MouseLeftButtonUp += (_, e) => { e.Handled = true; ChevronClicked?.Invoke(); };
        }
        Child = g;
        MouseEnter += (_, _) => { if (!_on) Background = QsUi.TileOffHover; };
        MouseLeave += (_, _) => Apply();
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    public bool IsOn { get => _on; set { _on = value; Apply(); } }
    public string Subtitle { get => _sub.Text; set { _sub.Text = value; _sub.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible; } }

    void Apply()
    {
        Background = _on ? QsUi.Accent : QsUi.TileOff;
        _iconBox.Background = new SolidColorBrush(_on ? Color.FromArgb(0x38, 255, 255, 255) : Color.FromArgb(0x30, 255, 255, 255));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Spread;

/// <summary>The dock: a rounded translucent bar with one button per app (running dots, hover magnify, name on hover). Pure view: actions are events.</summary>
internal sealed class DockBar : Border
{
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    public event Action<DockEntry, bool>? Activated;                 // (entry, new instance)
    public event Action<DockEntry, FrameworkElement>? ContextRequested;
    public event Action<FrameworkElement>? AddRequested;             // the "+" button
    public double IconDip { get; private set; } = 48;

    public DockBar()
    {
        CornerRadius = new CornerRadius(26);
        Background = new SolidColorBrush(Color.FromArgb(0x78, 0x10, 0x10, 0x10));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 8, 10, 6);
        Child = _row;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        RenderTransform = new TranslateTransform();
    }

    public TranslateTransform Slide => (TranslateTransform)RenderTransform;

    /// <summary>Total height in DIPs for a given icon size (used to reserve room in the layout).</summary>
    public static double HeightFor(double icon) => icon + 8 + 6 + 6 + 8 + 2;

    public void Rebuild(IReadOnlyList<DockEntry> entries, DockConfig cfg, double availableDip)
    {
        _row.Children.Clear();
        // narrow screens: smaller icons (never below 32)
        double icon = cfg.IconSize;
        if (entries.Count > 0)
        {
            double perItem = (availableDip - 40) / entries.Count - 16;
            icon = Math.Clamp(Math.Min(icon, perItem), 32, cfg.IconSize);
        }
        IconDip = icon;
        bool separated = false;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!separated && i > 0 && entries[i - 1].Pinned && !e.Pinned)
            {
                _row.Children.Add(new Border { Width = 1, Height = icon * 0.7, Margin = new Thickness(6, 0, 6, 8), Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
                separated = true;
            }
            _row.Children.Add(Button(e, icon, cfg));
        }
        _row.Children.Add(AddButton(icon));
    }

    FrameworkElement AddButton(double size)
    {
        var plus = new Border
        {
            Width = size * 0.6, Height = size * 0.6, CornerRadius = new CornerRadius(size * 0.3), Margin = new Thickness(0, size * 0.2, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            Child = new TextBlock { Text = "+", FontSize = size * 0.45, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
        };
        var sp = new StackPanel(); sp.Children.Add(plus); sp.Children.Add(new Border { Height = 11 });
        var cell = new Border { Padding = new Thickness(8, 6, 8, 2), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = sp, ToolTip = "Add an app to the dock" };
        cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255));
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; AddRequested?.Invoke(cell); };
        return cell;
    }

    FrameworkElement Button(DockEntry e, double size, DockConfig cfg)
    {
        FrameworkElement face;
        if (e.Icon != null) { var img = new Image { Source = e.Icon, Width = size, Height = size }; RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality); face = img; }
        else face = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)),
                                 Child = new TextBlock { Text = e.Name.Length > 0 ? e.Name[..1].ToUpperInvariant() : "?", FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var scale = new ScaleTransform(1, 1);
        face.RenderTransform = scale; face.RenderTransformOrigin = new Point(0.5, 1);
        if (e.AllMinimized) face.Opacity = 0.75;

        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 8, Margin = new Thickness(0, 3, 0, 0) };
        for (int d = 0; d < Math.Min(3, e.Windows.Count); d++)
            dots.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1.5, 2, 1.5, 0), Fill = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) });

        var sp = new StackPanel(); sp.Children.Add(face); sp.Children.Add(dots);
        var cell = new Border { Padding = new Thickness(8, 6, 8, 2), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = sp, Tag = e };
        if (cfg.Labels)
            cell.ToolTip = new ToolTip
            {
                Content = new TextBlock { Text = e.Name + (e.Windows.Count > 1 ? $"  ({e.Windows.Count} windows)" : ""), Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI") },
                Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x20, 0x20, 0x20)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), Padding = new Thickness(10, 4, 10, 5), Placement = System.Windows.Controls.Primitives.PlacementMode.Top
            };
        cell.MouseEnter += (_, _) =>
        {
            cell.Background = new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255));
            if (cfg.Magnify) { var a = new DoubleAnimation(1.18, TimeSpan.FromMilliseconds(120)); scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a); }
        };
        cell.MouseLeave += (_, _) =>
        {
            cell.Background = Brushes.Transparent;
            var a = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)); scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        };
        cell.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; Activated?.Invoke(e, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); };
        cell.MouseUp += (_, ev) => { if (ev.ChangedButton == MouseButton.Middle) { ev.Handled = true; Activated?.Invoke(e, true); } };
        cell.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ContextRequested?.Invoke(e, cell); };
        return cell;
    }
}

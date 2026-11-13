using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Spread;

/// <summary>
/// Bottom window of the overview: the dimmed wallpaper (so the real windows disappear, as in GNOME, where the
/// windows "move into" the overview) plus the DWM window thumbnails.
/// DWM draws thumbnails above everything in their destination window, so all UI (icons, titles, close
/// buttons, search) lives in the transparent HUD window that sits on top of this one.
/// </summary>
internal sealed class StageWindow : Window
{
    public IntPtr Hwnd { get; private set; }

    readonly Canvas _host = new() { Opacity = 0, ClipToBounds = true };
    readonly Rectangle _solid = new();
    readonly Rectangle _wall = new();
    double _lastWall = -1;

    public StageWindow()
    {
        Title = "Spread stage";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;   // a layered window cannot be a DWM thumbnail target
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000; Top = -20000; Width = 200; Height = 200;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1), CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        _host.Children.Add(_solid); _host.Children.Add(_wall);
        Content = _host;
    }

    public void Init()
    {
        Hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(Hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        long ex = Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        Show();   // stays shown for good; the overlay parks it off-screen so its surfaces are never reallocated
    }

    public void ShowAt(RECT view)
    {
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, view.Left, view.Top, view.Width, view.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void Park(RECT parked) => ShowAt(parked);

    /// <summary>
    /// Sets the wallpaper layer. The window covers <paramref name="view"/> (the monitor or just its work area); the
    /// wallpaper is laid out for the whole <paramref name="mon"/> and offset so it lines up with the real desktop.
    /// </summary>
    public void SetWallpaper(WallpaperInfo info, RECT mon, RECT view, double scale)
    {
        _host.Width = view.Width / scale; _host.Height = view.Height / scale;
        _solid.Width = _host.Width; _solid.Height = _host.Height;
        _solid.Fill = new SolidColorBrush(info.Background);
        _wall.Width = mon.Width / scale; _wall.Height = mon.Height / scale;
        Canvas.SetLeft(_wall, (mon.Left - view.Left) / scale); Canvas.SetTop(_wall, (mon.Top - view.Top) / scale);
        if (info.Image == null) { _wall.Fill = null; return; }

        var brush = new ImageBrush(info.Image);
        switch (info.Position)
        {
            case 2: brush.Stretch = Stretch.Fill; break;
            case 3: case 0: brush.Stretch = Stretch.Uniform; break;
            case 5: // span: one image across the whole virtual desktop; show the part under this monitor
                {
                    double vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77), vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
                    double ia = info.Image.PixelWidth / (double)info.Image.PixelHeight, va = vw / vh;
                    double iw = ia > va ? vh * ia : vw, ih = ia > va ? vh : vw / ia;
                    double ox = (iw - vw) / 2, oy = (ih - vh) / 2;
                    brush.Stretch = Stretch.Fill; brush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                    brush.Viewbox = new Rect((ox + mon.Left - vx) / iw, (oy + mon.Top - vy) / ih, mon.Width / iw, mon.Height / ih);
                    break;
                }
            default: brush.Stretch = Stretch.UniformToFill; break;   // fill (and tile, approximated)
        }
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        _wall.Fill = brush;
    }

    /// <summary>Opacity of the wallpaper layer (1 = it hides the real windows). Static at 1 unless a fade is configured.</summary>
    public void SetBackdrop(double wallpaper)
    {
        wallpaper = Math.Clamp(wallpaper, 0, 1);
        if (Math.Abs(wallpaper - _lastWall) > 0.003) { _lastWall = wallpaper; _host.Opacity = wallpaper; }
    }
}

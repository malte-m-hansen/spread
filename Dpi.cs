using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Spread;

/// <summary>
/// Per-monitor DPI awareness (v2), set in code. Spread runs as "dotnet Spread.dll" (it has no .exe of its own), and Windows only reads
/// the manifest of the .exe, so the PerMonitorV2 entry in app.manifest is never applied: WPF then made the process "system aware" at
/// the scale of the monitor at start-up. After a dock to a monitor with another scale, Windows converted Spread's window coordinates but
/// not the numbers sent for the top bar's reserved space, which left a strip of wallpaper above maximized windows until a restart.
/// Must run before the first window of any kind is created.
/// </summary>
internal static class Dpi
{
    static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    /// <summary>The result of the call at start-up, for the log: null = it worked.</summary>
    public static string? StartupError { get; private set; }

    public static void EnablePerMonitorV2()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(PerMonitorAwareV2) && !IsPerMonitorV2)
                StartupError = "SetProcessDpiAwarenessContext failed (error " + Marshal.GetLastWin32Error() + ")";
        }
        catch (Exception e) { StartupError = e.GetType().Name; }
    }

    public static bool IsPerMonitorV2 => AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2);

    public static string Describe() => Name(GetThreadDpiAwarenessContext());

    internal static string Name(IntPtr ctx) => AreDpiAwarenessContextsEqual(ctx, PerMonitorAwareV2) ? "per-monitor v2"
        : GetAwarenessFromDpiAwarenessContext(ctx) switch { 0 => "unaware", 1 => "system aware", 2 => "per-monitor (v1)", _ => "unknown" };
}

/// <summary>`--dpi-test`: a real (hidden) WPF window must get the per-monitor v2 mode and render at its monitor's own scale.</summary>
internal static class DpiTest
{
    public static int Run()
    {
        var w = new Window { Width = 200, Height = 100, Left = 100, Top = 100, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Opacity = 0 };
        IntPtr h = new WindowInteropHelper(w).EnsureHandle();
        w.Show();
        string mode = Dpi.Name(Dpi.GetWindowDpiAwarenessContext(h));
        uint windowDpi = Native.GetDpiForWindow(h);
        double wpfDpi = VisualTreeHelper.GetDpi(w).PixelsPerInchX;
        w.Close();
        bool ok = mode == "per-monitor v2" && Math.Abs(wpfDpi - windowDpi) < 0.5;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} a WPF window is per-monitor v2 and WPF draws it at the monitor's scale  (mode={mode}, window dpi={windowDpi}, WPF dpi={wpfDpi})");
        Console.WriteLine(ok ? "DPI TEST PASSED" : "DPI TEST FAILED");
        return ok ? 0 : 1;
    }
}

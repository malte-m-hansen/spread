using System;

namespace Spread;

/// <summary>The numbers behind the top bar's reserved screen space. Pure, so they can be tested.</summary>
internal static class AppBarMath
{
    /// <summary>The bar's height in pixels on a monitor with this scale (96 dpi = 100%).</summary>
    public static int HeightPx(int heightDip, uint dpi) => (int)Math.Round(heightDip * dpi / 96.0);

    /// <summary>
    /// How many pixels Windows reserves at the top of the monitor beyond the bar: above 0 is a strip of wallpaper between the bar and a
    /// maximized window (a reservation made at another scale, e.g. the laptop's, that was never replaced); below 0 the bar covers window space.
    /// </summary>
    public static int ReservedGap(RECT monitor, RECT work, int barHeightPx) => (work.Top - monitor.Top) - barHeightPx;

    /// <summary>
    /// Windows applies a dock or a scale change in steps, so the numbers read at the first message can be old. After one, the bar looks
    /// again after 400 ms, 1 s and 2.5 s while the reservation is wrong, then gives up (another top bar of a different program can be the reason).
    /// </summary>
    public static int SettleDelayMs(int attempt) => attempt switch { 0 => 400, 1 => 1000, 2 => 2500, _ => -1 };
}

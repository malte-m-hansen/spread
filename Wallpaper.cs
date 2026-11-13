using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spread;

internal sealed class WallpaperInfo
{
    public BitmapSource? Image;
    /// <summary>DESKTOP_WALLPAPER_POSITION: 0 center, 1 tile, 2 stretch, 3 fit, 4 fill, 5 span.</summary>
    public int Position = 4;
    public Color Background = Color.FromRgb(0x20, 0x20, 0x20);
}

/// <summary>
/// Finds the current desktop wallpaper for a monitor (IDesktopWallpaper, no admin needed) and keeps a decoded copy
/// cached. The overview shows it dimmed behind the window clones, as GNOME does, instead of the real windows.
/// </summary>
internal static class Wallpaper
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, out IntPtr wallpaper);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, out IntPtr monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(int position);
        [PreserveSig] int GetPosition(out int position);
    }

    /// <summary>The overview shows the wallpaper at this brightness. It is baked into the cached bitmap so no dim layer
    /// has to be composited every frame (full-screen layers are expensive at 5120x2160).</summary>
    public const double Brightness = 0.58;

    static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
    static readonly ConcurrentDictionary<string, (DateTime stamp, BitmapSource img)> Images = new();
    static readonly ConcurrentDictionary<string, byte> Loading = new();

    /// <summary>Cheap (a few COM calls); decoding happens on a worker the first time and is cached.</summary>
    public static WallpaperInfo Refresh(RECT mon)
    {
        var info = new WallpaperInfo();
        IDesktopWallpaper? wp = null;
        try
        {
            wp = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidDesktopWallpaper)!)!;
            string? monitorId = FindMonitor(wp, mon);

            string path = "";
            if (wp.GetWallpaper(monitorId, out IntPtr p) == 0 && p != IntPtr.Zero) { path = Marshal.PtrToStringUni(p) ?? ""; Marshal.FreeCoTaskMem(p); }
            if (wp.GetPosition(out int pos) == 0) info.Position = pos;
            if (wp.GetBackgroundColor(out uint c) == 0) info.Background = Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));

            if (path.Length > 0 && File.Exists(path))
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                string key = $"{path}|{mon.Width}x{mon.Height}|{info.Position}";
                if (Images.TryGetValue(key, out var cached) && cached.stamp == stamp) info.Image = cached.img;
                else if (Loading.TryAdd(key, 1)) Task.Run(() => Load(key, path, stamp, mon, info.Position));
            }
        }
        catch (Exception e) { Log.Write("wallpaper lookup: " + e.Message); }
        finally { if (wp != null) Marshal.ReleaseComObject(wp); }
        return info;
    }

    static string? FindMonitor(IDesktopWallpaper wp, RECT mon)
    {
        if (wp.GetMonitorDevicePathCount(out uint n) != 0) return null;
        for (uint i = 0; i < n; i++)
        {
            if (wp.GetMonitorDevicePathAt(i, out IntPtr idp) != 0 || idp == IntPtr.Zero) continue;
            string id = Marshal.PtrToStringUni(idp) ?? ""; Marshal.FreeCoTaskMem(idp);
            if (id.Length > 0 && wp.GetMonitorRECT(id, out RECT r) == 0 &&
                r.Left == mon.Left && r.Top == mon.Top && r.Right == mon.Right && r.Bottom == mon.Bottom) return id;
        }
        return null;   // single monitor / not found: the global wallpaper
    }

    static void Load(string key, string path, DateTime stamp, RECT mon, int position)
    {
        try
        {
            int iw, ih;
            using (var fs = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                iw = frame.PixelWidth; ih = frame.PixelHeight;
            }
            // Decode no larger than needed to cover the target (the span mode covers the whole virtual desktop).
            double tw = mon.Width, th = mon.Height;
            if (position == 5) { tw = Native.GetSystemMetrics(78); th = Native.GetSystemMetrics(79); }
            int decodeW = (int)Math.Min(iw, Math.Max(tw, th * iw / (double)ih));

            using var fs2 = File.OpenRead(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = fs2;
            bi.DecodePixelWidth = Math.Max(1, decodeW);
            bi.EndInit();
            bi.Freeze();

            // opaque copy with RGB scaled by Brightness
            var fc = new FormatConvertedBitmap(bi, PixelFormats.Bgr32, null, 0);
            int w = fc.PixelWidth, h = fc.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            fc.CopyPixels(px, stride, 0);
            int mul = (int)Math.Round(Brightness * 256);
            Parallel.For(0, h, y =>
            {
                int o = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x * 4;
                    px[i] = (byte)((px[i] * mul) >> 8); px[i + 1] = (byte)((px[i + 1] * mul) >> 8); px[i + 2] = (byte)((px[i + 2] * mul) >> 8);
                }
            });
            var dimmed = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, stride);
            dimmed.Freeze();
            Images[key] = (stamp, dimmed);
            Evict(Images, key, MaxCached);
            Log.Write($"wallpaper cached (dimmed): {iw}x{ih} -> {w}x{h}");
        }
        catch (Exception e) { Log.Write("wallpaper load failed: " + e.Message); }
        finally { Loading.TryRemove(key, out _); }
    }

    const int MaxCached = 3;

    /// <summary>
    /// Each cached copy is a full-screen bitmap (25-65 MB). An older wallpaper for the same monitor geometry is never needed again
    /// (slideshow, Spotlight, a new picture), so it goes; beyond <see cref="MaxCached"/> geometries (docking stations, projectors)
    /// the others go too. Without this the cache only ever grew.
    /// </summary>
    internal static void Evict<T>(ConcurrentDictionary<string, T> images, string keep, int max)
    {
        int bar = keep.IndexOf('|');
        string geometry = bar >= 0 ? keep[bar..] : "\0";   // "|WxH|position"
        foreach (var k in images.Keys)
            if (k != keep && k.EndsWith(geometry, StringComparison.Ordinal)) images.TryRemove(k, out _);
        foreach (var k in images.Keys)
        {
            if (images.Count <= max) break;
            if (k != keep) images.TryRemove(k, out _);
        }
    }
}

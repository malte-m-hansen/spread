using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Spread;

internal sealed class TrayEntry
{
    public string ExePath = "";
    public string Tooltip = "";
    public bool Promoted;
    public ImageSource? Icon;
}

/// <summary>
/// The icons for the top bar's tray area. Windows 11 keeps a list of every notification icon in
/// HKCU\Control Panel\NotifyIconSettings (executable, tooltip, "show in the taskbar corner" flag and a PNG snapshot of the icon).
/// That is a supported part of the Settings page. It is not a live feed: an entry is shown here when its program is running.
/// </summary>
internal static class TrayRegistry
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    // system icons are drawn by the bar itself or are not interesting
    static readonly HashSet<string> BuiltInSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dotnet.exe", "Spread.exe", "wlrmdr.exe", "MoNotificationUx.exe", "bcmFirmwareUpgradeProgress.exe"
    };

    static string? Resolve(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        if (exe[0] != '{') return exe;
        int i = exe.IndexOf('\\');
        if (i < 0) return null;
        if (Guid.TryParse(exe.AsSpan(0, i), out var g) && SHGetKnownFolderPath(g, 0, IntPtr.Zero, out var p) == 0)
        {
            string? dir = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
            return dir == null ? null : Path.Combine(dir, exe.Substring(i + 1));
        }
        return null;
    }

    static HashSet<string> RunningExePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var hp = Native.OpenProcess(0x1000, false, (uint)proc.Id);
                if (hp == IntPtr.Zero) continue;
                try { var sb = new StringBuilder(1024); int n = sb.Capacity; if (Native.QueryFullProcessImageName(hp, 0, sb, ref n)) set.Add(sb.ToString()); }
                finally { Native.CloseHandle(hp); }
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return set;
    }

    /// <summary>Entries whose program is running now. Promoted ("shown in the corner") first, otherwise alphabetical.</summary>
    public static List<TrayEntry> ReadRunning(string[] excludeNames)
    {
        var result = new Dictionary<string, TrayEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return new List<TrayEntry>();
            var running = RunningExePaths();
            var skip = new HashSet<string>(excludeNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k == null) continue;
                var path = Resolve(k.GetValue("ExecutablePath") as string ?? "");
                if (path == null) continue;
                var file = Path.GetFileName(path);
                if (BuiltInSkip.Contains(file) || skip.Contains(file) || !running.Contains(path)) continue;

                bool promoted = k.GetValue("IsPromoted") is int ip && ip == 1;
                string tip = (k.GetValue("InitialTooltip") as string ?? "").Trim('‪', '‬', ' ');
                if (result.TryGetValue(path, out var existing) && (existing.Promoted || !promoted) && existing.Tooltip.Length >= tip.Length) continue;

                ImageSource? icon = null;
                if (k.GetValue("IconSnapshot") is byte[] png && png.Length > 8)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(png); bi.EndInit(); bi.Freeze();
                        icon = bi;
                    }
                    catch { }
                }
                result[path] = new TrayEntry { ExePath = path, Tooltip = tip, Promoted = promoted, Icon = icon };
            }
        }
        catch (Exception e) { Log.Write("tray registry read failed: " + e.Message); }
        return result.Values.OrderByDescending(e => e.Promoted).ThenBy(e => Path.GetFileName(e.ExePath), StringComparer.OrdinalIgnoreCase).ToList();
    }
}

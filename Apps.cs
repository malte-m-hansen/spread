using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spread;

internal sealed class AppEntry
{
    public string Name = "";
    public string AppId = "";
    public ImageSource? Icon;
    /// <summary>Set only after Icon (volatile): the icon preload thread and the UI thread both call EnsureIcon.</summary>
    public volatile bool IconTried;
}

internal sealed class AppCatalog
{
    volatile List<AppEntry> _all = new();   // replaced as a whole (from a thread-pool thread), never changed in place
    public int Count => _all.Count;
    public IReadOnlyList<AppEntry> All => _all;
    /// <summary>Tests only: replaces the catalog.</summary>
    public void SetAll(IEnumerable<AppEntry> apps) => _all = apps.ToList();

    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The Start menu's apps from PowerShell's Get-StartApps. Bounded: a PowerShell that hangs (a busy PC at logon) used to leave search without apps
    /// until Spread restarted; it is now ended after a minute and asked once more.
    /// </summary>
    public async Task LoadAsync()
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var list = await ReadStartApps().ConfigureAwait(false);
            if (list != null && list.Count > 0)
            {
                _all = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                Log.Write($"app catalog loaded: {_all.Count} apps");
                return;
            }
            if (attempt == 1) await Task.Delay(5000).ConfigureAwait(false);
        }
    }

    static async Task<List<AppEntry>?> ReadStartApps()
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
            };
            p = Process.Start(psi);
            if (p == null) return null;
            // the read itself is what hangs with a stuck PowerShell (no EOF): bound it, not just the exit (killing the process ends the read)
            var read = p.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(LoadTimeout)).ConfigureAwait(false) != read) { Log.Write("app catalog: Get-StartApps did not answer in time"); return null; }
            var list = ParseStartApps(await read.ConfigureAwait(false));
            if (list.Count == 0) Log.Write("app catalog: Get-StartApps returned no apps");
            return list;
        }
        catch (Exception e) { Log.Write("app catalog failed: " + e.Message); return null; }
        finally
        {
            if (p != null)
            {
                try { if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
    }

    /// <summary>ConvertTo-Json writes an array, but a single object when there is exactly one app (and nothing at all for none).</summary>
    public static List<AppEntry> ParseStartApps(string json)
    {
        var list = new List<AppEntry>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.ValueKind == JsonValueKind.Object ? new[] { root } : Array.Empty<JsonElement>();
        static string Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string name = Str(e, "Name"), id = Str(e, "AppID");
            if (name.Length > 0 && id.Length > 0) list.Add(new AppEntry { Name = name, AppId = id });
        }
        return list;
    }

    /// <summary>
    /// Loads all app icons once, in the background, so a search never has to extract icons on the UI thread
    /// (each extraction is a shell COM call of several ms; 24 tiles per keystroke made typing lag).
    /// </summary>
    public void PreloadIcons()
    {
        var list = _all.ToList();
        var t = new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var a in list) EnsureIcon(a);
            Log.Write($"app icons preloaded: {list.Count} in {sw.ElapsedMilliseconds} ms");
        }) { IsBackground = true, Name = "app icon preload", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public List<AppEntry> Search(string query, int max)
    {
        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return new List<AppEntry>();
        var scored = new List<(int score, AppEntry app)>();
        foreach (var a in _all)
        {
            string n = a.Name.ToLowerInvariant();
            int score;
            if (n.StartsWith(q)) score = 0;
            else if (n.Split(' ', '-', '.', '_').Any(w => w.StartsWith(q))) score = 1;
            else if (n.Contains(q)) score = 2;
            else continue;
            scored.Add((score, a));
        }
        return scored.OrderBy(s => s.score).ThenBy(s => s.app.Name.Length).Take(max).Select(s => s.app).ToList();
    }

    public static void Launch(AppEntry app)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.AppId) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("launch failed: " + e.Message); }
    }

    // --- icons through the shell (works for classic and packaged apps) ---
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    /// <summary>
    /// Called by the preload thread and on the UI thread (dock picker). IconTried is set only once the attempt is over: set first, a caller that came
    /// while the other thread was still extracting got no icon. Two callers at once may both extract; the result is the same frozen image.
    /// The shell item is released here, on the thread that made it (the preload thread's apartment is gone by the time a finalizer would run).
    /// </summary>
    public static void EnsureIcon(AppEntry app)
    {
        if (app.IconTried) return;
        IShellItemImageFactory? f = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName("shell:AppsFolder\\" + app.AppId, IntPtr.Zero, ref iid, out f);
            // 0x4 = ICONONLY, 0x1 = BIGGERSIZEOK
            if (f.GetImage(new SIZE { cx = 64, cy = 64 }, 0x5, out hbm) == 0 && hbm != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                app.Icon = bs;
            }
        }
        catch (Exception e) { Log.Write("icon failed for " + app.Name + ": " + e.Message); }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            if (f != null) { try { Marshal.ReleaseComObject(f); } catch { } }
            app.IconTried = true;
        }
    }
}

/// <summary>Small shell icons for files and folders by extension (no disk access: USEFILEATTRIBUTES), cached.</summary>
internal static class FileIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string name, bool folder)
    {
        string key = folder ? "<folder>" : System.IO.Path.GetExtension(name);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? img = null;
        try
        {
            var fi = new SHFILEINFO();
            // 0x100 ICON, 0x1 SMALLICON, 0x10 USEFILEATTRIBUTES; attributes 0x10 = directory, 0x80 = normal file
            SHGetFileInfo(folder ? "folder" : "file" + key, folder ? 0x10u : 0x80u, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1 | 0x10);
            if (fi.hIcon != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze(); img = bs;
                DestroyIcon(fi.hIcon);
            }
        }
        catch { }
        Cache[key] = img;
        return img;
    }
}

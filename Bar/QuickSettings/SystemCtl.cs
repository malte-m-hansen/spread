using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Spread;

// Controls behind the quick-settings panel. Small, separate, each hides the matching tile when it does not work on this PC.

// ---------------- brightness (internal panel, WMI) ----------------

/// <summary>
/// Brightness of the laptop panel through WMI (root\wmi, System.Management), on one worker thread; the latest value wins while you drag the slider.
/// (The scripting-COM way failed with E_FAIL inside the running app, and the display driver's own interface is not supported on this PC.)
/// </summary>
internal static class BrightnessCtl
{
    static readonly BlockingCollection<Action> Q = new();
    static Thread? _t;
    static int _pending = -1;
    static readonly System.Management.ManagementScope Scope = new(@"\\.\root\wmi");

    static void Ensure()
    {
        if (_t != null) return;
        _t = new Thread(() => { foreach (var a in Q.GetConsumingEnumerable()) { try { a(); } catch { } } }) { IsBackground = true, Name = "Spread brightness" };
        _t.Start();
    }

    static void Connect() { if (!Scope.IsConnected) Scope.Connect(); }

    /// <summary>Last error of a read or write (exception type + HRESULT only), for the log and the test.</summary>
    public static string LastError = "";
    static string Describe(Exception e) => e.GetType().Name + " 0x" + e.HResult.ToString("X8");

    static int? ReadNow()
    {
        Connect();
        using var s = new System.Management.ManagementObjectSearcher(Scope, new System.Management.ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"));
        foreach (System.Management.ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        return null;
    }

    /// <summary>0..100, or null when there is no controllable panel (desktop PC, external monitor only). A failed read is retried once with a fresh connection.</summary>
    public static void ReadAsync(Action<int?> done)
    {
        Ensure();
        Q.Add(() =>
        {
            int? v = null;
            for (int attempt = 0; attempt < 2 && v == null; attempt++)
            {
                try { v = ReadNow(); break; }
                catch (Exception e) { LastError = Describe(e); Log.Debug("brightness read: " + LastError); try { Scope.Connect(); } catch { } }
            }
            done(v);
        });
    }

    /// <summary>Sets the brightness; the callback (optional) gets null on success or the error text.</summary>
    public static void Set(int level, Action<string?>? done = null)
    {
        Ensure();
        Volatile.Write(ref _pending, Math.Clamp(level, 0, 100));
        Q.Add(() =>
        {
            int v = Interlocked.Exchange(ref _pending, -1);
            if (v < 0) { done?.Invoke(null); return; }   // a newer value was already applied
            string? err = "no active panel";
            for (int attempt = 0; attempt < 2 && err != null; attempt++)
            {
                try
                {
                    Connect();
                    using var s = new System.Management.ManagementObjectSearcher(Scope, new System.Management.ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"));
                    foreach (System.Management.ManagementObject m in s.Get()) using (m) { m.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); err = null; break; }
                }
                catch (Exception e) { err = Describe(e); try { Scope.Connect(); } catch { } }
            }
            if (err != null) { LastError = err; Log.Write("brightness set failed: " + err); }
            done?.Invoke(err);
        });
    }
}

// ---------------- radios, Wi-Fi networks, Bluetooth devices ----------------

internal sealed record BtDevice(string Name, bool Connected);
internal sealed record WifiNet(string Ssid, int Signal, bool Secure, bool Connected, bool Saved);

internal static class RadioCtl
{
    public static async Task<List<Windows.Devices.Radios.Radio>> ListAsync()
    {
        try { return (await Windows.Devices.Radios.Radio.GetRadiosAsync()).ToList(); } catch { return new(); }
    }

    public static async Task<bool> SetAsync(Windows.Devices.Radios.RadioKind kind, bool on)
    {
        try
        {
            var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
            if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;
            bool ok = true;
            foreach (var r in await ListAsync())
                if (r.Kind == kind) ok &= await r.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off) == Windows.Devices.Radios.RadioAccessStatus.Allowed;
            return ok;
        }
        catch (Exception e) { Log.Write("radio switch failed: " + e.GetType().Name); return false; }
    }

    /// <summary>Airplane mode = every radio off (and back on).</summary>
    public static async Task<bool> SetAllAsync(bool on)
    {
        try
        {
            var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
            if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;
            foreach (var r in await ListAsync()) await r.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            return true;
        }
        catch { return false; }
    }

    public static async Task<List<BtDevice>> BluetoothAsync()
    {
        var res = new List<BtDevice>();
        try
        {
            string sel = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            foreach (var info in await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel))
            {
                try
                {
                    using var dev = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id);
                    if (dev != null) res.Add(new BtDevice(dev.Name, dev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected));
                }
                catch { }
            }
        }
        catch { }
        return res.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }
}

/// <summary>Wi-Fi through the native Wlan API (locale independent): connected network, visible networks, connecting to a saved profile.</summary>
internal static class WifiCtl
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WLAN_INTERFACE_INFO { public Guid Guid; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WLAN_AVAILABLE_NETWORK
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
        public int BssType; public uint NumberOfBssids; public int Connectable; public int NotConnectableReason; public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
        public int MorePhyTypes; public uint SignalQuality; public int SecurityEnabled; public int DefaultAuth; public int DefaultCipher; public uint Flags; public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WLAN_CONNECTION_PARAMETERS { public int Mode; public IntPtr Profile; public IntPtr Ssid; public IntPtr BssidList; public int BssType; public uint Flags; }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    static bool Open(out IntPtr h, out Guid iface)
    {
        iface = Guid.Empty; h = IntPtr.Zero;
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return false;
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var lp) != 0 || lp == IntPtr.Zero) { WlanCloseHandle(h, IntPtr.Zero); return false; }
        try
        {
            int n = Marshal.ReadInt32(lp);
            if (n < 1) { WlanCloseHandle(h, IntPtr.Zero); return false; }
            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(lp + 8);
            iface = info.Guid;
            return true;
        }
        finally { WlanFreeMemory(lp); }
    }

    /// <summary>Visible networks (strongest first). Call off the UI thread.</summary>
    public static List<WifiNet> Networks(bool scan)
    {
        var res = new List<WifiNet>();
        if (!Open(out var h, out var iface)) return res;
        try
        {
            if (scan) { WlanScan(h, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(1200); }
            if (WlanGetAvailableNetworkList(h, ref iface, 0, IntPtr.Zero, out var lp) != 0 || lp == IntPtr.Zero) return res;
            try
            {
                int n = Marshal.ReadInt32(lp);
                int size = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                for (int i = 0; i < n; i++)
                {
                    var an = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(lp + 8 + i * size);
                    string ssid = System.Text.Encoding.UTF8.GetString(an.Ssid, 0, (int)Math.Min(32, an.SsidLength));
                    if (ssid.Length == 0) continue;
                    res.Add(new WifiNet(ssid, (int)an.SignalQuality, an.SecurityEnabled != 0, (an.Flags & 1) != 0, (an.Flags & 2) != 0));
                }
            }
            finally { WlanFreeMemory(lp); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
        return res.GroupBy(w => w.Ssid).Select(g => g.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Signal).First()).OrderByDescending(w => w.Connected).ThenByDescending(w => w.Signal).ToList();
    }

    /// <summary>Connects to a network that has a saved profile (the profile name equals the SSID for normal networks).</summary>
    public static bool Connect(string profile)
    {
        if (!Open(out var h, out var iface)) return false;
        IntPtr name = Marshal.StringToHGlobalUni(profile);
        try
        {
            var p = new WLAN_CONNECTION_PARAMETERS { Mode = 0 /*profile*/, Profile = name, BssType = 1 /*infrastructure*/ };
            return WlanConnect(h, ref iface, ref p, IntPtr.Zero) == 0;
        }
        finally { Marshal.FreeHGlobal(name); WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>0..4 bars from a 0..100 signal quality.</summary>
    public static int Bars(int quality) => quality >= 75 ? 4 : quality >= 50 ? 3 : quality >= 25 ? 2 : quality > 0 ? 1 : 0;
}

// ---------------- power mode, theme, keep awake, session, battery ----------------

internal static class PowerMode
{
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(ref Guid scheme);

    public static readonly (Guid id, string name)[] Modes =
    {
        (new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), "Best power efficiency"),
        (Guid.Empty, "Balanced"),
        (new Guid("3af9B8d9-7c97-431d-ad78-34a8bfea439f"), "Better performance"),
        (new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), "Best performance"),
    };

    public static string NameOf(Guid id) => Modes.FirstOrDefault(m => m.id == id).name ?? "Custom";

    public static Guid? Get() { try { return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; } catch { return null; } }

    public static bool Set(Guid id) { try { var g = id; return PowerSetActiveOverlayScheme(ref g) == 0; } catch { return false; } }
}

internal static class ThemeMode
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    public static bool IsDark()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Key); return !(k?.GetValue("AppsUseLightTheme") is int a && a == 1); }
        catch { return false; }
    }

    /// <summary>Dark style for apps and Windows itself; running programs are told to re-read the theme.</summary>
    public static void SetDark(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            SendMessageTimeoutW(new IntPtr(0xFFFF), 0x1A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);
        }
        catch (Exception e) { Log.Write("theme switch failed: " + e.GetType().Name); }
    }
}

/// <summary>Keeps the PC (and screen) awake while on. The execution state belongs to a thread, so one thread holds it.</summary>
internal static class KeepAwake
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    static Thread? _t;
    static ManualResetEventSlim? _stop;
    static DateTime? _until;

    public static bool IsOn => _t != null && _t.IsAlive;
    public static DateTime? Until => _until;

    public static void Set(bool on, int minutes = 0)
    {
        if (!on) { _stop?.Set(); _t = null; _until = null; return; }
        if (IsOn) return;
        var stop = _stop = new ManualResetEventSlim();
        _until = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null;
        _t = new Thread(() =>
        {
            SetThreadExecutionState(0x80000000 | 0x1 | 0x2);   // CONTINUOUS | SYSTEM_REQUIRED | DISPLAY_REQUIRED
            stop.Wait(minutes > 0 ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan);
            SetThreadExecutionState(0x80000000);
        }) { IsBackground = true, Name = "Spread keep awake" };
        _t.Start();
    }
}

internal static class SessionCtl
{
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    public static void Lock() { try { LockWorkStation(); } catch { } }
    public static void Sleep() { try { SetSuspendState(false, false, false); } catch { } }
    public static void SignOut() => Shutdown("/l");
    public static void Restart() => Shutdown("/r /t 0");
    public static void ShutDown() => Shutdown("/s /t 0");
    static void Shutdown(string args) { try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception e) { Log.Write("shutdown failed: " + e.GetType().Name); } }
}

internal static class BatteryInfo
{
    /// <summary>(percent, plugged in, charging, minutes left or -1); null without a battery.</summary>
    public static (int pct, bool plugged, bool charging, int minutesLeft)? Read()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) return null;
        int sec = ps.BatteryLifeRemaining;
        return ((int)Math.Round(ps.BatteryLifePercent * 100), ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging), sec > 0 ? sec / 60 : -1);
    }

    public static string Describe((int pct, bool plugged, bool charging, int minutesLeft) b) =>
        b.charging ? "Charging" : b.plugged ? (b.pct >= 99 ? "Fully charged" : "Plugged in") : b.minutesLeft > 0 ? $"{b.minutesLeft / 60} h {b.minutesLeft % 60:00} min left" : "On battery";
}

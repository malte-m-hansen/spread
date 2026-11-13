using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Spread;

/// <summary>
/// Experiment (plan phase 0): can another program learn about the notification-area icons on this Windows build?
/// Step 1 (--tray-spike, safe): create Explorer's TrayNotify object and ask which interfaces it offers. Nothing is registered.
/// Step 2 (--tray-spike-register, run it yourself and watch your tray): register a callback for a few seconds and print what arrives,
/// then unregister. Never reads or writes another process's memory.
/// </summary>
internal static class TraySpike
{
    static readonly Guid ClsidTrayNotify = new("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD");
    static readonly (string name, Guid iid)[] Candidates =
    {
        ("ITrayNotify (Win8-Win11 21H2 style)", new Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4")),
        ("ITrayNotify (Win7 style)",            new Guid("FB852B2C-6BAD-4605-9551-F15F87830935")),
        ("INotificationCB",                     new Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB")),
        ("IUnknown",                            new Guid("00000000-0000-0000-C000-000000000046")),
    };

    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITrayNotify
    {
        [PreserveSig] int RegisterCallback([MarshalAs(UnmanagedType.Interface)] INotificationCB callback, out uint handle);
        [PreserveSig] int UnregisterCallback(uint handle);
        [PreserveSig] int SetPreference(IntPtr item);
        [PreserveSig] int EnableAutoTray([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int DoAction([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport, Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INotificationCB
    {
        [PreserveSig] int Notify(uint evt, IntPtr item);
    }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    sealed class Callback : INotificationCB
    {
        public int Count;
        public int Notify(uint evt, IntPtr item)
        {
            Count++;
            try
            {
                // NOTIFYITEM (64-bit): exe name, tooltip, icon, window, preference, id, guid
                string exe = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                string tip = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 8)) ?? "";
                IntPtr hIcon = Marshal.ReadIntPtr(item, 16);
                IntPtr hwnd = Marshal.ReadIntPtr(item, 24);
                int pref = Marshal.ReadInt32(item, 32);
                Console.WriteLine($"  event {evt}: exe='{System.IO.Path.GetFileName(exe)}' tip='{tip}' hasIcon={hIcon != IntPtr.Zero} hwnd={(hwnd != IntPtr.Zero ? "yes" : "no")} preference={pref}");
            }
            catch (Exception e) { Console.WriteLine($"  event {evt}: could not read item ({e.GetType().Name})"); }
            return 0;
        }
    }

    public static int Run(bool register)
    {
        Console.WriteLine($"Tray spike on Windows {Environment.OSVersion.Version} ({(register ? "register mode" : "probe only")})");
        object? obj;
        try { obj = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidTrayNotify)!); }
        catch (Exception e) { Console.WriteLine("Could not create the TrayNotify object: " + e.Message); return 1; }
        Console.WriteLine("TrayNotify object created (served by Explorer).");

        IntPtr unk = Marshal.GetIUnknownForObject(obj!);
        foreach (var (name, iid) in Candidates)
        {
            var g = iid;
            int hr = Marshal.QueryInterface(unk, ref g, out IntPtr p);
            Console.WriteLine($"  QueryInterface {name,-38} {iid}: {(hr == 0 ? "AVAILABLE" : $"not available (0x{hr:X8})")}");
            if (p != IntPtr.Zero) Marshal.Release(p);
        }
        Marshal.Release(unk);

        if (!register) { Console.WriteLine("Probe only: nothing registered. Run --tray-spike-register to listen for icons."); return 0; }

        var cb = new Callback();
        var tn = (ITrayNotify)obj!;
        int rhr = tn.RegisterCallback(cb, out uint handle);
        Console.WriteLine($"RegisterCallback -> 0x{rhr:X8} (handle {handle})");
        if (rhr == 0)
        {
            // live test: add / change / remove a tray icon of our own while listening
            using var probe = new NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Spread tray spike", Visible = true };
            var start = DateTime.UtcNow; bool changed = false;
            var end = start.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                if (!changed && DateTime.UtcNow > start.AddSeconds(2)) { probe.Text = "Spread tray spike (changed)"; changed = true; Console.WriteLine("  -- changed our own icon's tooltip"); }
                Application.DoEvents(); System.Threading.Thread.Sleep(20);
            }
            probe.Visible = false;
            var end2 = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < end2) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            int uhr = tn.UnregisterCallback(handle);
            Console.WriteLine($"UnregisterCallback -> 0x{uhr:X8}; {cb.Count} notifications received.");
        }
        Marshal.ReleaseComObject(obj!);
        return rhr == 0 ? 0 : 2;
    }
}

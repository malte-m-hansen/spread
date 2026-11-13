using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Spread;

// Sound devices through Core Audio. Listing is standard; switching the default device uses the (undocumented but long-stable) IPolicyConfig interface.

internal sealed record AudioDev(string Id, string Name, bool IsDefault);

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[StructLayout(LayoutKind.Sequential)] internal struct PROPERTYKEY { public Guid fmtid; public uint pid; }
[StructLayout(LayoutKind.Sequential)] internal struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] internal class PolicyConfigClient { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility();
}

/// <summary>
/// Output (render) and input (capture) devices: list, default, volume and mute. Call from the UI (STA) thread: the COM objects are kept
/// (one enumerator, the volume endpoint for about 2 s) and belong to the thread that made them. Before, the panel's 1 s refresh and every
/// mouse move of a slider drag created a new enumerator + device + endpoint.
/// </summary>
internal static class AudioCtl
{
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    static Guid _ctx = Guid.Empty;
    static IMMDeviceEnumerator? _en;
    static readonly IAudioEndpointVolume?[] _ep = new IAudioEndpointVolume?[2];   // [0] output, [1] input
    static readonly long[] _epAt = new long[2];
    const int EndpointTtlMs = 2000;   // the default device can change (headset plugged in): look it up again after this

    static IMMDeviceEnumerator Enumerator() => _en ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    static void Forget(bool capture) => _ep[capture ? 1 : 0] = null;

    static string NameOf(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var store) != 0) return "?";
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // PKEY_Device_FriendlyName
            if (store.GetValue(ref key, out var pv) != 0) return "?";
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "?" : "?"; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return "?"; }
    }

    public static string? DefaultId(bool capture)
    {
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(capture ? 1 : 0, capture ? 0 : 1, out var dev) != 0) return null;
            return dev.GetId(out string id) == 0 ? id : null;
        }
        catch { return null; }
    }

    public static List<AudioDev> List(bool capture)
    {
        var res = new List<AudioDev>();
        try
        {
            var en = Enumerator();
            string? def = DefaultId(capture);
            if (en.EnumAudioEndpoints(capture ? 1 : 0, 1 /*DEVICE_STATE_ACTIVE*/, out var col) != 0) return res;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                if (dev.GetId(out string id) != 0) continue;
                res.Add(new AudioDev(id, NameOf(dev), id == def));
            }
        }
        catch { }
        return res;
    }

    /// <summary>Makes a device the default for all roles (what Windows' own sound picker does).</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(id, role) == 0;
            Forget(false); Forget(true);   // the sliders follow the new device at once
            return ok;
        }
        catch (Exception e) { Log.Write("set default audio device failed: " + e.GetType().Name); return false; }
    }

    static IAudioEndpointVolume? Endpoint(bool capture)
    {
        int i = capture ? 1 : 0;
        long now = Environment.TickCount64;
        if (_ep[i] != null && now - _epAt[i] < EndpointTtlMs) return _ep[i];
        _ep[i] = null;
        try
        {
            int hr = Enumerator().GetDefaultAudioEndpoint(capture ? 1 : 0, capture ? 0 : 1, out var dev);
            if (hr != 0) { if (hr != unchecked((int)0x80070490)) _en = null; return null; }   // E_NOTFOUND = no such device; anything else: start over with a new enumerator
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0) return null;
            _ep[i] = (IAudioEndpointVolume)o; _epAt[i] = now;
            return _ep[i];
        }
        catch { _en = null; return null; }
    }

    public static bool TryGet(bool capture, out float level, out bool mute)
    {
        level = 0; mute = false;
        var ep = Endpoint(capture);
        if (ep == null) return false;
        try { if (ep.GetMasterVolumeLevelScalar(out level) != 0) { Forget(capture); return false; } ep.GetMute(out mute); return true; }
        catch { Forget(capture); return false; }
    }

    public static void SetLevel(bool capture, float level) { try { Endpoint(capture)?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _ctx); } catch { Forget(capture); } }
    public static void SetMute(bool capture, bool mute) { try { Endpoint(capture)?.SetMute(mute, ref _ctx); } catch { Forget(capture); } }
}

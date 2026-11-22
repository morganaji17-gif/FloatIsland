using System.Runtime.InteropServices;

namespace SonoIsland.Services;

/// <summary>
/// Core Audio access for the volume/output surface: the current default render endpoint's
/// friendly name and master volume, a change callback, and switching the default output
/// device (the volume surface).
/// </summary>
internal static class AudioOutput
{
    public static event Action<float, bool>? VolumeChanged;   // scalar 0..1, muted

    private static IMMDeviceEnumerator? _enum;
    private static IMMDevice? _device;
    private static IAudioEndpointVolume? _volume;
    private static VolumeCallback? _callback;
    private static string _deviceName = "";
    private static string _deviceId = "";

    public static string DeviceName => _deviceName;
    public static float Volume { get; private set; }
    public static bool Muted { get; private set; }

    public static void Start()
    {
        try
        {
            _enum = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Attach();
        }
        catch (Exception ex) { Log.Warn("AudioOutput: " + ex.Message); }
    }

    public static void Stop()
    {
        try { if (_volume != null && _callback != null) _volume.UnregisterControlChangeNotify(_callback); } catch { }
        _volume = null; _device = null; _callback = null;
        _enum = null;
    }

    private static void Attach()
    {
        if (_enum == null) return;
        if (_enum.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eMultimedia*/, out var dev) != 0 || dev == null) return;
        _device = dev;

        dev.GetId(out string id);
        _deviceId = id;
        _deviceName = FriendlyName(dev);

        var iid = IID_IAudioEndpointVolume;
        if (dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out var iface) != 0 || iface is not IAudioEndpointVolume vol) return;
        _volume = vol;
        _callback = new VolumeCallback(OnVolumeChanged);
        _volume.RegisterControlChangeNotify(_callback);
        _volume.GetMasterVolumeLevelScalar(out float v); Volume = v;
        _volume.GetMute(out bool m); Muted = m;
    }

    private static void OnVolumeChanged()
    {
        try
        {
            if (_volume == null) return;
            _volume.GetMasterVolumeLevelScalar(out float v);
            _volume.GetMute(out bool m);
            Volume = v; Muted = m;
            VolumeChanged?.Invoke(v, m);
        }
        catch { }
    }

    /// <summary>Active render device friendly names.</summary>
    public static List<string> ActiveDevices()
    {
        var list = new List<string>();
        try
        {
            if (_enum == null) return list;
            if (_enum.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out var col) != 0 || col == null) return list;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) == 0 && d != null) list.Add(FriendlyName(d));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Switches the default playback device to the next active one (cycles).</summary>
    public static string CycleDevice()
    {
        try
        {
            if (_enum == null) return _deviceName;
            if (_enum.EnumAudioEndpoints(0, 1, out var col) != 0 || col == null) return _deviceName;
            col.GetCount(out uint n);
            if (n < 2) return _deviceName;

            int cur = -1;
            var ids = new List<string>();
            var names = new List<string>();
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) != 0 || d == null) continue;
                d.GetId(out string id);
                ids.Add(id);
                names.Add(FriendlyName(d));
                if (string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase)) cur = ids.Count - 1;
            }
            if (ids.Count < 2) return _deviceName;

            int next = (cur + 1) % ids.Count;
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            policy.SetDefaultEndpoint(ids[next], 0);
            policy.SetDefaultEndpoint(ids[next], 1);
            policy.SetDefaultEndpoint(ids[next], 2);
            Marshal.ReleaseComObject(policy);

            try { if (_volume != null && _callback != null) _volume.UnregisterControlChangeNotify(_callback); } catch { }
            _volume = null; _callback = null; _device = null;
            Attach();
            Log.Info($"[audio] output -> {names[next]}");
            return names[next];
        }
        catch (Exception ex) { Log.Warn("AudioOutput switch: " + ex.Message); return _deviceName; }
    }

    private static string FriendlyName(IMMDevice d)
    {
        try
        {
            d.OpenPropertyStore(0, out var store);
            var key = new PROPERTYKEY
            {
                fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                pid = 14
            };
            store.GetValue(ref key, out var pv);
            if (pv.pointer != IntPtr.Zero) return Marshal.PtrToStringUni(pv.pointer) ?? "";
            return "";
        }
        catch { return ""; }
    }

    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    // ---- COM interop
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] private class CPolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(uint access, out IPropertyStore store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public short vt; public short r1, r2, r3; public IntPtr pointer; }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        void OnNotify(IntPtr notify);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float level);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Managed callback object; the CLR wraps it in a CCW when handed to COM.</summary>
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action _onNotify;
        public VolumeCallback(Action onNotify) => _onNotify = onNotify;
        public void OnNotify(IntPtr notify) => _onNotify();
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string id, IntPtr fmt);
        int GetDeviceFormat(string id, bool def, IntPtr fmt);
        int ResetDeviceFormat(string id);
        int SetDeviceFormat(string id, IntPtr end, IntPtr mix);
        int GetProcessingPeriod(string id, bool def, IntPtr a, IntPtr b);
        int SetProcessingPeriod(string id, IntPtr p);
        int GetShareMode(string id, IntPtr m);
        int SetShareMode(string id, IntPtr m);
        int GetPropertyValue(string id, ref PROPERTYKEY key, out PROPVARIANT v);
        int SetPropertyValue(string id, ref PROPERTYKEY key, ref PROPVARIANT v);
        int SetDefaultEndpoint(string id, int role);
        int SetEndpointVisibility(string id, bool visible);
    }
}

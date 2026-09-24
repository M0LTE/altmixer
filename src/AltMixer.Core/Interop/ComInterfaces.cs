using System.Runtime.InteropServices;

namespace AltMixer.Core.Interop;

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[Flags]
public enum DeviceState : uint { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8, All = 0xF }

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid fmtid;
    public int pid;
    public PropertyKey(string g, int p) { fmtid = new Guid(g); pid = p; }
    public override string ToString() => $"{{{fmtid}}},{pid}";
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptr;
    [FieldOffset(8)] public int i4;
    [FieldOffset(8)] public long i8;
    [FieldOffset(8)] public short boolVal;
    [FieldOffset(8)] public float r4;
    [FieldOffset(8)] public uint cb;
    [FieldOffset(16)] public IntPtr blob;

    public static PropVariant FromUInt(uint v) => new() { vt = 19, i4 = (int)v };
}

public static class Native
{
    public const uint CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("combase.dll")] public static extern int RoInitialize(int type);
    [DllImport("combase.dll")] public static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string s, int len, out IntPtr h);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll")] public static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);

    public static string? HStringToString(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        var p = WindowsGetStringRawBuffer(h, out var len);
        return Marshal.PtrToStringUni(p, (int)len);
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorCo { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow flow, DeviceState mask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(uint stgmAccess, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out DeviceState state);
}

[ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMEndpoint
{
    void GetDataFlow(out EDataFlow flow);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, DeviceState state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float db, ref Guid ctx);
    void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    void GetMasterVolumeLevel(out float db);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
    void SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    void GetChannelVolumeLevel(uint ch, out float db);
    void GetChannelVolumeLevelScalar(uint ch, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    void GetVolumeStepInfo(out uint step, out uint count);
    void VolumeStepUp(ref Guid ctx);
    void VolumeStepDown(ref Guid ctx);
    void QueryHardwareSupport(out uint mask);
    void GetVolumeRange(out float minDb, out float maxDb, out float incDb);
}

// ---------- Sessions ----------

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    void GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr ctl);
    void GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr vol);
    void GetSessionEnumerator(out IAudioSessionEnumerator e);
    void RegisterSessionNotification(IntPtr n);
    void UnregisterSessionNotification(IntPtr n);
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr n);
    void UnregisterDuckNotification(IntPtr n);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    void GetGroupingParam(out Guid g);
    void SetGroupingParam(ref Guid g, ref Guid ctx);
    void RegisterAudioSessionNotification(IntPtr n);
    void UnregisterAudioSessionNotification(IntPtr n);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid ctx);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

// ---------- Device topology ----------

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
    void GetSubunitCount(out uint count);
    void GetSubunit(uint index, out IntPtr subunit);
    void GetPartById(uint id, out IPart part);
    void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSignalPath(IntPtr from, IntPtr to, int rejectMixed, out IntPtr parts);
}

[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnector
{
    void GetConnectorType(out int type);
    void GetDataFlow(out int flow);
    void ConnectTo(IConnector other);
    void Disconnect();
    void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    void GetConnectedTo(out IConnector other);
    void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPart
{
    void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetLocalId(out uint id);
    void GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetPartType(out int type);
    void GetSubType(out Guid subtype);
    void GetControlInterfaceCount(out uint count);
    void GetControlInterface(uint index, out IControlInterface ci);
    void EnumPartsIncoming(out IPartsList parts);
    void EnumPartsOutgoing(out IPartsList parts);
    void GetTopologyObject(out IDeviceTopology topo);
    void Activate(uint clsCtx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void RegisterControlChangeCallback(ref Guid iid, IntPtr cb);
    void UnregisterControlChangeCallback(IntPtr cb);
}

[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPartsList
{
    void GetCount(out uint count);
    void GetPart(uint index, out IPart part);
}

[ComImport, Guid("45d37c3f-5140-444a-ae24-400789f3cbf3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IControlInterface
{
    void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetIID(out Guid iid);
}

[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioVolumeLevel
{
    void GetChannelCount(out uint count);
    void GetLevelRange(uint ch, out float minDb, out float maxDb, out float stepDb);
    void GetLevel(uint ch, out float db);
    void SetLevel(uint ch, float db, ref Guid ctx);
    void SetLevelUniform(float db, ref Guid ctx);
    void SetLevelAllChannels(IntPtr levels, uint count, ref Guid ctx);
}

[ComImport, Guid("85401FD4-6DE4-4b9d-9869-2D6753A82F3C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioAutoGainControl
{
    void GetEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    void SetEnabled([MarshalAs(UnmanagedType.Bool)] bool enabled, ref Guid ctx);
}

[ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMute
{
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("7D8B1437-DD53-4350-9C1B-1EE2890BD938"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioLoudness
{
    void GetEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    void SetEnabled([MarshalAs(UnmanagedType.Bool)] bool enabled, ref Guid ctx);
}

// ---------- Undocumented: IPolicyConfig (used by mmsys.cpl, EarTrumpet, SoundSwitch) ----------

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
public class PolicyConfigClientCo { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out IntPtr fmt);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out long defaultPeriod, out long minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int bFxStore, ref PropertyKey key, out PropVariant pv);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int bFxStore, ref PropertyKey key, ref PropVariant pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

// ---------- Undocumented: per-app default endpoint (Windows.Media.Internal.AudioPolicyConfig) ----------
// Called through the raw vtable so we don't depend on WinRT projection support.
public static unsafe class AppRouting
{
    static readonly Guid IID_21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");
    const int SlotSet = 25, SlotGet = 26;
    static IntPtr _factory;

    public static int Init()
    {
        Native.RoInitialize(1);
        Native.WindowsCreateString("Windows.Media.Internal.AudioPolicyConfig", 40, out var cls);
        var iid = IID_21H2;
        var hr = Native.RoGetActivationFactory(cls, ref iid, out _factory);
        Native.WindowsDeleteString(cls);
        return hr;
    }

    /// <summary>Extracts the MMDevice endpoint id from a SWD interface path, or null.</summary>
    public static string? FromSwdId(string? swd)
    {
        if (swd == null) return null;
        var start = swd.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += 9;
        var end = swd.IndexOf("#{", start, StringComparison.Ordinal);
        return end < 0 ? swd[start..] : swd[start..end];
    }

    public static string ToSwdId(string mmId, EDataFlow flow) =>
        $@"\\?\SWD#MMDEVAPI#{mmId}#{(flow == EDataFlow.Render ? "{e6327cad-dcec-4949-ae8a-991e976a79d2}" : "{2eef81be-33fa-4800-9670-1cd474972c3f}")}";

    public static bool Available => _factory != IntPtr.Zero;

    public static int Set(uint pid, EDataFlow flow, ERole role, string? mmId)
    {
        if (_factory == IntPtr.Zero) return unchecked((int)0x80004005);
        IntPtr h = IntPtr.Zero;
        if (mmId != null) { var s = ToSwdId(mmId, flow); Native.WindowsCreateString(s, s.Length, out h); }
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr, int>)(*(IntPtr**)_factory)[SlotSet];
        var hr = fn(_factory, pid, flow, role, h);
        if (h != IntPtr.Zero) Native.WindowsDeleteString(h);
        return hr;
    }

    public static int Get(uint pid, EDataFlow flow, ERole role, out string? swdId)
    {
        swdId = null;
        if (_factory == IntPtr.Zero) return unchecked((int)0x80004005);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr*, int>)(*(IntPtr**)_factory)[SlotGet];
        IntPtr h;
        var hr = fn(_factory, pid, flow, role, &h);
        swdId = Native.HStringToString(h);
        if (h != IntPtr.Zero) Native.WindowsDeleteString(h);
        return hr;
    }
}

// ---------- WASAPI client (used to list the formats a device supports) ----------

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    void GetBufferSize(out uint frames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long def, out long min);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr h);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object svc);
}


[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    void OnNotify(IntPtr notify);
}

public static class PropKeys
{
    public static readonly PropertyKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly PropertyKey DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly PropertyKey InterfaceFriendlyName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
    public static readonly PropertyKey DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);
    public static readonly PropertyKey DeviceFormat = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0);
    public static readonly PropertyKey ExclusiveAllow = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 3);
    public static readonly PropertyKey ExclusivePriority = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 4);
    public static readonly PropertyKey Listen = new("24dbb0fc-9311-4b3d-9cf0-18ff155639d4", 1);
    public static readonly PropertyKey Spatial = new("908dba32-edff-4c28-8e45-c918561f6748", 2);
    public static readonly Guid SpatialProvidersFmtid = new("a45429a4-aa63-4480-b7f8-3f2552daee93");
}

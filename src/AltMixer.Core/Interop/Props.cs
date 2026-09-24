using System.Runtime.InteropServices;

namespace AltMixer.Core.Interop;

/// <summary>Typed reads/writes of endpoint properties through IPolicyConfig (works unelevated; see spike/FINDINGS.md).</summary>
public static class Props
{
    const ushort VT_BOOL = 11, VT_UI4 = 19, VT_LPWSTR = 31, VT_BLOB = 65;

    public static string? ReadString(IPropertyStore ps, PropertyKey key)
    {
        if (ps.GetValue(ref key, out var v) != 0) return null;
        try { return v.vt == VT_LPWSTR ? Marshal.PtrToStringUni(v.ptr) : null; }
        finally { Native.PropVariantClear(ref v); }
    }

    public static uint? ReadUInt(IPolicyConfig pc, string id, bool fx, PropertyKey key)
    {
        if (pc.GetPropertyValue(id, fx ? 1 : 0, ref key, out var v) != 0) return null;
        try { return v.vt == VT_UI4 ? (uint)v.i4 : null; }
        finally { Native.PropVariantClear(ref v); }
    }

    public static bool? ReadBool(IPolicyConfig pc, string id, PropertyKey key)
    {
        if (pc.GetPropertyValue(id, 0, ref key, out var v) != 0) return null;
        try { return v.vt == VT_BOOL ? v.boolVal != 0 : null; }
        finally { Native.PropVariantClear(ref v); }
    }

    public static byte[]? ReadBlob(IPolicyConfig pc, string id, PropertyKey key)
    {
        if (pc.GetPropertyValue(id, 0, ref key, out var v) != 0) return null;
        try
        {
            if (v.vt != VT_BLOB || v.blob == IntPtr.Zero) return null;
            var b = new byte[v.cb];
            Marshal.Copy(v.blob, b, 0, b.Length);
            return b;
        }
        finally { Native.PropVariantClear(ref v); }
    }

    public static IEnumerable<(PropertyKey key, byte[] blob)> ReadBlobs(IPropertyStore ps, Guid fmtid)
    {
        ps.GetCount(out var n);
        for (uint i = 0; i < n; i++)
        {
            ps.GetAt(i, out var key);
            if (key.fmtid != fmtid) continue;
            if (ps.GetValue(ref key, out var v) != 0) continue;
            byte[]? b = null;
            if (v.vt == VT_BLOB && v.blob != IntPtr.Zero) { b = new byte[v.cb]; Marshal.Copy(v.blob, b, 0, b.Length); }
            Native.PropVariantClear(ref v);
            if (b != null) yield return (key, b);
        }
    }

    public static void WriteUInt(IPolicyConfig pc, string id, bool fx, PropertyKey key, uint value)
    {
        var pv = new PropVariant { vt = VT_UI4, i4 = (int)value };
        Check(pc.SetPropertyValue(id, fx ? 1 : 0, ref key, ref pv));
    }

    public static void WriteBool(IPolicyConfig pc, string id, PropertyKey key, bool value)
    {
        var pv = new PropVariant { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) };
        Check(pc.SetPropertyValue(id, 0, ref key, ref pv));
    }

    public static void WriteBlob(IPolicyConfig pc, string id, PropertyKey key, byte[] value)
    {
        var mem = Marshal.AllocCoTaskMem(value.Length);
        try
        {
            Marshal.Copy(value, 0, mem, value.Length);
            var pv = new PropVariant { vt = VT_BLOB, cb = (uint)value.Length, blob = mem };
            Check(pc.SetPropertyValue(id, 0, ref key, ref pv));
        }
        finally { Marshal.FreeCoTaskMem(mem); }
    }

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}

using System.Runtime.InteropServices;
using AltMixer.Core.Interop;

namespace AltMixer.Core.Audio;

public readonly record struct AudioFormat(int Rate, int Bits, int ValidBits, int Channels, int ChannelMask, bool IsFloat)
{
    public string Id => $"{Rate}:{Bits}:{ValidBits}";
    public string Label => $"{ValidBits}-bit{(Bits != ValidBits ? $" (in {Bits})" : "")}, {Rate / 1000.0:0.###} kHz";

    static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static AudioFormat Read(IntPtr p)
    {
        var tag = (ushort)Marshal.ReadInt16(p, 0);
        var ch = Marshal.ReadInt16(p, 2);
        var rate = Marshal.ReadInt32(p, 4);
        var bits = Marshal.ReadInt16(p, 14);
        if (tag == 0xFFFE)
        {
            var valid = Marshal.ReadInt16(p, 18);
            var mask = Marshal.ReadInt32(p, 20);
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            return new(rate, bits, valid == 0 ? bits : valid, ch, mask, sub == FloatSubtype);
        }
        return new(rate, bits, bits, ch, ch == 1 ? 4 : 3, tag == 3);
    }

    /// <summary>Allocates a WAVEFORMATEXTENSIBLE with CoTaskMemAlloc; caller frees.</summary>
    public IntPtr Alloc()
    {
        var p = Marshal.AllocCoTaskMem(40);
        var blockAlign = (short)(Channels * Bits / 8);
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
        Marshal.WriteInt16(p, 2, (short)Channels);
        Marshal.WriteInt32(p, 4, Rate);
        Marshal.WriteInt32(p, 8, Rate * blockAlign);
        Marshal.WriteInt16(p, 12, blockAlign);
        Marshal.WriteInt16(p, 14, (short)Bits);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, (short)ValidBits);
        Marshal.WriteInt32(p, 20, ChannelMask);
        Marshal.StructureToPtr(IsFloat ? FloatSubtype : PcmSubtype, p + 24, false);
        return p;
    }

    public static AudioFormat? Parse(string id, AudioFormat like)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var b) || !int.TryParse(parts[2], out var v)) return null;
        return like with { Rate = r, Bits = b, ValidBits = v, IsFloat = false };
    }

    static readonly int[] Rates = [44100, 48000, 88200, 96000, 176400, 192000];
    static readonly (int bits, int valid)[] Depths = [(16, 16), (24, 24), (32, 24), (32, 32)];

    /// <summary>Formats the device accepts in exclusive mode, which is what the Sound control panel offers.</summary>
    public static List<AudioFormat> Supported(IAudioClient client, AudioFormat current)
    {
        var list = new List<AudioFormat>();
        foreach (var rate in Rates)
            foreach (var (bits, valid) in Depths)
            {
                var f = current with { Rate = rate, Bits = bits, ValidBits = valid, IsFloat = false };
                var p = f.Alloc();
                try
                {
                    if (client.IsFormatSupported(1, p, out var closest) == 0) list.Add(f);
                    if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        if (!list.Any(f => f.Id == current.Id)) list.Insert(0, current);
        return list;
    }
}

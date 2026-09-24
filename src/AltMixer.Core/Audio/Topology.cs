using AltMixer.Core.Interop;
using AltMixer.Core.Model;

namespace AltMixer.Core.Audio;

/// <summary>A driver control found by walking the device topology (mic boost, AGC, monitor paths...).</summary>
sealed class HwControl
{
    public required string SettingId { get; init; }
    public required string Label { get; init; }
    public required SettingKind Kind { get; init; }
    public required object Control { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
}

static class Topology
{
    static readonly Guid Volume = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");
    static readonly Guid Mute = new("02B223C0-C557-11D0-8A2B-00A0C9255AC1");
    static readonly HashSet<Guid> Mixers =
    [
        new("DA441A60-C556-11D0-8A2B-00A0C9255AC1"), // SUM
        new("E573ADC0-C555-11D0-8A2B-00A0C9255AC1"), // SUPERMIX
        new("2CEAF780-C556-11D0-8A2B-00A0C9255AC1"), // MUX
    ];
    const int SoftwareIo = 3, SoftwareFixed = 4;

    /// <summary>The KS filter path behind an endpoint (contains USB VID/PID), or null for virtual devices.</summary>
    public static string? FilterId(IMMDevice dev)
    {
        try
        {
            var topo = AudioSystem.Activate<IDeviceTopology>(dev);
            topo.GetConnector(0, out var conn);
            conn.GetDeviceIdConnectedTo(out var id);
            return id;
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds driver controls reachable from the endpoint. Controls on the endpoint's own signal path that duplicate the
    /// endpoint volume/mute are skipped; controls on other paths (e.g. a CM108's mic→speaker monitor) are labelled.
    /// </summary>
    public static List<HwControl> Find(string deviceId, IMMDevice dev, Flow flow, float epMin, float epMax, bool epHwMute, HashSet<string> claimed)
    {
        var result = new List<HwControl>();
        IPart start;
        string filter;
        try
        {
            var topo = AudioSystem.Activate<IDeviceTopology>(dev);
            topo.GetConnector(0, out var conn);
            conn.GetConnectedTo(out var other);
            start = (IPart)other;
            start.GetTopologyObject(out var hw);
            hw.GetDeviceId(out filter);
        }
        catch { return result; }

        var outgoing = flow == Flow.Capture;
        var leaves = new Dictionary<string, HashSet<(string name, int type)>>();
        var order = new List<(IPart part, string gid, bool pastMixer)>();
        Visit(start, outgoing, leaves, order, 0, false);

        foreach (var (part, gid, pastMixer) in order)
        {
            var reach = leaves[gid];
            var main = reach.Any(l => l.type is SoftwareIo or SoftwareFixed);
            // Off our own path, anything beyond a mixer belongs to the other endpoint (e.g. the speaker's own volume).
            if (!main && pastMixer) continue;
            if (!claimed.Add($"{filter}|{gid}")) continue;
            var leafName = reach.Select(l => l.name).FirstOrDefault(n => n.Length > 0) ?? "other output";
            try
            {
                part.GetName(out var name);
                part.GetLocalId(out var localId);
                part.GetSubType(out var sub);
                part.GetControlInterfaceCount(out var n);
                for (uint i = 0; i < n; i++)
                {
                    part.GetControlInterface(i, out var ci);
                    ci.GetIID(out var iid);
                    part.Activate(Native.CLSCTX_ALL, ref iid, out var ctl);
                    var label = main ? name : $"Monitor {(outgoing ? "→" : "←")} {leafName}: {name}";
                    var id = Ids.Dev(deviceId, $"hw/{localId}/");
                    switch (ctl)
                    {
                        case IAudioVolumeLevel lvl:
                            lvl.GetLevelRange(0, out var mn, out var mx, out var st);
                            if (main && sub == Volume && Math.Abs(mn - epMin) < 0.01 && Math.Abs(mx - epMax) < 0.01) break;
                            result.Add(new HwControl { SettingId = id + "level", Label = main && name == "Volume" ? "Hardware volume" : label, Kind = SettingKind.Level, Control = lvl, Min = mn, Max = mx, Step = st });
                            break;
                        case IAudioAutoGainControl:
                            result.Add(new HwControl { SettingId = id + "agc", Label = main ? "Automatic gain control (AGC)" : label, Kind = SettingKind.Toggle, Control = ctl });
                            break;
                        case IAudioMute:
                            if (main && sub == Mute && epHwMute) break;
                            result.Add(new HwControl { SettingId = id + "mute", Label = label, Kind = SettingKind.Toggle, Control = ctl });
                            break;
                        case IAudioLoudness:
                            result.Add(new HwControl { SettingId = id + "loudness", Label = main ? "Loudness" : label, Kind = SettingKind.Toggle, Control = ctl });
                            break;
                    }
                }
            }
            catch { /* a part we can't read; skip it */ }
        }
        return result;
    }

    static HashSet<(string, int)> Visit(IPart part, bool outgoing, Dictionary<string, HashSet<(string, int)>> leaves, List<(IPart, string, bool)> order, int depth, bool pastMixer)
    {
        part.GetGlobalId(out var gid);
        if (leaves.TryGetValue(gid, out var known)) return known;
        var mine = new HashSet<(string, int)>();
        leaves[gid] = mine;
        order.Add((part, gid, pastMixer));
        part.GetSubType(out var sub);
        pastMixer |= Mixers.Contains(sub);
        if (depth > 40) return mine;

        IPartsList? list = null;
        try { if (outgoing) part.EnumPartsOutgoing(out list); else part.EnumPartsIncoming(out list); } catch { }
        uint count = 0;
        list?.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            list!.GetPart(i, out var child);
            mine.UnionWith(Visit(child, outgoing, leaves, order, depth + 1, pastMixer));
        }
        if (count == 0 && depth > 0 && part is IConnector c)
        {
            part.GetName(out var name);
            c.GetConnectorType(out var type);
            mine.Add((name, type));
        }
        return mine;
    }
}

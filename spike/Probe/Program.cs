using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Probe;

var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
var clock = Stopwatch.StartNew();

var cmd = args.Length > 0 ? args[0] : "list";
var rest = args.Skip(1).ToArray();

switch (cmd)
{
    case "list": List(rest.Contains("--all")); break;
    case "props": Props(Find(rest[0])); break;
    case "topo": Topo(Find(rest[0])); break;
    case "sessions": Sessions(); break;
    case "defaults": Defaults(); break;
    case "setdefault": SetDefault(Find(rest[0]), rest.Length > 1 ? rest[1] : "all"); break;
    case "watch": Watch(rest.Contains("--lock"), rest.FirstOrDefault(a => int.TryParse(a, out _)) is { } s ? int.Parse(s) : 0); break;
    case "bench-revert": BenchRevert(); break;
    case "fx": Fx(Find(rest[0]), rest.Length > 1 ? rest[1] : "get"); break;
    case "format": Format(Find(rest[0])); break;
    case "snapshot": Snapshot(rest[0]); break;
    case "diff": Diff(rest[0], rest[1]); break;
    case "approute": AppRoute(); break;
    case "measure": Measure(Find(rest[0])); break;
    case "sessvol": SessVol(Find(rest[0]), uint.Parse(rest[1]), float.Parse(rest[2])); break;
    case "setlevel": { var v = Activate<IAudioEndpointVolume>(Find(rest[0]).Device); var g = Guid.Empty; v.SetMasterVolumeLevel(float.Parse(rest[1]), ref g); Console.WriteLine("ok"); break; }
    case "setprop": SetProp(Find(rest[0]), int.Parse(rest[1]), new PropertyKey(rest[2], int.Parse(rest[3])), rest[4]); break;
    default: Console.WriteLine("unknown command"); break;
}

// ---------------------------------------------------------------------------

List<Dev> AllDevices(DeviceState mask = DeviceState.All)
{
    enumerator.EnumAudioEndpoints(EDataFlow.All, mask, out var coll);
    coll.GetCount(out var n);
    var list = new List<Dev>();
    for (uint i = 0; i < n; i++)
    {
        coll.Item(i, out var d);
        d.GetId(out var id);
        d.GetState(out var st);
        ((IMMEndpoint)d).GetDataFlow(out var flow);
        d.OpenPropertyStore(0, out var ps);
        list.Add(new Dev(id, flow, st, d, ReadString(ps, Keys.FriendlyName) ?? "?", ReadString(ps, Keys.InterfaceFriendlyName) ?? ""));
    }
    return list.OrderBy(x => x.Flow).ThenBy(x => x.State != DeviceState.Active).ThenBy(x => x.Name).ToList();
}

Dev Find(string q)
{
    var all = AllDevices();
    if (int.TryParse(q, out var idx)) return all[idx];
    return all.First(d => d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
}

string? DefaultId(EDataFlow flow, ERole role)
{
    if (enumerator.GetDefaultAudioEndpoint(flow, role, out var d) != 0) return null;
    d.GetId(out var id);
    return id;
}

void List(bool includeAll)
{
    var all = AllDevices();
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    for (int i = 0; i < all.Count; i++)
    {
        var d = all[i];
        if (!includeAll && d.State is DeviceState.NotPresent) continue;
        var marks = "";
        if (DefaultId(d.Flow, ERole.Console) == d.Id) marks += "D";
        if (DefaultId(d.Flow, ERole.Communications) == d.Id) marks += "C";
        Console.WriteLine($"[{i,2}] {(d.Flow == EDataFlow.Render ? "OUT" : "IN "),-3} {d.State,-10} {marks,-2} {d.Name}");
        Console.WriteLine($"       id={d.Id}");
        if (d.State != DeviceState.Active) continue;
        try
        {
            var vol = Activate<IAudioEndpointVolume>(d.Device);
            vol.GetVolumeRange(out var min, out var max, out var inc);
            vol.GetMasterVolumeLevel(out var db);
            vol.GetMasterVolumeLevelScalar(out var sc);
            vol.GetMute(out var mute);
            vol.GetChannelCount(out var ch);
            vol.QueryHardwareSupport(out var hw);
            var chans = string.Join(" ", Enumerable.Range(0, (int)ch).Select(c => { vol.GetChannelVolumeLevel((uint)c, out var v); return $"{v:0.0}"; }));
            Console.WriteLine($"       vol={db:0.00} dB (scalar {sc:0.000}) range [{min:0.00} .. {max:0.00}] step {inc:0.000}  mute={mute}  ch={ch} [{chans}] hw=0x{hw:x}");
        }
        catch (Exception e) { Console.WriteLine($"       vol: {e.Message}"); }
        var fxhr = pc.GetPropertyValue(d.Id, 1, ref Keys.DisableSysFx, out var fxv);
        Console.WriteLine($"       DisableSysFx(fx store)={(fxhr == 0 ? FormatPv(fxv) : $"hr=0x{fxhr:x8}")}  format={DeviceFormat(pc, d.Id)}");
        Native.PropVariantClear(ref fxv);
    }
}

void Defaults()
{
    foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        foreach (var role in Enum.GetValues<ERole>())
            Console.WriteLine($"{flow,-8} {role,-15} {NameOf(DefaultId(flow, role))}");
}

string NameOf(string? id) => id == null ? "(none)" : AllDevices().FirstOrDefault(d => d.Id == id)?.Name ?? id;

void SetDefault(Dev d, string which)
{
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    var roles = which switch
    {
        "comms" => new[] { ERole.Communications },
        "default" => new[] { ERole.Console, ERole.Multimedia },
        _ => Enum.GetValues<ERole>(),
    };
    foreach (var r in roles) Console.WriteLine($"SetDefaultEndpoint({d.Name}, {r}) hr=0x{pc.SetDefaultEndpoint(d.Id, r):x8}");
}

// ---- Watch / lock ----

void Watch(bool doLock, int seconds)
{
    var desired = new Dictionary<(EDataFlow, ERole), string?>();
    foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        foreach (var role in Enum.GetValues<ERole>())
            desired[(flow, role)] = DefaultId(flow, role);
    var client = new NotifyClient(clock, desired, doLock, id => NameOf(id));
    enumerator.RegisterEndpointNotificationCallback(client);
    Console.WriteLine($"watching{(doLock ? " with LOCK on current defaults" : "")}... {(seconds > 0 ? $"{seconds}s" : "Ctrl+C to stop")}");
    if (seconds > 0) Thread.Sleep(seconds * 1000); else Thread.Sleep(Timeout.Infinite);
    enumerator.UnregisterEndpointNotificationCallback(client);
    client.Stop();
}

// Simulates "Windows switched default because something was plugged in" by switching it ourselves,
// then measures how fast the lock puts it back.
void BenchRevert()
{
    var renders = AllDevices(DeviceState.Active).Where(d => d.Flow == EDataFlow.Render).ToList();
    var original = DefaultId(EDataFlow.Render, ERole.Console)!;
    var other = renders.First(d => d.Id != original);
    var desired = new Dictionary<(EDataFlow, ERole), string?>();
    foreach (var role in Enum.GetValues<ERole>()) desired[(EDataFlow.Render, role)] = DefaultId(EDataFlow.Render, role);
    var client = new NotifyClient(clock, desired, true, id => NameOf(id));
    enumerator.RegisterEndpointNotificationCallback(client);
    Console.WriteLine($"desired default = {NameOf(original)}; hijacking to {other.Name}");
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    var t0 = clock.Elapsed.TotalMilliseconds;
    pc.SetDefaultEndpoint(other.Id, ERole.Console);
    pc.SetDefaultEndpoint(other.Id, ERole.Multimedia);
    Console.WriteLine($"{t0,8:0.0} ms  hijack issued");
    Thread.Sleep(2000);
    enumerator.UnregisterEndpointNotificationCallback(client);
    client.Stop();
    Console.WriteLine($"final default = {NameOf(DefaultId(EDataFlow.Render, ERole.Console))}");
}

// ---- Enhancements ----

void Fx(Dev d, string op)
{
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    var hr = pc.GetPropertyValue(d.Id, 1, ref Keys.DisableSysFx, out var cur);
    Console.WriteLine($"{d.Name}: GetPropertyValue(DisableSysFx) hr=0x{hr:x8} value={FormatPv(cur)}");
    if (op == "get") return;
    uint newVal = op switch { "off" => 1u, "on" => 0u, _ => cur.vt == 19 ? (uint)cur.i4 : 0u }; // "rewrite" = write current back
    var pv = PropVariant.FromUInt(newVal);
    hr = pc.SetPropertyValue(d.Id, 1, ref Keys.DisableSysFx, ref pv);
    Console.WriteLine($"IPolicyConfig.SetPropertyValue(DisableSysFx={newVal}) hr=0x{hr:x8}");
    d.Device.OpenPropertyStore(2 /*STGM_READWRITE*/, out var ps);
    Console.WriteLine("(IMMDevice.OpenPropertyStore(READWRITE) succeeded)");
    hr = ps.SetValue(ref Keys.DisableSysFx, ref pv);
    Console.WriteLine($"IPropertyStore.SetValue hr=0x{hr:x8}");
}

// ---- Formats ----

void Format(Dev d)
{
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    Console.WriteLine($"device format: {DeviceFormat(pc, d.Id)}");
    Console.WriteLine($"default fmt:   {DeviceFormat(pc, d.Id, 1)}");
    Console.WriteLine($"mix format:    {(pc.GetMixFormat(d.Id, out var p) == 0 ? Wfx(p) : "n/a")}");
    if (pc.GetProcessingPeriod(d.Id, 0, out var def, out var min) == 0) Console.WriteLine($"period: default {def / 10000.0} ms, min {min / 10000.0} ms");
}

string DeviceFormat(IPolicyConfig pc, string id, int bDefault = 0) =>
    pc.GetDeviceFormat(id, bDefault, out var p) == 0 ? Wfx(p) : "n/a";

static string Wfx(IntPtr p)
{
    var tag = (ushort)Marshal.ReadInt16(p, 0);
    var ch = Marshal.ReadInt16(p, 2);
    var rate = Marshal.ReadInt32(p, 4);
    var bits = Marshal.ReadInt16(p, 14);
    var s = $"{rate} Hz {bits}-bit {ch}ch";
    if (tag == 0xFFFE)
    {
        var valid = Marshal.ReadInt16(p, 18);
        var mask = Marshal.ReadInt32(p, 20);
        var sub = Marshal.PtrToStructure<Guid>(p + 24);
        s += $" (valid {valid}, mask 0x{mask:x}, {(sub.ToString().StartsWith("00000003") ? "float" : "PCM")})";
    }
    Marshal.FreeCoTaskMem(p);
    return s;
}

// ---- Property store dump ----

void Props(Dev d)
{
    d.Device.OpenPropertyStore(0, out var ps);
    ps.GetCount(out var n);
    Console.WriteLine($"{d.Name}  ({n} properties)");
    for (uint i = 0; i < n; i++)
    {
        ps.GetAt(i, out var key);
        ps.GetValue(ref key, out var v);
        Console.WriteLine($"  {Keys.NameOf(key),-40} {FormatPv(v)}");
        Native.PropVariantClear(ref v);
    }
}

// ---- Topology (mic boost, AGC, hardware mute/volume) ----

void Topo(Dev d)
{
    Console.WriteLine($"{d.Name}");
    var topo = Activate<IDeviceTopology>(d.Device);
    topo.GetConnector(0, out var conn);
    IConnector other;
    try { conn.GetConnectedTo(out other); }
    catch (Exception e) { Console.WriteLine($"  not connected to a hardware topology: {e.Message}"); return; }
    other.GetDeviceIdConnectedTo(out var devId);
    ((IPart)other).GetTopologyObject(out var hwTopo);
    hwTopo.GetDeviceId(out var hwId);
    Console.WriteLine($"  hw filter: {hwId}");
    Walk((IPart)other, d.Flow == EDataFlow.Render, 1, new HashSet<string>());
}

void Walk(IPart part, bool incoming, int depth, HashSet<string> seen)
{
    part.GetGlobalId(out var gid);
    if (!seen.Add(gid) || depth > 30) return;
    part.GetName(out var name);
    part.GetPartType(out var type);
    part.GetSubType(out var sub);
    var pad = new string(' ', depth * 2);
    Console.WriteLine($"{pad}{(type == 0 ? "Connector" : "Subunit")} \"{name}\" {Keys.NodeType(sub)}");
    part.GetControlInterfaceCount(out var nci);
    for (uint i = 0; i < nci; i++)
    {
        part.GetControlInterface(i, out var ci);
        ci.GetIID(out var iid);
        ci.GetName(out var ciName);
        try
        {
            part.Activate(Native.CLSCTX_ALL, ref iid, out var o);
            switch (o)
            {
                case IAudioVolumeLevel lvl:
                    lvl.GetChannelCount(out var ch);
                    for (uint c = 0; c < ch; c++)
                    {
                        lvl.GetLevelRange(c, out var mn, out var mx, out var st);
                        lvl.GetLevel(c, out var cur);
                        Console.WriteLine($"{pad}  * VolumeLevel ch{c}: {cur:0.00} dB  range [{mn:0.00} .. {mx:0.00}] step {st:0.00}");
                    }
                    break;
                case IAudioAutoGainControl agc: agc.GetEnabled(out var en); Console.WriteLine($"{pad}  * AGC enabled={en}"); break;
                case IAudioMute m: m.GetMute(out var mu); Console.WriteLine($"{pad}  * Mute={mu}"); break;
                case IAudioLoudness l: l.GetEnabled(out var le); Console.WriteLine($"{pad}  * Loudness={le}"); break;
                default: Console.WriteLine($"{pad}  * {ciName} {iid}"); break;
            }
        }
        catch (Exception e) { Console.WriteLine($"{pad}  * {ciName}: {e.Message}"); }
    }
    foreach (var dirIn in new[] { incoming, !incoming })
    {
        IPartsList list;
        try { if (dirIn) part.EnumPartsIncoming(out list); else part.EnumPartsOutgoing(out list); }
        catch (Exception e) { if (depth == 1) Console.WriteLine($"{pad}  ({(dirIn ? "incoming" : "outgoing")}: {e.Message.Split('\r')[0]})"); continue; }
        list.GetCount(out var n);
        for (uint i = 0; i < n; i++) { list.GetPart(i, out var p); Walk(p, dirIn, depth + 1, seen); }
        if (depth > 1) break; // after the first hop keep going in one direction only
    }
}

void SessVol(Dev d, uint pid, float db)
{
    var ep = Activate<IAudioEndpointVolume>(d.Device);
    ep.GetMasterVolumeLevel(out var before);
    var mgr = Activate<IAudioSessionManager2>(d.Device);
    mgr.GetSessionEnumerator(out var e);
    e.GetCount(out var n);
    var g = Guid.Empty;
    for (int i = 0; i < n; i++)
    {
        e.GetSession(i, out var s);
        s.GetProcessId(out var p);
        if (p != pid) continue;
        var v = (ISimpleAudioVolume)s;
        v.GetMasterVolume(out var old);
        v.SetMasterVolume((float)Math.Pow(10, db / 20), ref g);
        Thread.Sleep(300);
        v.GetMasterVolume(out var now);
        ep.GetMasterVolumeLevel(out var after);
        Console.WriteLine($"session {20 * Math.Log10(old):0.0} -> {20 * Math.Log10(now):0.0} dB; endpoint {before:0.0} -> {after:0.0} dB");
    }
}

// ---- Sessions ----

void Sessions()
{
    var hr = AppRouting.Init();
    Console.WriteLine($"AudioPolicyConfig factory hr=0x{hr:x8}");
    foreach (var d in AllDevices(DeviceState.Active))
    {
        var mgr = Activate<IAudioSessionManager2>(d.Device);
        mgr.GetSessionEnumerator(out var e);
        e.GetCount(out var n);
        if (n == 0) continue;
        Console.WriteLine($"{(d.Flow == EDataFlow.Render ? "OUT" : "IN ")} {d.Name}");
        for (int i = 0; i < n; i++)
        {
            e.GetSession(i, out var s);
            s.GetProcessId(out var pid);
            s.GetState(out var st);
            s.GetDisplayName(out var dn);
            var sys = s.IsSystemSoundsSession() == 0;
            var vol = (ISimpleAudioVolume)s;
            vol.GetMasterVolume(out var v);
            vol.GetMute(out var m);
            string proc;
            try { proc = sys ? "System Sounds" : Process.GetProcessById((int)pid).MainModule?.FileName ?? "?"; } catch { proc = $"pid {pid} (no access)"; }
            var route = "";
            if (hr == 0 && !sys) { AppRouting.Get(pid, d.Flow, ERole.Multimedia, out var r); route = r == null ? "route=default" : $"route={r}"; }
            var db = v <= 0 ? "-inf" : $"{20 * Math.Log10(v):0.0}";
            Console.WriteLine($"   pid {pid,-6} {(st == 1 ? "active" : st == 0 ? "inactive" : "expired"),-8} {db,6} dB mute={m,-5} {proc} {dn} {route}");
        }
    }
}

// Test per-app routing on our own process: set, read back, reset.
void AppRoute()
{
    var hr = AppRouting.Init();
    Console.WriteLine($"factory hr=0x{hr:x8}");
    if (hr != 0) return;
    var pid = (uint)Environment.ProcessId;
    var target = AllDevices(DeviceState.Active).First(d => d.Flow == EDataFlow.Render && d.Id != DefaultId(EDataFlow.Render, ERole.Console));
    AppRouting.Get(pid, EDataFlow.Render, ERole.Multimedia, out var before);
    Console.WriteLine($"before: {before ?? "(default)"}");
    Console.WriteLine($"set -> {target.Name}: hr=0x{AppRouting.Set(pid, EDataFlow.Render, ERole.Multimedia, target.Id):x8}");
    AppRouting.Get(pid, EDataFlow.Render, ERole.Multimedia, out var after);
    Console.WriteLine($"after:  {after}");
    Console.WriteLine($"reset: hr=0x{AppRouting.Set(pid, EDataFlow.Render, ERole.Multimedia, null):x8}");
    AppRouting.Get(pid, EDataFlow.Render, ERole.Multimedia, out var reset);
    Console.WriteLine($"final:  {reset ?? "(default)"}");
}

// ---- Registry snapshot / diff (to discover where each Settings toggle lives) ----

void Snapshot(string file)
{
    var lines = new List<string>();
    void Dump(RegistryKey root, string path)
    {
        using var k = root.OpenSubKey(path);
        if (k == null) return;
        foreach (var v in k.GetValueNames())
        {
            var val = k.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var s = val switch { byte[] b => Convert.ToHexString(b), string[] a => string.Join("|", a), null => "", _ => val.ToString() };
            lines.Add($"{root.Name}\\{path}\t{v}\t{s}");
        }
        foreach (var sk in k.GetSubKeyNames())
        {
            try { Dump(root, $"{path}\\{sk}"); } catch (Exception e) { lines.Add($"{root.Name}\\{path}\\{sk}\t!\t{e.GetType().Name}"); }
        }
    }
    Dump(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio");
    Dump(Registry.CurrentUser, @"Software\Microsoft\Multimedia\Audio");
    Dump(Registry.CurrentUser, @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore");
    Dump(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\MMDevices");
    File.WriteAllLines(file, lines);
    Console.WriteLine($"{lines.Count} values -> {file}");
}

void Diff(string a, string b)
{
    static Dictionary<string, string> Load(string f) => File.ReadAllLines(f).Select(l => l.Split('\t')).GroupBy(p => p[0] + "\t" + p[1]).ToDictionary(g => g.Key, g => g.First()[2]);
    var x = Load(a); var y = Load(b);
    var names = AllDevices().ToDictionary(d => d.Id.Split('.').Last(), d => d.Name, StringComparer.OrdinalIgnoreCase);
    string Label(string k) { foreach (var (g, n) in names) if (k.Contains(g, StringComparison.OrdinalIgnoreCase)) return $"[{n}] "; return ""; }
    foreach (var k in x.Keys.Union(y.Keys).Order())
    {
        x.TryGetValue(k, out var o); y.TryGetValue(k, out var n);
        if (o == n) continue;
        Console.WriteLine($"{Label(k)}{k.Replace("\t", "  ::  ")}\n    - {o ?? "(absent)"}\n    + {n ?? "(absent)"}");
    }
}


// ---- Measure: does the endpoint dB label correspond to real gain on the captured stream? ----

void Measure(Dev d)
{
    var vol = Activate<IAudioEndpointVolume>(d.Device);
    vol.GetVolumeRange(out var min, out var max, out _);
    vol.GetMasterVolumeLevel(out var orig);
    var ctx = Guid.Empty;
    var levels = new List<float> { Math.Clamp(0f, min, max), max, Math.Clamp(0f, min, max), max };
    if (min > 0) levels = new List<float> { min, max, min, max };
    Console.WriteLine($"{d.Name}: range [{min} .. {max}] dB, currently {orig:0.00}");
    try
    {
        foreach (var lvl in levels)
        {
            vol.SetMasterVolumeLevel(lvl, ref ctx);
            Thread.Sleep(300);
            var (rms, peak, silent) = CaptureStats(d.Device, 1500);
            Console.WriteLine($"  label {lvl,7:0.00} dB  ->  RMS {rms,7:0.0} dBFS  peak {peak,6:0.0} dBFS {(silent ? "(all-silent buffers)" : "")}");
        }
    }
    finally { vol.SetMasterVolumeLevel(orig, ref ctx); Console.WriteLine($"  restored {orig:0.00} dB"); }
}

(double rms, double peak, bool silent) CaptureStats(IMMDevice dev, int ms)
{
    var client = Activate<IAudioClient>(dev);
    client.GetMixFormat(out var fmt);
    var bits = Marshal.ReadInt16(fmt, 14);
    var ch = Marshal.ReadInt16(fmt, 2);
    client.Initialize(0, 0, 10_000_000, 0, fmt, IntPtr.Zero);
    var iid = typeof(IAudioCaptureClient).GUID;
    client.GetService(ref iid, out var o);
    var cap = (IAudioCaptureClient)o;
    double sum = 0, peak = 0; long n = 0; bool allSilent = true;
    client.Start();
    var sw = Stopwatch.StartNew();
    var skip = 200; // let the stream settle
    while (sw.ElapsedMilliseconds < ms + skip)
    {
        Thread.Sleep(10);
        while (cap.GetBuffer(out var data, out var frames, out var flags, out _, out _) == 0 && frames > 0)
        {
            if (sw.ElapsedMilliseconds > skip)
            {
                if ((flags & 2) == 0) allSilent = false;
                if ((flags & 2) == 0 && bits == 32)
                    for (int i = 0; i < frames * ch; i++)
                    {
                        var x = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, i * 4));
                        sum += x * x; n++; peak = Math.Max(peak, Math.Abs(x));
                    }
                else n += frames * ch;
            }
            cap.ReleaseBuffer(frames);
        }
    }
    client.Stop();
    Marshal.FreeCoTaskMem(fmt);
    static double Db(double v) => v <= 0 ? double.NegativeInfinity : 20 * Math.Log10(v);
    return (Db(Math.Sqrt(sum / Math.Max(1, n))), Db(peak), allSilent);
}


// setprop <dev> <fxStore 0|1> <fmtid> <pid> <uint>: write a VT_UI4 via IPolicyConfig (unelevated permission test)
void SetProp(Dev d, int fx, PropertyKey key, string val)
{
    var pc = (IPolicyConfig)new PolicyConfigClientCo();
    var hr = pc.GetPropertyValue(d.Id, fx, ref key, out var cur);
    Console.WriteLine($"{d.Name} {key} before: hr=0x{hr:x8} {FormatPv(cur)}");
    var pv = bool.TryParse(val, out var b) ? new PropVariant { vt = 11, boolVal = (short)(b ? -1 : 0) } : PropVariant.FromUInt(uint.Parse(val));
    Console.WriteLine($"SetPropertyValue -> hr=0x{pc.SetPropertyValue(d.Id, fx, ref key, ref pv):x8}");
    pc.GetPropertyValue(d.Id, fx, ref key, out cur);
    Console.WriteLine($"after: {FormatPv(cur)}");
}

// ---------------------------------------------------------------------------

static T Activate<T>(IMMDevice d)
{
    var iid = typeof(T).GUID;
    d.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var o);
    return (T)o;
}

static string? ReadString(IPropertyStore ps, PropertyKey key)
{
    if (ps.GetValue(ref key, out var v) != 0) return null;
    var s = v.vt == 31 ? Marshal.PtrToStringUni(v.ptr) : null;
    Native.PropVariantClear(ref v);
    return s;
}

static string FormatPv(PropVariant v) => v.vt switch
{
    0 => "(empty)",
    2 => $"I2 {(short)v.i4}",
    3 or 22 => $"I4 {v.i4}",
    4 => $"R4 {v.r4}",
    11 => $"BOOL {v.boolVal != 0}",
    17 => $"UI1 {v.i4 & 0xff}",
    18 => $"UI2 {v.i4 & 0xffff}",
    19 or 23 => $"UI4 {(uint)v.i4}",
    20 or 21 => $"I8 {v.i8}",
    31 => $"\"{Marshal.PtrToStringUni(v.ptr)}\"",
    65 => $"BLOB[{v.cb}] {(v.cb <= 64 ? Convert.ToHexString(Bytes(v.blob, (int)v.cb)) : Convert.ToHexString(Bytes(v.blob, 64)) + "…")}",
    72 => $"CLSID {Marshal.PtrToStructure<Guid>(v.ptr)}",
    0x101F => "[" + string.Join(", ", Enumerable.Range(0, (int)v.cb).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(v.blob, i * IntPtr.Size)))) + "]",
    _ => $"vt=0x{v.vt:x}",
};

static byte[] Bytes(IntPtr p, int n) { var b = new byte[n]; Marshal.Copy(p, b, 0, n); return b; }

// ---------------------------------------------------------------------------

record Dev(string Id, EDataFlow Flow, DeviceState State, IMMDevice Device, string Name, string InterfaceName);

static class Keys
{
    public static PropertyKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static PropertyKey InterfaceFriendlyName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
    public static PropertyKey DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{a45c254e-df1c-4efd-8020-67d146a850e0},2"] = "Device_DeviceDesc",
        ["{a45c254e-df1c-4efd-8020-67d146a850e0},14"] = "Device_FriendlyName",
        ["{a45c254e-df1c-4efd-8020-67d146a850e0},24"] = "Device_EnumeratorName?",
        ["{a45c254e-df1c-4efd-8020-67d146a850e0},26"] = "Device_Icon?",
        ["{026e516e-b814-414b-83cd-856d6fef4822},2"] = "DeviceInterface_FriendlyName",
        ["{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2"] = "Device_ContainerId",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0"] = "AudioEndpoint_FormFactor",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},1"] = "AudioEndpoint_CplPageProvider",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},2"] = "AudioEndpoint_Association",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},3"] = "AudioEndpoint_PhysicalSpeakers",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},4"] = "AudioEndpoint_GUID",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5"] = "AudioEndpoint_Disable_SysFx",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},6"] = "AudioEndpoint_FullRangeSpeakers",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},7"] = "AudioEndpoint_EventDriven",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},8"] = "AudioEndpoint_JackSubType",
        ["{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},9"] = "AudioEndpoint_Default_VolumeInDb",
        ["{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0"] = "AudioEngine_DeviceFormat",
        ["{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},0"] = "AudioEngine_OEMFormat?",
        ["{b3f8fa53-0004-438e-9003-51a46e139bfc},2"] = "DeviceInterface path?",
        ["{b3f8fa53-0004-438e-9003-51a46e139bfc},3"] = "Exclusive allow?",
        ["{b3f8fa53-0004-438e-9003-51a46e139bfc},4"] = "Exclusive priority?",
        ["{b3f8fa53-0004-438e-9003-51a46e139bfc},6"] = "Device instance?",
        ["{24dbb0fc-9311-4b3d-9cf0-18ff155639d4},0"] = "Listen target?",
        ["{24dbb0fc-9311-4b3d-9cf0-18ff155639d4},1"] = "Listen enabled?",
    };

    public static string NameOf(PropertyKey k) => Known.TryGetValue(k.ToString(), out var n) ? $"{n}" : k.ToString();

    static readonly Dictionary<Guid, string> Nodes = new()
    {
        [new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1")] = "VOLUME",
        [new("02B223C0-C557-11D0-8A2B-00A0C9255AC1")] = "MUTE",
        [new("E88C9BA0-C557-11D0-8A2B-00A0C9255AC1")] = "AGC",
        [new("AD809C00-7B88-11D0-A5D6-28DB04C10000")] = "MUX",
        [new("CFA9D240-C557-11D0-8A2B-00A0C9255AC1")] = "SUM",
        [new("DFF21BE5-F70F-11D0-B917-00A0C9223196")] = "MICROPHONE",
        [new("DFF21CE1-F70F-11D0-B917-00A0C9223196")] = "SPEAKER",
        [new("DFF21FE1-F70F-11D0-B917-00A0C9223196")] = "LINE_CONNECTOR",
        [new("DFF21FE3-F70F-11D0-B917-00A0C9223196")] = "SPDIF",
        [new("DFF21CE5-F70F-11D0-B917-00A0C9223196")] = "HEADPHONES",
        [new("DFF220F3-F70F-11D0-B917-00A0C9223196")] = "DIGITAL_AUDIO_INTERFACE",
    };

    public static string NodeType(Guid g) => Nodes.TryGetValue(g, out var n) ? n : (g == Guid.Empty ? "" : g.ToString());
}

[ComVisible(true)]
class NotifyClient : IMMNotificationClient
{
    readonly Stopwatch _clock;
    readonly Dictionary<(EDataFlow, ERole), string?> _desired;
    readonly bool _lock;
    readonly Func<string?, string> _name;
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _worker;

    public NotifyClient(Stopwatch clock, Dictionary<(EDataFlow, ERole), string?> desired, bool doLock, Func<string?, string> name)
    {
        _clock = clock; _desired = desired; _lock = doLock; _name = name;
        // Never call back into the audio service from inside a notification; hand off to a worker.
        _worker = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) try { a(); } catch (Exception e) { Log($"worker: {e.Message}"); } }) { IsBackground = true };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public void Stop() { _work.CompleteAdding(); _worker.Join(1000); }

    void Log(string s) => Console.WriteLine($"{_clock.Elapsed.TotalMilliseconds,8:0.0} ms  {s}");

    public void OnDeviceStateChanged(string id, DeviceState state) => _work.Add(() =>
    {
        Log($"state   {_name(id)} -> {state}");
        if (_lock && state == DeviceState.Active)
            foreach (var ((flow, role), want) in _desired)
                if (want == id) Reapply(flow, role, want);
    });
    public void OnDeviceAdded(string id) => _work.Add(() => Log($"added   {_name(id)}"));
    public void OnDeviceRemoved(string id) => _work.Add(() => Log($"removed {id}"));
    public void OnPropertyValueChanged(string id, PropertyKey key) => _work.Add(() => Log($"prop    {_name(id)} {Keys.NameOf(key)}"));

    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
    {
        var t = _clock.Elapsed.TotalMilliseconds;
        _work.Add(() =>
        {
            Log($"DEFAULT {flow}/{role} -> {_name(id)}  (event at {t:0.0} ms)");
            if (_lock && _desired.TryGetValue((flow, role), out var want) && want != null && want != id)
                Reapply(flow, role, want);
        });
    }

    void Reapply(EDataFlow flow, ERole role, string want)
    {
        var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        if (e.GetDevice(want, out var d) != 0) return;
        d.GetState(out var st);
        if (st != DeviceState.Active) { Log($"  desired {_name(want)} not active ({st}); leaving it"); return; }
        var pc = (IPolicyConfig)new PolicyConfigClientCo();
        var hr = pc.SetDefaultEndpoint(want, role);
        Log($"  LOCK reverted {flow}/{role} -> {_name(want)} hr=0x{hr:x8}");
    }
}

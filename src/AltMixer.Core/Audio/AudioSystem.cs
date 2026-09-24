using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AltMixer.Core.Interop;
using AltMixer.Core.Model;
using Microsoft.Win32;

namespace AltMixer.Core.Audio;

/// <summary>
/// Reads the whole Windows audio configuration into a <see cref="Snapshot"/> and applies individual settings.
/// Not thread-safe: use from the engine's single MTA thread.
/// </summary>
public sealed partial class AudioSystem : IDisposable
{
    const string DuckingKey = @"Software\Microsoft\Multimedia\Audio";
    const string DefaultRoute = "default";
    const double AppFloorDb = -60;

    readonly IMMDeviceEnumerator _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
    readonly IPolicyConfig _policy = (IPolicyConfig)new PolicyConfigClientCo();
    readonly Dictionary<string, DeviceCache> _cache = new();
    readonly HashSet<string> _claimedHwParts = new();
    readonly Action _onVolumeChanged;

    sealed class DeviceCache
    {
        public required IMMDevice Device;
        public required string Fingerprint;
        public IAudioEndpointVolume? Volume;
        public VolumeCallback? Callback;
        public IAudioSessionManager2? Sessions;
        public List<HwControl>? Hw;
        public List<AudioFormat>? Formats;
    }

    [ComVisible(true)]
    sealed class VolumeCallback(Action onChange) : IAudioEndpointVolumeCallback
    {
        public void OnNotify(IntPtr notify) => onChange();
    }

    public AudioSystem(Action onVolumeChanged)
    {
        _onVolumeChanged = onVolumeChanged;
        AppRouting.Init();
    }

    public IMMDeviceEnumerator Enumerator => _enum;

    /// <summary>Forget cached per-device COM objects (after devices arrive, leave or change state).</summary>
    public void Invalidate()
    {
        foreach (var c in _cache.Values)
            if (c.Volume != null && c.Callback != null)
                try { c.Volume.UnregisterControlChangeNotify(c.Callback); } catch { }
        _cache.Clear();
        _claimedHwParts.Clear();
    }

    public void Dispose() => Invalidate();

    internal static T Activate<T>(IMMDevice d)
    {
        var iid = typeof(T).GUID;
        d.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var o);
        return (T)o;
    }

    // ------------------------------------------------------------------ read

    public Snapshot Read()
    {
        var devices = new List<DeviceInfo>();
        var settings = new List<Setting>();
        var defaults = new Dictionary<(Flow, bool), string?>
        {
            [(Flow.Render, false)] = DefaultId(EDataFlow.Render, ERole.Console),
            [(Flow.Render, true)] = DefaultId(EDataFlow.Render, ERole.Communications),
            [(Flow.Capture, false)] = DefaultId(EDataFlow.Capture, ERole.Console),
            [(Flow.Capture, true)] = DefaultId(EDataFlow.Capture, ERole.Communications),
        };

        _enum.EnumAudioEndpoints(EDataFlow.All, DeviceState.Active | DeviceState.Disabled, out var coll);
        coll.GetCount(out var n);
        var raw = new List<(IMMDevice dev, string id, Flow flow, DeviceState state)>();
        for (uint i = 0; i < n; i++)
        {
            coll.Item(i, out var d);
            d.GetId(out var id);
            d.GetState(out var st);
            ((IMMEndpoint)d).GetDataFlow(out var f);
            raw.Add((d, id, f == EDataFlow.Render ? Flow.Render : Flow.Capture, st));
        }
        // Capture first so shared hardware controls (e.g. CM108 monitor path) are shown under the microphone.
        foreach (var (dev, id, flow, state) in raw.OrderByDescending(r => r.flow))
        {
            try
            {
                var cache = GetCache(id, dev, flow);
                dev.OpenPropertyStore(0, out var ps);
                var name = Props.ReadString(ps, PropKeys.FriendlyName) ?? id;
                var info = new DeviceInfo
                {
                    Id = id, Flow = flow, Name = name, Fingerprint = cache.Fingerprint + "|" + name,
                    Status = state == DeviceState.Active ? DeviceStatus.Active : DeviceStatus.Disabled,
                    IsDefault = defaults[(flow, false)] == id,
                    IsComms = defaults[(flow, true)] == id,
                };
                devices.Add(info);
                settings.Add(new Setting { Id = Ids.Dev(id, "enabled"), Owner = id, Label = "Enabled", Kind = SettingKind.Toggle, Current = SettingValue.Of(state == DeviceState.Active) });
                if (state == DeviceState.Active) ReadDevice(info, cache, ps, settings);
            }
            catch (Exception e) { Trace.WriteLine($"read {id}: {e.Message}"); }
        }

        foreach (var flow in new[] { Flow.Render, Flow.Capture })
        {
            var choices = devices.Where(d => d.Flow == flow && d.Status == DeviceStatus.Active).OrderBy(d => d.Name).Select(d => new Choice(d.Id, d.Name)).ToList();
            foreach (var comms in new[] { false, true })
                settings.Add(new Setting
                {
                    Id = Ids.Default(flow, comms), Owner = Ids.Defaults, Kind = SettingKind.Choice, Choices = choices,
                    Label = $"{(comms ? "Communications " : "Default ")}{(flow == Flow.Render ? "output" : "input")}",
                    Current = SettingValue.Of(defaults[(flow, comms)] ?? ""),
                });
        }

        var apps = ReadApps(devices, settings);
        settings.Add(ReadDucking());
        return new Snapshot(devices, apps, settings);
    }

    string? DefaultId(EDataFlow flow, ERole role)
    {
        if (_enum.GetDefaultAudioEndpoint(flow, role, out var d) != 0) return null;
        d.GetId(out var id);
        return id;
    }

    DeviceCache GetCache(string id, IMMDevice dev, Flow flow)
    {
        if (_cache.TryGetValue(id, out var c)) return c;
        var filter = Topology.FilterId(dev);
        var usb = filter == null ? null : UsbId().Match(filter) is { Success: true } m ? m.Value.ToLowerInvariant() : null;
        c = new DeviceCache { Device = dev, Fingerprint = $"{flow}|{usb ?? "-"}" };
        _cache[id] = c;
        return c;
    }

    [GeneratedRegex(@"vid_[0-9a-f]{4}&pid_[0-9a-f]{4}(&mi_[0-9a-f]{2})?", RegexOptions.IgnoreCase)]
    private static partial Regex UsbId();

    void ReadDevice(DeviceInfo d, DeviceCache c, IPropertyStore ps, List<Setting> settings)
    {
        var id = d.Id;
        if (c.Volume == null)
        {
            c.Volume = Activate<IAudioEndpointVolume>(c.Device);
            c.Callback = new VolumeCallback(_onVolumeChanged);
            c.Volume.RegisterControlChangeNotify(c.Callback);
        }
        var vol = c.Volume;
        vol.GetVolumeRange(out var min, out var max, out var inc);
        vol.GetMasterVolumeLevel(out var db);
        vol.GetMute(out var mute);
        vol.GetChannelCount(out var channels);
        vol.QueryHardwareSupport(out var hw);
        var level = new Setting
        {
            Id = Ids.Dev(id, "level"), Owner = id, Label = "Level", Kind = SettingKind.Level, Primary = true,
            Current = SettingValue.Of(Math.Round(db, 2)), Min = min, Max = max, Step = inc,
        };
        settings.Add(level with
        {
            Note = level.Uncalibrated ? $"Driver reports {min:0.#} to {max:0.#} dB, which never reaches 0 dB, so its labels aren't real gain. The slider is disabled; restore or keep the level as it is."
                 : max > 0.01 ? $"Driver allows up to +{max:0.#} dB; capped at 0 dB." : null,
        });
        settings.Add(new Setting { Id = Ids.Dev(id, "mute"), Owner = id, Label = "Mute", Kind = SettingKind.Toggle, Primary = true, Current = SettingValue.Of(mute) });

        if (channels > 1)
        {
            var offsets = Enumerable.Range(0, (int)channels).Select(ch => { vol.GetChannelVolumeLevel((uint)ch, out var v); return Math.Round(v - db, 1); }).ToList();
            var text = string.Join(",", offsets.Select(o => o.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            settings.Add(new Setting
            {
                Id = Ids.Dev(id, "balance"), Owner = id, Label = "Balance", Kind = SettingKind.Opaque, Current = SettingValue.Of(text),
                Display = offsets.All(o => Math.Abs(o) < 0.05) ? "Centred" : string.Join(" / ", offsets.Select((o, i) => $"{(channels == 2 ? (i == 0 ? "L" : "R") : $"ch{i + 1}")} {o:+0.0;-0.0;0.0} dB")),
            });
        }

        var fx = Props.ReadUInt(_policy, id, true, PropKeys.DisableSysFx);
        settings.Add(new Setting { Id = Ids.Dev(id, "enhancements"), Owner = id, Label = "Audio enhancements", Kind = SettingKind.Toggle, Current = SettingValue.Of(fx is null or 0) });
        settings.Add(new Setting { Id = Ids.Dev(id, "exclusive.allow"), Owner = id, Label = "Allow exclusive control", Kind = SettingKind.Toggle, Current = SettingValue.Of((Props.ReadUInt(_policy, id, false, PropKeys.ExclusiveAllow) ?? 1) != 0) });
        settings.Add(new Setting { Id = Ids.Dev(id, "exclusive.priority"), Owner = id, Label = "Exclusive apps get priority", Kind = SettingKind.Toggle, Current = SettingValue.Of((Props.ReadUInt(_policy, id, false, PropKeys.ExclusivePriority) ?? 1) != 0) });
        if (d.Flow == Flow.Capture)
            settings.Add(new Setting { Id = Ids.Dev(id, "listen"), Owner = id, Label = "Listen to this device", Kind = SettingKind.Toggle, Current = SettingValue.Of(Props.ReadBool(_policy, id, PropKeys.Listen) ?? false) });

        if (_policy.GetDeviceFormat(id, 0, out var fp) == 0 && fp != IntPtr.Zero)
        {
            var fmt = AudioFormat.Read(fp);
            Marshal.FreeCoTaskMem(fp);
            if (c.Formats == null)
                try { c.Formats = AudioFormat.Supported(Activate<IAudioClient>(c.Device), fmt); } catch { c.Formats = [fmt]; }
            var choices = c.Formats.Select(f => new Choice(f.Id, f.Label)).ToList();
            if (choices.All(ch => ch.Id != fmt.Id)) choices.Insert(0, new Choice(fmt.Id, fmt.Label));
            settings.Add(new Setting { Id = Ids.Dev(id, "format"), Owner = id, Label = "Format", Kind = SettingKind.Choice, Current = SettingValue.Of(fmt.Id), Choices = choices });
        }

        if (d.Flow == Flow.Render && Props.ReadBlob(_policy, id, PropKeys.Spatial) is { } spatial)
            settings.Add(new Setting
            {
                Id = Ids.Dev(id, "spatial"), Owner = id, Label = "Spatial sound", Kind = SettingKind.Opaque,
                Current = SettingValue.Of(Convert.ToBase64String(spatial)), Display = SpatialName(ps, spatial),
            });

        c.Hw ??= Topology.Find(id, c.Device, d.Flow, min, max, (hw & 2) != 0, _claimedHwParts);
        foreach (var h in c.Hw)
        {
            try
            {
                SettingValue cur = h.Control switch
                {
                    IAudioVolumeLevel l => Level(l),
                    IAudioAutoGainControl a => Flag(a),
                    IAudioMute m => Flag(m),
                    IAudioLoudness l => Flag(l),
                    _ => new(),
                };
                var s = new Setting { Id = h.SettingId, Owner = id, Label = h.Label, Kind = h.Kind, Current = cur, Min = h.Min, Max = h.Max, Step = h.Step };
                settings.Add(s.Kind == SettingKind.Level && s.Max > 0.01 && !s.Uncalibrated ? s with { Note = $"Driver allows up to +{s.Max:0.#} dB; capped at 0 dB." } : s);
            }
            catch { }
        }

        static SettingValue Level(IAudioVolumeLevel l) { l.GetLevel(0, out var v); return SettingValue.Of(Math.Round(v, 2)); }
        static SettingValue Flag(object o)
        {
            bool v;
            switch (o)
            {
                case IAudioAutoGainControl a: a.GetEnabled(out v); break;
                case IAudioMute m: m.GetMute(out v); break;
                case IAudioLoudness l: l.GetEnabled(out v); break;
                default: v = false; break;
            }
            return SettingValue.Of(v);
        }
    }

    /// <summary>The spatial sound blob embeds the chosen provider's CLSID; the provider list carries their names.</summary>
    static string SpatialName(IPropertyStore ps, byte[] selected)
    {
        foreach (var (_, blob) in Props.ReadBlobs(ps, PropKeys.SpatialProvidersFmtid))
        {
            var name = Encoding.Unicode.GetString(blob, 0, Math.Min(blob.Length, 256)).Split('\0')[0];
            for (var i = 256; i + 16 <= blob.Length; i += 4)
            {
                var window = blob.AsSpan(i, 16);
                if (window.IndexOfAnyExcept((byte)0) < 0) continue;
                if (selected.AsSpan().IndexOf(window) >= 0) return name;
            }
        }
        return "Off";
    }

    /// <remarks>
    /// Only output sessions get level/mute: on a capture device a session's volume is an alias of the device level
    /// (setting a mic-meter session to 0 dB drove a CM108 mic to +23 dB), so it's managed as the device level instead.
    /// </remarks>
    List<AppInfo> ReadApps(List<DeviceInfo> devices, List<Setting> settings)
    {
        var levels = new Dictionary<(string key, string device), (float vol, bool mute)>();
        var apps = new Dictionary<string, (string name, Dictionary<Flow, uint> pids)>();
        foreach (var d in devices.Where(d => d.Status == DeviceStatus.Active))
        {
            foreach (var (s, pid, key, name) in Sessions(d.Id))
            {
                if (!apps.TryGetValue(key, out var app)) apps[key] = app = (name, new());
                app.pids.TryAdd(d.Flow, pid);
                if (d.Flow != Flow.Render || levels.ContainsKey((key, d.Id))) continue;
                var v = (ISimpleAudioVolume)s;
                v.GetMasterVolume(out var vol);
                v.GetMute(out var mute);
                levels[(key, d.Id)] = (vol, mute);
            }
        }

        var result = new List<AppInfo>();
        foreach (var (key, (name, pids)) in apps)
        {
            var owner = Ids.AppOwner(key);
            var outputs = levels.Where(l => l.Key.key == key).ToList();
            if (outputs.Count == 0 && (key == SystemSoundsKey || !AppRouting.Available)) continue;
            result.Add(new AppInfo(key, name, pids.Keys.OrderBy(f => f).ToList()));
            var single = outputs.Count == 1;
            foreach (var ((_, deviceId), (vol, mute)) in outputs)
            {
                var dev = devices.First(d => d.Id == deviceId).Name;
                var db = vol <= 0 ? AppFloorDb : Math.Max(AppFloorDb, Math.Round(20 * Math.Log10(vol), 1));
                settings.Add(new Setting { Id = Ids.AppOnDevice(key, deviceId, "level"), Owner = owner, Label = single ? "Level" : $"Level on {dev}", Kind = SettingKind.Level, Primary = single, Current = SettingValue.Of(db), Min = AppFloorDb, Max = 0, Step = 0.5 });
                settings.Add(new Setting { Id = Ids.AppOnDevice(key, deviceId, "mute"), Owner = owner, Label = single ? "Mute" : $"Mute on {dev}", Kind = SettingKind.Toggle, Primary = single, Current = SettingValue.Of(mute) });
            }
            if (key == SystemSoundsKey || !AppRouting.Available) continue;
            foreach (var (flow, pid) in pids.OrderBy(p => p.Key))
            {
                AppRouting.Get(pid, flow == Flow.Render ? EDataFlow.Render : EDataFlow.Capture, ERole.Multimedia, out var swd);
                var choices = new List<Choice> { new(DefaultRoute, "Windows default") };
                choices.AddRange(devices.Where(d => d.Flow == flow && d.Status == DeviceStatus.Active).OrderBy(d => d.Name).Select(d => new Choice(d.Id, d.Name)));
                settings.Add(new Setting
                {
                    Id = Ids.AppRoute(key, flow), Owner = owner, Label = flow == Flow.Render ? "Output device" : "Input device",
                    Kind = SettingKind.Choice, Choices = choices, Current = SettingValue.Of(AppRouting.FromSwdId(swd) ?? DefaultRoute),
                });
            }
        }
        return result.OrderBy(a => a.Key == SystemSoundsKey ? "" : a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public const string SystemSoundsKey = "system-sounds";

    IEnumerable<(IAudioSessionControl2 s, uint pid, string key, string name)> Sessions(string deviceId)
    {
        if (!_cache.TryGetValue(deviceId, out var c)) yield break;
        IAudioSessionEnumerator e;
        try
        {
            c.Sessions ??= Activate<IAudioSessionManager2>(c.Device);
            c.Sessions.GetSessionEnumerator(out e);
        }
        catch { yield break; }
        e.GetCount(out var n);
        for (var i = 0; i < n; i++)
        {
            e.GetSession(i, out var s);
            s.GetState(out var state);
            if (state == 2) continue; // expired
            s.GetProcessId(out var pid);
            if (s.IsSystemSoundsSession() == 0)
            {
                if (c.Fingerprint.StartsWith(nameof(Flow.Render))) yield return (s, pid, SystemSoundsKey, "System sounds");
                continue;
            }
            var path = ProcessPath(pid);
            if (path == null) continue;
            yield return (s, pid, path.ToLowerInvariant(), AppName(path));
        }
    }

    static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    static string AppName(string path)
    {
        if (AppNames.TryGetValue(path, out var n)) return n;
        try { n = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { n = ""; }
        if (string.IsNullOrWhiteSpace(n)) n = Path.GetFileNameWithoutExtension(path);
        return AppNames[path] = n.Trim();
    }

    static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Choice[] DuckingChoices =
    [
        new("0", "Mute all other sounds"),
        new("1", "Reduce other sounds by 80%"),
        new("2", "Reduce other sounds by 50%"),
        new("3", "Do nothing"),
    ];

    static Setting ReadDucking()
    {
        using var k = Registry.CurrentUser.OpenSubKey(DuckingKey);
        var v = k?.GetValue("UserDuckingPreference") is int i ? i : 1;
        return new Setting
        {
            Id = Ids.Ducking, Owner = Ids.System, Label = "When Windows detects communications activity", Kind = SettingKind.Choice,
            Choices = DuckingChoices, Current = SettingValue.Of(v.ToString()),
        };
    }

    // ------------------------------------------------------------------ write

    public void Apply(Setting s, SettingValue v)
    {
        var ctx = Guid.Empty;
        if (s.Id.StartsWith("default/", StringComparison.Ordinal))
        {
            var roles = s.Id.EndsWith("/comms") ? new[] { ERole.Communications } : new[] { ERole.Console, ERole.Multimedia };
            foreach (var r in roles) Props.Check(_policy.SetDefaultEndpoint(v.Text!, r));
            return;
        }
        if (s.Id == Ids.Ducking)
        {
            using var k = Registry.CurrentUser.CreateSubKey(DuckingKey);
            k.SetValue("UserDuckingPreference", int.Parse(v.Text!), RegistryValueKind.DWord);
            return;
        }
        if (s.Owner.StartsWith("app:", StringComparison.Ordinal))
        {
            ApplyApp(s, v);
            return;
        }

        var id = s.Owner;
        var what = s.Id[(Ids.Dev(id, "").Length)..];
        if (what == "enabled") { Props.Check(_policy.SetEndpointVisibility(id, v.Flag == true ? 1 : 0)); return; }
        if (!_cache.TryGetValue(id, out var c)) throw new InvalidOperationException("Device not connected");
        switch (what)
        {
            case "level":
                c.Volume!.SetMasterVolumeLevel((float)ClampLevel(s, v.Number!.Value), ref ctx);
                break;
            case "mute":
                c.Volume!.SetMute(v.Flag == true, ref ctx);
                break;
            case "balance":
                c.Volume!.GetMasterVolumeLevel(out var master);
                var offsets = v.Text!.Split(',').Select(o => double.Parse(o, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                for (var ch = 0; ch < offsets.Length; ch++)
                    c.Volume.SetChannelVolumeLevel((uint)ch, (float)Math.Clamp(master + offsets[ch], s.Min, Math.Max(s.Min, 0)), ref ctx);
                break;
            case "enhancements":
                Props.WriteUInt(_policy, id, true, PropKeys.DisableSysFx, v.Flag == true ? 0u : 1u);
                break;
            case "exclusive.allow":
                Props.WriteUInt(_policy, id, false, PropKeys.ExclusiveAllow, v.Flag == true ? 1u : 0u);
                c.Formats = null;
                break;
            case "exclusive.priority":
                Props.WriteUInt(_policy, id, false, PropKeys.ExclusivePriority, v.Flag == true ? 1u : 0u);
                break;
            case "listen":
                Props.WriteBool(_policy, id, PropKeys.Listen, v.Flag == true);
                break;
            case "spatial":
                Props.WriteBlob(_policy, id, PropKeys.Spatial, Convert.FromBase64String(v.Text!));
                break;
            case "format":
                ApplyFormat(id, v.Text!);
                break;
            default:
                var h = c.Hw?.FirstOrDefault(x => x.SettingId == s.Id) ?? throw new InvalidOperationException($"Unknown setting {s.Id}");
                switch (h.Control)
                {
                    case IAudioVolumeLevel l: l.SetLevelUniform((float)ClampLevel(s, v.Number!.Value), ref ctx); break;
                    case IAudioAutoGainControl a: a.SetEnabled(v.Flag == true, ref ctx); break;
                    case IAudioMute m: m.SetMute(v.Flag == true, ref ctx); break;
                    case IAudioLoudness l: l.SetEnabled(v.Flag == true, ref ctx); break;
                }
                break;
        }
    }

    /// <summary>Never above the cap (0 dB, or the bottom of a range that doesn't reach 0 dB).</summary>
    static double ClampLevel(Setting s, double v) => Math.Clamp(v, s.Min, s.Cap);

    void ApplyFormat(string id, string formatId)
    {
        Props.Check(_policy.GetDeviceFormat(id, 0, out var cur));
        var like = AudioFormat.Read(cur);
        Marshal.FreeCoTaskMem(cur);
        var f = AudioFormat.Parse(formatId, like) ?? throw new ArgumentException(formatId);
        var endpoint = f.Alloc();
        var mix = (f with { Bits = 32, ValidBits = 32, IsFloat = true }).Alloc();
        try { Props.Check(_policy.SetDeviceFormat(id, endpoint, mix)); }
        finally { Marshal.FreeCoTaskMem(endpoint); Marshal.FreeCoTaskMem(mix); }
    }

    void ApplyApp(Setting s, SettingValue v)
    {
        var parts = s.Id.Split('/');
        var what = parts[^1];
        var scope = parts[^2]; // device id for level/mute, flow name for route
        var key = s.Owner["app:".Length..];
        var ctx = Guid.Empty;
        if (what is "level" or "mute")
        {
            foreach (var (session, _, k, _) in Sessions(scope))
            {
                if (k != key) continue;
                var vol = (ISimpleAudioVolume)session;
                if (what == "level") vol.SetMasterVolume(v.Number!.Value <= AppFloorDb ? 0f : (float)Math.Pow(10, Math.Min(0, v.Number.Value) / 20), ref ctx);
                else vol.SetMute(v.Flag == true, ref ctx);
            }
            return;
        }
        var flow = scope == Ids.Name(Flow.Render) ? Flow.Render : Flow.Capture;
        var pids = _cache.Where(c => c.Value.Fingerprint.StartsWith(flow.ToString()))
            .SelectMany(c => Sessions(c.Key)).Where(x => x.key == key).Select(x => x.pid).ToHashSet();
        var target = v.Text == DefaultRoute ? null : v.Text;
        var ef = flow == Flow.Render ? EDataFlow.Render : EDataFlow.Capture;
        foreach (var pid in pids)
        {
            Props.Check(AppRouting.Set(pid, ef, ERole.Console, target));
            Props.Check(AppRouting.Set(pid, ef, ERole.Multimedia, target));
        }
    }
}


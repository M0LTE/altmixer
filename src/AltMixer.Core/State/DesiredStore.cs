using System.Text.Json;
using System.Text.Json.Serialization;
using AltMixer.Core.Model;

namespace AltMixer.Core.State;

public sealed class DesiredEntry
{
    public SettingValue Value { get; set; } = new();
    public bool Locked { get; set; }
}

/// <summary>A named set of desired values. Only one exists today; the shape allows more later.</summary>
public sealed class Profile
{
    public Dictionary<string, DesiredEntry> Settings { get; set; } = new();
}

public sealed class KnownDevice
{
    public string Name { get; set; } = "";
    public Flow Flow { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>True once the user has answered (or we've auto-resolved) the adopt question for this device.</summary>
    public bool AdoptResolved { get; set; }
}

public sealed class UiState
{
    /// <summary>Per-device collapsed state; absent = default (collapsed only when disabled).</summary>
    public Dictionary<string, bool> Collapsed { get; set; } = new();

    /// <summary>Device ids in the user's order (see <see cref="DeviceOrdering"/>).</summary>
    public List<string> DeviceOrder { get; set; } = new();
}

public sealed class StoreData
{
    public int Version { get; set; } = DesiredStore.CurrentVersion;
    public string ActiveProfile { get; set; } = "Default";
    public Dictionary<string, Profile> Profiles { get; set; } = new() { ["Default"] = new() };
    public Dictionary<string, KnownDevice> Devices { get; set; } = new();
    public UiState Ui { get; set; } = new();
}

/// <summary>Desired state persisted as JSON in %APPDATA%\AltMixer\state.json.</summary>
public sealed class DesiredStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string? _path;
    readonly bool _readOnly;
    public StoreData Data { get; private set; }
    public bool Dirty { get; private set; }

    /// <param name="readOnly">Load from <paramref name="path"/> but never write it (diagnostics).</param>
    public DesiredStore(string? path, bool readOnly = false)
    {
        _path = path;
        _readOnly = readOnly;
        Data = new StoreData();
        if (path != null && File.Exists(path))
        {
            try { Data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), Json) ?? new(); }
            catch (JsonException)
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        if (!Data.Profiles.ContainsKey(Data.ActiveProfile)) Data.Profiles[Data.ActiveProfile] = new();
        Migrate();
    }

    public const int CurrentVersion = 2;

    void Migrate()
    {
        if (Data.Version < 2)
        {
            // v1 kept one app level per flow; v2 keeps it per output device and doesn't track capture sessions.
            foreach (var p in Data.Profiles.Values)
                foreach (var k in p.Settings.Keys.Where(k => k.StartsWith("app/") && (k.EndsWith("/render/level") || k.EndsWith("/render/mute") || k.EndsWith("/capture/level") || k.EndsWith("/capture/mute"))).ToList())
                    p.Settings.Remove(k);
        }
        if (Data.Version != CurrentVersion) { Data.Version = CurrentVersion; Dirty = true; }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltMixer", "state.json");

    Dictionary<string, DesiredEntry> Settings => Data.Profiles[Data.ActiveProfile].Settings;

    public DesiredEntry? Get(string id) => Settings.GetValueOrDefault(id);

    public void SetValue(string id, SettingValue value)
    {
        if (Settings.TryGetValue(id, out var e)) { if (e.Value == value) return; e.Value = value; }
        else Settings[id] = new DesiredEntry { Value = value };
        Dirty = true;
    }

    public void SetLocked(string id, bool locked)
    {
        if (!Settings.TryGetValue(id, out var e) || e.Locked == locked) return;
        e.Locked = locked;
        Dirty = true;
    }

    public bool? GetCollapsed(string deviceId) => Data.Ui.Collapsed.TryGetValue(deviceId, out var c) ? c : null;

    public void SetCollapsed(string deviceId, bool collapsed)
    {
        Data.Ui.Collapsed[deviceId] = collapsed;
        Dirty = true;
    }

    public void SetDeviceOrder(List<string> order)
    {
        Data.Ui.DeviceOrder = order;
        Dirty = true;
    }

    public void MarkDirty() => Dirty = true;

    /// <summary>
    /// Moves every desired value for <paramref name="oldId"/> onto <paramref name="newId"/>: its own settings, and any
    /// setting (defaults, app routes) whose value points at the old device.
    /// </summary>
    public void Adopt(string oldId, string newId)
    {
        foreach (var profile in Data.Profiles.Values)
        {
            var s = profile.Settings;
            var oldPrefix = $"dev/{oldId}/";
            foreach (var key in s.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList())
            {
                s[$"dev/{newId}/{key[oldPrefix.Length..]}"] = s[key];
                s.Remove(key);
            }
            foreach (var e in s.Values)
                if (e.Value.Text == oldId) e.Value = e.Value with { Text = newId };
        }
        if (Data.Ui.Collapsed.Remove(oldId, out var c)) Data.Ui.Collapsed[newId] = c;
        var at = Data.Ui.DeviceOrder.IndexOf(oldId);
        if (at >= 0)
        {
            Data.Ui.DeviceOrder.Remove(newId);
            Data.Ui.DeviceOrder[Data.Ui.DeviceOrder.IndexOf(oldId)] = newId;
        }
        Data.Devices.Remove(oldId);
        Dirty = true;
    }

    public void Save()
    {
        if (!Dirty || _path == null || _readOnly) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
        File.Move(tmp, _path, overwrite: true);
        Dirty = false;
    }
}

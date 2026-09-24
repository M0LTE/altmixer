using System.Collections.ObjectModel;
using AltMixer.Core;
using AltMixer.Core.Model;
using AltMixer.Core.State;

namespace AltMixer.ViewModels;

/// <summary>A collapsible card: a device or an application. Header shows level + mute; body shows the rest.</summary>
public class GroupViewModel(Engine engine, string id) : Observable
{
    protected readonly Engine Engine = engine;

    public string Id { get; } = id;
    public ObservableCollection<SettingViewModel> Body { get; } = new();

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _subtitle = "";
    public string Subtitle { get => _subtitle; set { if (Set(ref _subtitle, value)) Raise(nameof(HasSubtitle)); } }
    public bool HasSubtitle => _subtitle.Length > 0;

    SettingViewModel? _level;
    public SettingViewModel? Level { get => _level; private set { if (Set(ref _level, value)) Raise(nameof(HasLevel)); } }
    public bool HasLevel => _level != null;

    SettingViewModel? _mute;
    public SettingViewModel? Mute { get => _mute; private set => Set(ref _mute, value); }

    int _bodyDrift;
    /// <summary>Changed settings hidden inside the body; shown on the header while collapsed.</summary>
    public int BodyDriftCount { get => _bodyDrift; private set { if (Set(ref _bodyDrift, value)) Raise(nameof(BodyDriftText)); } }
    public string BodyDriftText => _bodyDrift == 1 ? "1 change" : $"{_bodyDrift} changes";

    public void UpdateSettings(IReadOnlyList<Setting> settings, EngineState state)
    {
        Level = Sync(Level, settings.FirstOrDefault(s => s.Primary && s.Kind == SettingKind.Level), state);
        Mute = Sync(Mute, settings.FirstOrDefault(s => s.Primary && s.Kind == SettingKind.Toggle), state);
        var body = settings.Where(s => s.Id != Level?.Id && s.Id != Mute?.Id && !IsHeaderOnly(s)).ToList();
        CollectionSync.Sync(Body, body, s => s.Id, vm => vm.Id, s => new SettingViewModel(Engine, s), (vm, s) => vm.Update(s, state.Status.GetValueOrDefault(s.Id), state.KnownNames));
        BodyDriftCount = Body.Count(b => b.IsDrifted);
        Raise(nameof(HeaderLocked)); Raise(nameof(HeaderDrifted)); Raise(nameof(HeaderFighting)); Raise(nameof(HeaderDriftText));
    }

    IEnumerable<SettingViewModel> Header => new[] { Level, Mute }.OfType<SettingViewModel>();

    /// <summary>The header's level and mute share one lock: "lock this volume".</summary>
    public bool HeaderLocked
    {
        get => Header.Any() && Header.All(h => h.IsLocked);
        set { foreach (var h in Header) h.IsLocked = value; Raise(); }
    }
    public bool HeaderDrifted => Header.Any(h => h.IsDrifted);
    public bool HeaderFighting => Header.Any(h => h.IsFighting);
    public string HeaderDriftText => string.Join(Environment.NewLine + Environment.NewLine, Header.Where(h => h.IsDrifted).Select(h => $"{h.Label}: {h.DriftText}"));
    public Command HeaderRestoreCommand => new(() => { foreach (var h in Header.Where(h => h.IsDrifted)) h.RestoreCommand.Execute(null); });
    public Command HeaderAcceptCommand => new(() => { foreach (var h in Header.Where(h => h.IsDrifted)) h.AcceptCommand.Execute(null); });

    protected virtual bool IsHeaderOnly(Setting s) => false;

    SettingViewModel? Sync(SettingViewModel? vm, Setting? s, EngineState state)
    {
        if (s == null) return null;
        if (vm == null || vm.Id != s.Id) vm = new SettingViewModel(Engine, s);
        vm.Update(s, state.Status.GetValueOrDefault(s.Id), state.KnownNames);
        return vm;
    }
}

public sealed class DeviceViewModel : GroupViewModel
{
    public DeviceViewModel(Engine engine, string id) : base(engine, id)
    {
        MoveUpCommand = new Command(() => engine.MoveDevice(id, -1));
        MoveDownCommand = new Command(() => engine.MoveDevice(id, +1));
    }

    public Command MoveUpCommand { get; }
    public Command MoveDownCommand { get; }

    bool _dropAbove, _dropBelow;
    /// <summary>Insertion line shown while another card is dragged over this one.</summary>
    public bool DropAbove { get => _dropAbove; set => Set(ref _dropAbove, value); }
    public bool DropBelow { get => _dropBelow; set => Set(ref _dropBelow, value); }

    bool _collapsed;
    bool _updating;

    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (!Set(ref _collapsed, value)) return;
            Raise(nameof(IsExpanded));
            if (!_updating) Engine.SetCollapsed(Id, value);
        }
    }
    public bool IsExpanded => !_collapsed;

    bool _disabled;
    public bool IsDisabled { get => _disabled; private set => Set(ref _disabled, value); }

    SettingViewModel? _enabled;
    /// <summary>The Enabled toggle, shown in the header of disabled devices.</summary>
    public SettingViewModel? Enabled { get => _enabled; private set => Set(ref _enabled, value); }

    public void Update(DeviceInfo d, EngineState state)
    {
        Name = d.Name;
        IsDisabled = d.Status == DeviceStatus.Disabled;
        var roles = new List<string>();
        if (d.IsDefault) roles.Add("Default");
        if (d.IsComms) roles.Add("Communications");
        Subtitle = IsDisabled ? "Disabled" : string.Join(" · ", roles);
        _updating = true;
        IsCollapsed = state.Collapsed.GetValueOrDefault(d.Id, IsDisabled);
        _updating = false;
        var mine = state.Snapshot.Settings.Where(s => s.Owner == d.Id).ToList();
        UpdateSettings(mine, state);
        Enabled = Body.FirstOrDefault(b => b.Id == Ids.Dev(d.Id, "enabled"));
    }
}

public sealed class AppViewModel(Engine engine, string id) : GroupViewModel(engine, id)
{
    public void Update(AppInfo a, EngineState state)
    {
        Name = a.Name;
        Subtitle = a.Key == Core.Audio.AudioSystem.SystemSoundsKey ? "" : Path.GetFileName(a.Key);
        UpdateSettings(state.Snapshot.Settings.Where(s => s.Owner == Ids.AppOwner(a.Key)).ToList(), state);
    }
}

public sealed class AdoptViewModel(Engine engine, AdoptOffer offer, string oldName) : Observable
{
    public string Id => offer.NewId;
    public string Text => $"“{offer.Name}” has appeared on a different USB port. Use the settings you had for it before ({oldName})?";
    public Command AdoptCommand { get; } = new(() => engine.Adopt(offer));
    public Command DeclineCommand { get; } = new(() => engine.DeclineAdopt(offer));
}

public sealed class MainViewModel : Observable
{
    readonly Engine _engine;

    public MainViewModel(Engine engine)
    {
        _engine = engine;
        RestoreAllCommand = new Command(engine.RestoreAll);
    }

    public ObservableCollection<SettingViewModel> Defaults { get; } = new();
    public ObservableCollection<DeviceViewModel> Outputs { get; } = new();
    public ObservableCollection<DeviceViewModel> Inputs { get; } = new();
    public ObservableCollection<AppViewModel> Apps { get; } = new();
    public ObservableCollection<SettingViewModel> SystemSettings { get; } = new();
    public ObservableCollection<AdoptViewModel> AdoptOffers { get; } = new();
    public Command RestoreAllCommand { get; }

    int _driftCount;
    public int DriftCount { get => _driftCount; private set { if (Set(ref _driftCount, value)) { Raise(nameof(StatusText)); Raise(nameof(HasDrift)); } } }
    public bool HasDrift => _driftCount > 0;
    public string StatusText => _driftCount switch
    {
        0 => "Everything is as you set it",
        1 => "1 setting changed",
        _ => $"{_driftCount} settings changed",
    };

    string? _error;
    public string? LastError { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    // ---- drag to reorder (within a section only)

    ObservableCollection<DeviceViewModel>? SectionOf(DeviceViewModel d) => Outputs.Contains(d) ? Outputs : Inputs.Contains(d) ? Inputs : null;

    /// <summary>Shows where <paramref name="dragged"/> would land; returns false if it can't be dropped there.</summary>
    public bool DragOver(DeviceViewModel dragged, DeviceViewModel target, bool below)
    {
        ClearDropHints();
        var section = SectionOf(dragged);
        if (section == null || !section.Contains(target) || target == dragged) return false;
        target.DropAbove = !below;
        target.DropBelow = below;
        return true;
    }

    public void Drop(DeviceViewModel dragged, DeviceViewModel target, bool below)
    {
        ClearDropHints();
        var section = SectionOf(dragged);
        if (section == null || !section.Contains(target) || target == dragged) return;
        var to = section.IndexOf(target) + (below ? 1 : 0);
        if (section.IndexOf(dragged) < to) to--;
        _engine.MoveDeviceTo(dragged.Id, to);
    }

    public void ClearDropHints()
    {
        foreach (var d in Outputs.Concat(Inputs)) { d.DropAbove = false; d.DropBelow = false; }
    }

    /// <summary>Raised with a short description when new drift appears (for the tray balloon).</summary>
    public event Action<string>? NewDrift;
    HashSet<string> _knownDrift = new();

    public void Update(EngineState state)
    {
        var snap = state.Snapshot;
        var byOwner = snap.Settings.ToLookup(s => s.Owner);

        CollectionSync.Sync(Defaults, byOwner[Ids.Defaults].ToList(), s => s.Id, vm => vm.Id, s => new SettingViewModel(_engine, s),
            (vm, s) => vm.Update(s, state.Status.GetValueOrDefault(s.Id), state.KnownNames));
        CollectionSync.Sync(SystemSettings, byOwner[Ids.System].ToList(), s => s.Id, vm => vm.Id, s => new SettingViewModel(_engine, s),
            (vm, s) => vm.Update(s, state.Status.GetValueOrDefault(s.Id), state.KnownNames));

        List<DeviceInfo> Devices(Flow f) => DeviceOrdering.Sort(snap.Devices.Where(d => d.Flow == f), state.DeviceOrder);
        CollectionSync.Sync(Outputs, Devices(Flow.Render), d => d.Id, vm => vm.Id, d => new DeviceViewModel(_engine, d.Id), (vm, d) => vm.Update(d, state));
        CollectionSync.Sync(Inputs, Devices(Flow.Capture), d => d.Id, vm => vm.Id, d => new DeviceViewModel(_engine, d.Id), (vm, d) => vm.Update(d, state));
        CollectionSync.Sync(Apps, snap.Apps, a => a.Key, vm => vm.Id, a => new AppViewModel(_engine, a.Key), (vm, a) => vm.Update(a, state));
        CollectionSync.Sync(AdoptOffers, state.AdoptOffers, o => o.NewId, vm => vm.Id,
            o => new AdoptViewModel(_engine, o, state.KnownNames.GetValueOrDefault(o.OldId, o.Name)), (_, _) => { });

        DriftCount = state.DriftCount;
        LastError = state.LastError;

        var drifted = state.Status.Where(kv => kv.Value.State is DriftState.Drifted or DriftState.Fighting).Select(kv => kv.Key).ToHashSet();
        var fresh = drifted.Except(_knownDrift).ToList();
        _knownDrift = drifted;
        if (fresh.Count > 0)
        {
            var s = snap.Settings.FirstOrDefault(x => x.Id == fresh[0]);
            var owner = s == null ? "" : snap.Devices.FirstOrDefault(d => d.Id == s.Owner)?.Name ?? snap.Apps.FirstOrDefault(a => Ids.AppOwner(a.Key) == s.Owner)?.Name ?? "";
            NewDrift?.Invoke(fresh.Count == 1 && s != null ? $"{(owner.Length > 0 ? owner + ": " : "")}{s.Label} changed" : $"{fresh.Count} audio settings changed");
        }
    }
}

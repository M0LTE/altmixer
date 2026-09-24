using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AltMixer.Core.Audio;
using AltMixer.Core.Interop;
using AltMixer.Core.Model;
using AltMixer.Core.State;

namespace AltMixer.Core;

public sealed record EngineState(
    Snapshot Snapshot,
    IReadOnlyDictionary<string, SettingStatus> Status,
    IReadOnlyList<AdoptOffer> AdoptOffers,
    IReadOnlyDictionary<string, bool> Collapsed,
    IReadOnlyDictionary<string, string> KnownNames,
    string? LastError)
{
    public int DriftCount => Status.Values.Count(s => s.State is DriftState.Drifted or DriftState.Fighting);
}

/// <summary>
/// Owns the audio system on a dedicated MTA thread: listens for changes, reconciles against the desired state,
/// enforces locks immediately, and publishes <see cref="EngineState"/> for the UI.
/// </summary>
public sealed class Engine : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    readonly DesiredStore _store;
    readonly Reconciler _reconciler;
    readonly BlockingCollection<Action> _commands = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    volatile bool _invalidate;
    AudioSystem? _audio;
    Snapshot _snapshot = Snapshot.Empty;
    string? _lastError;
    DateTime _dirtySince = DateTime.MaxValue;

    public event Action<EngineState>? StateChanged;

    public Engine(DesiredStore store)
    {
        _store = store;
        _reconciler = new Reconciler(store);
        _thread = new Thread(Run) { IsBackground = true, Name = "AltMixer engine" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
    }

    // ------------------------------------------------------------------ commands (any thread)

    /// <summary>User changed a setting in the UI: apply it and make it the desired value.</summary>
    public void Set(string id, SettingValue value) => Post(() =>
    {
        _store.SetValue(id, value);
        _reconciler.ResetFight(id);
        if (Find(id) is { } s) Try(() => _audio!.Apply(s, value));
    });

    public void Restore(string id) => Post(() =>
    {
        _reconciler.ResetFight(id);
        if (Find(id) is { } s && _store.Get(id) is { } e) Try(() => _audio!.Apply(s, e.Value));
    });

    public void RestoreAll() => Post(() =>
    {
        foreach (var s in _snapshot.Settings)
            if (_store.Get(s.Id) is { } e && !Reconciler.Matches(s, s.Current, e.Value))
            {
                if (s.Kind == SettingKind.Choice && s.Choices.All(c => c.Id != e.Value.Text)) continue; // unavailable
                _reconciler.ResetFight(s.Id);
                Try(() => _audio!.Apply(s, e.Value));
            }
    });

    /// <summary>Take the current value as the new desired value.</summary>
    public void Accept(string id) => Post(() =>
    {
        if (Find(id) is { } s) _store.SetValue(id, s.Current);
    });

    public void SetLocked(string id, bool locked) => Post(() =>
    {
        _store.SetLocked(id, locked);
        _reconciler.ResetFight(id);
    });

    public void Adopt(AdoptOffer offer) => Post(() =>
    {
        _store.Adopt(offer.OldId, offer.NewId);
        if (_store.Data.Devices.TryGetValue(offer.NewId, out var k)) k.AdoptResolved = true;
    });

    public void DeclineAdopt(AdoptOffer offer) => Post(() =>
    {
        if (_store.Data.Devices.TryGetValue(offer.NewId, out var k)) { k.AdoptResolved = true; _store.MarkDirty(); }
    });

    public void SetCollapsed(string deviceId, bool collapsed) => Post(() => _store.SetCollapsed(deviceId, collapsed));

    void Post(Action a)
    {
        _commands.Add(a);
        _wake.Set();
    }

    Setting? Find(string id) => _snapshot.Settings.FirstOrDefault(s => s.Id == id);

    void Try(Action a)
    {
        try { a(); _lastError = null; }
        catch (Exception e) { _lastError = e.Message; Trace.WriteLine(e); }
    }

    // ------------------------------------------------------------------ engine thread

    void Run()
    {
        _audio = new AudioSystem(() => _wake.Set());
        var notify = new NotifyClient(this);
        _audio.Enumerator.RegisterEndpointNotificationCallback(notify);
        try
        {
            Cycle();
            while (!_stop)
            {
                _wake.WaitOne(PollInterval);
                if (_stop) break;
                Thread.Sleep(15); // coalesce bursts of notifications
                while (_commands.TryTake(out var cmd)) cmd();
                Cycle();
            }
        }
        finally
        {
            try { _audio.Enumerator.UnregisterEndpointNotificationCallback(notify); } catch { }
            _audio.Dispose();
            _store.Save();
        }
    }

    ReconcileResult? _last;

    void Cycle()
    {
        if (_invalidate) { _invalidate = false; _audio!.Invalidate(); }
        try { _snapshot = _audio!.Read(); }
        catch (Exception e) { _lastError = e.Message; Trace.WriteLine(e); }
        var result = _reconciler.Reconcile(_snapshot);
        foreach (var (setting, value) in result.ToApply) Try(() => _audio!.Apply(setting, value));
        _last = result;
        Save();
        Publish();
        if (result.ToApply.Count > 0) _wake.Set(); // re-read soon to confirm the restore took
    }

    void Save()
    {
        if (_store.Dirty && _dirtySince == DateTime.MaxValue) _dirtySince = DateTime.UtcNow;
        if (_store.Dirty && DateTime.UtcNow - _dirtySince >= SaveDelay || _stop)
        {
            Try(_store.Save);
            _dirtySince = DateTime.MaxValue;
        }
    }

    void Publish()
    {
        if (_last == null) return;
        var collapsed = _snapshot.Devices.ToDictionary(d => d.Id, d => _store.GetCollapsed(d.Id) ?? d.Status == DeviceStatus.Disabled);
        // Reflect commands (lock, accept) that ran after the last reconcile.
        var status = _last.Status.ToDictionary(kv => kv.Key, kv => _store.Get(kv.Key) is { } e ? kv.Value with { Desired = e.Value, Locked = e.Locked } : kv.Value);
        StateChanged?.Invoke(new EngineState(_snapshot, status, _last.AdoptOffers.Where(o => _store.Data.Devices.TryGetValue(o.NewId, out var k) && !k.AdoptResolved).ToList(), collapsed,
            _store.Data.Devices.ToDictionary(d => d.Key, d => d.Value.Name), _lastError));
    }

    [ComVisible(true)]
    sealed class NotifyClient(Engine e) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, DeviceState state) { e._invalidate = true; e._wake.Set(); }
        public void OnDeviceAdded(string id) { e._invalidate = true; e._wake.Set(); }
        public void OnDeviceRemoved(string id) { e._invalidate = true; e._wake.Set(); }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id) => e._wake.Set();
        public void OnPropertyValueChanged(string id, PropertyKey key) => e._wake.Set();
    }
}

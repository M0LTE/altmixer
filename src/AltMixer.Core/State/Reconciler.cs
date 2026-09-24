using AltMixer.Core.Model;

namespace AltMixer.Core.State;

public enum DriftState
{
    None,
    /// <summary>Current differs from desired and isn't locked: flagged, waiting for Restore or Accept.</summary>
    Drifted,
    /// <summary>Locked and being restored automatically.</summary>
    Restoring,
    /// <summary>Locked, but something has kept changing it back for 30 s or more; we've stopped fighting.</summary>
    Fighting,
    /// <summary>The desired value refers to a device that isn't connected, so it can't be applied right now.</summary>
    Unavailable,
}

public sealed record SettingStatus(SettingValue Desired, bool Locked, DriftState State);

public sealed record AdoptOffer(string NewId, string OldId, string Name);

public sealed record ReconcileResult(
    IReadOnlyDictionary<string, SettingStatus> Status,
    IReadOnlyList<(Setting Setting, SettingValue Value)> ToApply,
    IReadOnlyList<AdoptOffer> AdoptOffers);

/// <summary>Compares what the system reports with what the user wants.</summary>
public sealed class Reconciler(DesiredStore store, Func<DateTime>? clock = null)
{
    // "Fighting" means something has kept undoing a lock for a sustained period, not a burst of restores.
    const int FightMinRestores = 5;
    static readonly TimeSpan FightSpan = TimeSpan.FromSeconds(30);
    /// <summary>A pause this long with nothing to restore ends a fight: the next one starts from scratch.</summary>
    static readonly TimeSpan FightQuiet = TimeSpan.FromSeconds(10);
    /// <summary>After the user changes a setting, devices may lag or round the value; don't treat that as drift.</summary>
    static readonly TimeSpan UserGrace = TimeSpan.FromSeconds(3);

    readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    readonly Dictionary<string, List<DateTime>> _applies = new();
    readonly Dictionary<string, DateTime> _userChanged = new();

    public static bool Matches(Setting s, SettingValue current, SettingValue desired) => s.Kind switch
    {
        SettingKind.Level => current.Number is { } a && desired.Number is { } b && Math.Abs(a - b) <= Tolerance(s),
        SettingKind.Toggle => current.Flag == desired.Flag,
        _ => current.Text == desired.Text,
    };

    static double Tolerance(Setting s) => Math.Max(0.05, s.Step / 2 + 0.001);

    /// <summary>What we want for a setting we've never seen: what it is now, but never above the cap.</summary>
    public static SettingValue Initial(Setting s) =>
        s.Kind == SettingKind.Level && s.Current.Number is { } n && n > s.Cap + Tolerance(s) ? SettingValue.Of(s.Cap) : s.Current;

    public ReconcileResult Reconcile(Snapshot snap)
    {
        var status = new Dictionary<string, SettingStatus>();
        var apply = new List<(Setting, SettingValue)>();
        var offers = TrackDevices(snap);

        foreach (var s in snap.Settings)
        {
            var entry = store.Get(s.Id);
            if (entry == null)
            {
                store.SetValue(s.Id, Initial(s));
                entry = store.Get(s.Id)!;
            }
            var desired = entry.Value;
            var inGrace = _userChanged.TryGetValue(s.Id, out var changed) && _now() - changed < UserGrace;
            if (inGrace && Settled(s, desired))
            {
                // The device rounded the user's value to one it supports: that is what they asked for.
                store.SetValue(s.Id, s.Current);
                desired = s.Current;
            }
            DriftState state;
            if (Matches(s, s.Current, desired))
            {
                state = DriftState.None;
            }
            else if (s.Kind == SettingKind.Choice && s.Choices.All(c => c.Id != desired.Text))
            {
                state = DriftState.Unavailable;
            }
            else if (!entry.Locked)
            {
                state = DriftState.Drifted;
            }
            else if (IsFighting(s.Id))
            {
                state = DriftState.Fighting;
            }
            else
            {
                state = DriftState.Restoring;
                apply.Add((s, desired));
                if (!inGrace) RecordApply(s.Id);
            }
            status[s.Id] = new SettingStatus(desired, entry.Locked, state);
        }
        return new ReconcileResult(status, apply, offers);
    }

    /// <summary>Called when the user explicitly restores; clears fight history so a lock can try again.</summary>
    public void ResetFight(string id) => _applies.Remove(id);

    /// <summary>The user just changed this setting: clear its fight history and allow a moment for the device to settle.</summary>
    public void UserChanged(string id)
    {
        ResetFight(id);
        _userChanged[id] = _now();
    }

    /// <summary>A level within a step or so of what was asked for: the device's nearest supported value.</summary>
    static bool Settled(Setting s, SettingValue desired) =>
        s.Kind == SettingKind.Level && s.Current.Number is { } cur && desired.Number is { } want &&
        Math.Abs(cur - want) <= Math.Max(s.Step, 0.1) * 1.5 + 0.01;

    bool IsFighting(string id)
    {
        if (!_applies.TryGetValue(id, out var list) || list.Count < FightMinRestores) return false;
        return _now() - list[^1] <= FightQuiet && list[^1] - list[0] >= FightSpan;
    }

    void RecordApply(string id)
    {
        if (!_applies.TryGetValue(id, out var list)) _applies[id] = list = new();
        if (list.Count > 0 && _now() - list[^1] > FightQuiet) list.Clear();
        list.Add(_now());
    }

    IReadOnlyList<AdoptOffer> TrackDevices(Snapshot snap)
    {
        var known = store.Data.Devices;
        var now = _now();
        var present = snap.Devices.Select(d => d.Id).ToHashSet();
        var offers = new List<AdoptOffer>();
        // Before we've ever saved anything, treat everything as long-known: nothing to adopt from.
        var firstRun = known.Count == 0;
        foreach (var d in snap.Devices)
        {
            if (!known.TryGetValue(d.Id, out var k))
            {
                known[d.Id] = k = new KnownDevice { Name = d.Name, Flow = d.Flow, Fingerprint = d.Fingerprint, FirstSeen = now, AdoptResolved = firstRun };
                store.MarkDirty();
            }
            if (k.LastSeen < now - TimeSpan.FromMinutes(1)) { k.LastSeen = now; store.MarkDirty(); }
            if (k.AdoptResolved) continue;

            var candidate = known
                .Where(o => o.Key != d.Id && !present.Contains(o.Key) && o.Value.Fingerprint == d.Fingerprint && o.Value.Flow == d.Flow)
                .OrderByDescending(o => o.Value.LastSeen)
                .FirstOrDefault();
            if (candidate.Key != null) offers.Add(new AdoptOffer(d.Id, candidate.Key, d.Name));
            else { k.AdoptResolved = true; store.MarkDirty(); }
        }
        return offers;
    }
}

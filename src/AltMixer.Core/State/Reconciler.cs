using AltMixer.Core.Model;

namespace AltMixer.Core.State;

public enum DriftState
{
    None,
    /// <summary>Current differs from desired and isn't locked: flagged, waiting for Restore or Accept.</summary>
    Drifted,
    /// <summary>Locked and being restored automatically.</summary>
    Restoring,
    /// <summary>Locked, but something keeps changing it back; we've stopped fighting.</summary>
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
    const int FightLimit = 5;
    static readonly TimeSpan FightWindow = TimeSpan.FromSeconds(30);

    readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    readonly Dictionary<string, List<DateTime>> _applies = new();

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
                RecordApply(s.Id);
            }
            status[s.Id] = new SettingStatus(desired, entry.Locked, state);
        }
        return new ReconcileResult(status, apply, offers);
    }

    /// <summary>Called when the user explicitly restores; clears fight history so a lock can try again.</summary>
    public void ResetFight(string id) => _applies.Remove(id);

    bool IsFighting(string id) =>
        _applies.TryGetValue(id, out var list) && list.Count(t => _now() - t < FightWindow) >= FightLimit;

    void RecordApply(string id)
    {
        if (!_applies.TryGetValue(id, out var list)) _applies[id] = list = new();
        list.Add(_now());
        list.RemoveAll(t => _now() - t > FightWindow);
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

using AltMixer.Core.Model;

namespace AltMixer.Core.State;

/// <summary>
/// The user's device order. Devices they've placed come first, in their order; the rest follow in the default order
/// (enabled before disabled, default and communications devices first, then by name).
/// </summary>
public static class DeviceOrdering
{
    public static List<DeviceInfo> Sort(IEnumerable<DeviceInfo> devices, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return devices
            .OrderBy(d => rank.TryGetValue(d.Id, out var r) ? r : int.MaxValue)
            .ThenBy(d => d.Status)
            .ThenByDescending(d => d.IsDefault)
            .ThenByDescending(d => d.IsComms)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Moves one device up (delta -1) or down (+1). See <see cref="MoveTo"/>.</summary>
    public static List<string>? Move(IReadOnlyList<string> stored, IReadOnlyList<string> shown, string id, int delta)
    {
        var from = shown.ToList().IndexOf(id);
        return from < 0 ? null : MoveTo(stored, shown, id, from + delta);
    }

    /// <summary>
    /// Moves one device to position <paramref name="index"/> among <paramref name="shown"/> (one section, as displayed)
    /// and returns the new stored order, or null if nothing changes. Devices that aren't shown (disconnected) keep
    /// their slots, so they come back where they were.
    /// </summary>
    public static List<string>? MoveTo(IReadOnlyList<string> stored, IReadOnlyList<string> shown, string id, int index)
    {
        var list = shown.ToList();
        var from = list.IndexOf(id);
        if (from < 0 || index < 0 || index >= list.Count || index == from) return null;
        list.RemoveAt(from);
        list.Insert(index, id);

        // Refill the slots the shown devices occupy with their new order; append any not yet stored.
        var shownSet = list.ToHashSet();
        var queue = new Queue<string>(list);
        var result = new List<string>();
        foreach (var s in stored)
            result.Add(shownSet.Contains(s) ? queue.Dequeue() : s);
        result.AddRange(queue);
        return result;
    }
}

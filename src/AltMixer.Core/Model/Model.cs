namespace AltMixer.Core.Model;

public enum Flow { Render, Capture }

public enum DeviceStatus { Active, Disabled }

public enum SettingKind
{
    /// <summary>A gain in dB (Number). Always capped at 0 dB where the device allows.</summary>
    Level,
    /// <summary>On/off (Flag).</summary>
    Toggle,
    /// <summary>One of <see cref="Setting.Choices"/> (Text = choice id).</summary>
    Choice,
    /// <summary>An opaque value we can compare and restore but not edit (Text), shown via <see cref="Setting.Display"/>.</summary>
    Opaque,
}

/// <summary>A setting value. Exactly one field is used, depending on <see cref="SettingKind"/>.</summary>
public sealed record SettingValue(double? Number = null, bool? Flag = null, string? Text = null)
{
    public static SettingValue Of(double v) => new(Number: v);
    public static SettingValue Of(bool v) => new(Flag: v);
    public static SettingValue Of(string v) => new(Text: v);

    public override string ToString() => Number is { } n ? $"{n:0.0} dB" : Flag is { } f ? (f ? "On" : "Off") : Text ?? "";
}

public sealed record Choice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Everything the UI needs to show one setting, read from the system at one instant.</summary>
public sealed record Setting
{
    public required string Id { get; init; }
    /// <summary>Device id, "defaults", "system" or "app:&lt;key&gt;".</summary>
    public required string Owner { get; init; }
    public required string Label { get; init; }
    public required SettingKind Kind { get; init; }
    public required SettingValue Current { get; init; }

    public double Min { get; init; }
    /// <summary>Max as reported by the driver (may be above 0 dB).</summary>
    public double Max { get; init; }
    public double Step { get; init; }
    public IReadOnlyList<Choice> Choices { get; init; } = [];
    /// <summary>Human text for the current value (Opaque settings, or extra detail).</summary>
    public string? Display { get; init; }
    public string? Note { get; init; }
    /// <summary>Shown in the device header rather than the body.</summary>
    public bool Primary { get; init; }

    /// <summary>The highest value we allow: 0 dB if the range permits, else the nearest end of the range.</summary>
    public double Cap => Math.Clamp(0, Min, Max);

    /// <summary>The driver's range doesn't include 0 dB, so its labels can't be trusted as real gain.</summary>
    public bool Uncalibrated => Kind == SettingKind.Level && Min > 0;

    public string Describe(SettingValue v)
    {
        if (Kind == SettingKind.Choice && v.Text != null)
            return Choices.FirstOrDefault(c => c.Id == v.Text)?.Label ?? "(not connected)";
        if (Kind == SettingKind.Opaque)
            return v == Current ? Display ?? "(unknown)" : "(saved value)";
        return v.ToString();
    }
}

public sealed record DeviceInfo
{
    public required string Id { get; init; }
    public required Flow Flow { get; init; }
    public required string Name { get; init; }
    public required DeviceStatus Status { get; init; }
    /// <summary>Flow + USB VID/PID + endpoint name; used to offer "adopt" when a serial-less device moves port.</summary>
    public required string Fingerprint { get; init; }
    public bool IsDefault { get; init; }
    public bool IsComms { get; init; }
}

public sealed record AppInfo(string Key, string Name, IReadOnlyList<Flow> Flows);

public sealed record Snapshot(IReadOnlyList<DeviceInfo> Devices, IReadOnlyList<AppInfo> Apps, IReadOnlyList<Setting> Settings)
{
    public static readonly Snapshot Empty = new([], [], []);
}

public static class Ids
{
    public const string Defaults = "defaults";
    public const string System = "system";

    public static string Default(Flow f, bool comms) => $"default/{Name(f)}{(comms ? "/comms" : "")}";
    public static string Dev(string deviceId, string what) => $"dev/{deviceId}/{what}";
    /// <summary>Per app per output device (Windows keeps app volume per device).</summary>
    public static string AppOnDevice(string appKey, string deviceId, string what) => $"app/{appKey}/{deviceId}/{what}";
    public static string AppRoute(string appKey, Flow f) => $"app/{appKey}/{Name(f)}/route";
    public static string AppOwner(string appKey) => $"app:{appKey}";
    public const string Ducking = "sys/ducking";

    public static string Name(Flow f) => f == Flow.Render ? "render" : "capture";
}


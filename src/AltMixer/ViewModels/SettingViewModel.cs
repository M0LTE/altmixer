using System.Windows.Threading;
using AltMixer.Core;
using AltMixer.Core.Model;
using AltMixer.Core.State;

namespace AltMixer.ViewModels;

/// <summary>One setting row: its control, lock, and drift marker with Restore / Accept.</summary>
public sealed class SettingViewModel : Observable
{
    static readonly TimeSpan UserGrace = TimeSpan.FromMilliseconds(800);

    readonly Engine _engine;
    readonly DispatcherTimer _sendTimer;
    Setting _setting;
    DateTime _userTouched;
    bool _updating;

    public SettingViewModel(Engine engine, Setting s)
    {
        _engine = engine;
        _setting = s;
        Id = s.Id;
        _sendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _sendTimer.Tick += (_, _) => { _sendTimer.Stop(); _engine.Set(Id, SettingValue.Of(_number)); };
        RestoreCommand = new Command(() => _engine.Restore(Id));
        AcceptCommand = new Command(() => _engine.Accept(Id));
    }

    public string Id { get; }
    public string Label => _setting.Label;
    public SettingKind Kind => _setting.Kind;
    public double Min => _setting.Min;
    /// <summary>The slider never goes above the cap (normally 0 dB).</summary>
    public double Max => _setting.Uncalibrated ? _setting.Min : _setting.Cap;
    public double SmallChange => _setting.Step > 0 ? Math.Max(_setting.Step, 0.1) : 0.5;
    public double LargeChange => Math.Max(SmallChange, 3);
    public IReadOnlyList<Choice> Choices => _setting.Choices;
    public string? Note => _setting.Note;
    public bool HasNote => !string.IsNullOrEmpty(_setting.Note);
    public bool IsFixed => Kind == SettingKind.Level && Max - Min < 0.01;

    public Command RestoreCommand { get; }
    public Command AcceptCommand { get; }

    double _number;
    /// <summary>The real current level, which may be above the slider's cap (e.g. a mic at +30 dB).</summary>
    double _actual;

    /// <summary>Slider position; never above the cap.</summary>
    public double Number
    {
        get => _number;
        set
        {
            value = Math.Clamp(value, Min, Max);
            if (!Set(ref _number, value)) return;
            if (!_updating) _actual = value;
            Raise(nameof(ValueText));
            if (_updating) return;
            _userTouched = DateTime.UtcNow;
            _sendTimer.Stop();
            _sendTimer.Start();
        }
    }

    bool _flag;
    public bool Flag
    {
        get => _flag;
        set
        {
            if (!Set(ref _flag, value) || _updating) return;
            _userTouched = DateTime.UtcNow;
            _engine.Set(Id, SettingValue.Of(value));
        }
    }

    string? _choiceId;
    public string? ChoiceId
    {
        get => _choiceId;
        set
        {
            if (value == null || !Set(ref _choiceId, value) || _updating) return;
            _userTouched = DateTime.UtcNow;
            _engine.Set(Id, SettingValue.Of(value));
        }
    }

    public string ValueText => Kind switch
    {
        SettingKind.Level when Id.StartsWith("app/") && _actual <= Min => "−∞ dB",
        SettingKind.Level => $"{(_actual > 0.05 ? "+" : "")}{_actual:0.0} dB".Replace('-', '−'),
        SettingKind.Opaque => _setting.Display ?? "",
        _ => "",
    };

    bool _locked;
    public bool IsLocked
    {
        get => _locked;
        set
        {
            if (!Set(ref _locked, value) || _updating) return;
            _engine.SetLocked(Id, value);
        }
    }

    DriftState _state;
    public DriftState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(IsDrifted)); Raise(nameof(IsFighting)); Raise(nameof(IsUnavailable)); Raise(nameof(CanAct)); } } }
    public bool IsDrifted => _state is DriftState.Drifted or DriftState.Fighting;
    public bool IsFighting => _state == DriftState.Fighting;
    public bool IsUnavailable => _state == DriftState.Unavailable;
    public bool CanAct => IsDrifted;

    string _driftText = "";
    public string DriftText { get => _driftText; private set => Set(ref _driftText, value); }

    public void Update(Setting s, SettingStatus? status, IReadOnlyDictionary<string, string> knownNames)
    {
        var shapeChanged = s.Min != _setting.Min || s.Max != _setting.Max || s.Step != _setting.Step || !s.Choices.SequenceEqual(_setting.Choices) || s.Note != _setting.Note || s.Label != _setting.Label;
        _setting = s;
        _updating = true;
        try
        {
            if (shapeChanged)
            {
                Raise(nameof(Min)); Raise(nameof(Max)); Raise(nameof(Choices)); Raise(nameof(Note)); Raise(nameof(HasNote));
                Raise(nameof(SmallChange)); Raise(nameof(LargeChange)); Raise(nameof(Label)); Raise(nameof(IsFixed));
            }
            // Don't yank a control out from under the user while they're using it.
            if (DateTime.UtcNow - _userTouched > UserGrace && !_sendTimer.IsEnabled)
            {
                switch (s.Kind)
                {
                    case SettingKind.Level: _actual = s.Current.Number ?? 0; Number = _actual; break;
                    case SettingKind.Toggle: Flag = s.Current.Flag ?? false; break;
                    case SettingKind.Choice:
                        if (shapeChanged) { _choiceId = null; Raise(nameof(ChoiceId)); }
                        ChoiceId = s.Current.Text;
                        break;
                }
            }
            Raise(nameof(ValueText));
            if (status != null)
            {
                IsLocked = status.Locked;
                State = status.State;
                string Describe(SettingValue v) =>
                    s.Kind == SettingKind.Choice && v.Text != null && s.Choices.All(c => c.Id != v.Text) && knownNames.TryGetValue(v.Text, out var n)
                        ? $"{n} (not connected)" : s.Describe(v);
                DriftText = status.State switch
                {
                    DriftState.Drifted => $"Changed outside AltMixer.\nWanted: {Describe(status.Desired)}\nNow: {Describe(s.Current)}",
                    DriftState.Fighting => $"Locked, but something keeps changing it back, so AltMixer has stopped restoring it.\nWanted: {Describe(status.Desired)}\nNow: {Describe(s.Current)}",
                    DriftState.Unavailable => $"Wanted: {Describe(status.Desired)}",
                    _ => "",
                };
            }
        }
        finally { _updating = false; }
    }
}

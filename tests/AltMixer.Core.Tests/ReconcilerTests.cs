using AltMixer.Core.Model;
using AltMixer.Core.State;

namespace AltMixer.Core.Tests;

public class ReconcilerTests
{
    static DeviceInfo Dev(string id, string fp = "Render|vid_08bb&pid_2902|Speakers") =>
        new() { Id = id, Flow = Flow.Render, Name = "Speakers (USB Audio CODEC )", Status = DeviceStatus.Active, Fingerprint = fp };

    static Setting Level(string id, double current, double min = -96, double max = 0, double step = 1) =>
        new() { Id = id, Owner = "d", Label = "Level", Kind = SettingKind.Level, Current = SettingValue.Of(current), Min = min, Max = max, Step = step };

    static Setting Toggle(string id, bool current) =>
        new() { Id = id, Owner = "d", Label = "T", Kind = SettingKind.Toggle, Current = SettingValue.Of(current) };

    static Setting DefaultOut(string current, params string[] active) =>
        new() { Id = Ids.Default(Flow.Render, false), Owner = Ids.Defaults, Label = "Default output", Kind = SettingKind.Choice, Current = SettingValue.Of(current), Choices = active.Select(a => new Choice(a, a)).ToList() };

    static Snapshot Snap(params Setting[] s) => new([Dev("d")], [], s);

    [Fact]
    public void First_sight_takes_current_value_as_desired()
    {
        var store = new DesiredStore(null);
        var r = new Reconciler(store).Reconcile(Snap(Level("l", -12)));
        Assert.Equal(DriftState.None, r.Status["l"].State);
        Assert.Equal(-12, store.Get("l")!.Value.Number);
    }

    [Fact]
    public void First_sight_above_0dB_is_capped_and_flagged()
    {
        var store = new DesiredStore(null);
        var r = new Reconciler(store).Reconcile(Snap(Level("mic", 30, max: 30)));
        Assert.Equal(0, store.Get("mic")!.Value.Number);
        Assert.Equal(DriftState.Drifted, r.Status["mic"].State);
    }

    [Fact]
    public void Uncalibrated_range_caps_at_its_minimum()
    {
        var s = Level("cam", 39.5, min: 18, max: 54, step: 0.5);
        Assert.True(s.Uncalibrated);
        Assert.Equal(18, s.Cap);
        Assert.Equal(18, Reconciler.Initial(s).Number);
    }

    [Fact]
    public void Level_within_half_a_step_matches()
    {
        var store = new DesiredStore(null);
        store.SetValue("l", SettingValue.Of(-10.0));
        var r = new Reconciler(store).Reconcile(Snap(Level("l", -10.5, step: 1.5)));
        Assert.Equal(DriftState.None, r.Status["l"].State);
    }

    [Fact]
    public void Unlocked_change_is_flagged_not_applied()
    {
        var store = new DesiredStore(null);
        store.SetValue("t", SettingValue.Of(false));
        var r = new Reconciler(store).Reconcile(Snap(Toggle("t", true)));
        Assert.Equal(DriftState.Drifted, r.Status["t"].State);
        Assert.Empty(r.ToApply);
    }

    [Fact]
    public void Locked_change_is_applied()
    {
        var store = new DesiredStore(null);
        store.SetValue("t", SettingValue.Of(false));
        store.SetLocked("t", true);
        var r = new Reconciler(store).Reconcile(Snap(Toggle("t", true)));
        Assert.Equal(DriftState.Restoring, r.Status["t"].State);
        Assert.Equal(false, Assert.Single(r.ToApply).Value.Flag);
    }

    sealed class FakeClock
    {
        public DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    static (DesiredStore store, Reconciler rec, FakeClock clock) LockedToggle()
    {
        var store = new DesiredStore(null);
        store.SetValue("t", SettingValue.Of(false));
        store.SetLocked("t", true);
        var clock = new FakeClock();
        return (store, new Reconciler(store, () => clock.Now), clock);
    }

    [Fact]
    public void A_burst_of_restores_is_not_fighting()
    {
        var (_, rec, clock) = LockedToggle();
        for (var i = 0; i < 20; i++)
        {
            var r = rec.Reconcile(Snap(Toggle("t", true)));
            Assert.Equal(DriftState.Restoring, r.Status["t"].State);
            clock.Advance(0.1);
        }
    }

    [Fact]
    public void Being_undone_for_30_seconds_is_fighting_until_the_user_restores()
    {
        var (_, rec, clock) = LockedToggle();
        for (var i = 0; i < 5; i++)
        {
            Assert.Single(rec.Reconcile(Snap(Toggle("t", true))).ToApply);
            clock.Advance(8);
        }
        var r = rec.Reconcile(Snap(Toggle("t", true)));
        Assert.Equal(DriftState.Fighting, r.Status["t"].State);
        Assert.Empty(r.ToApply);

        rec.ResetFight("t");
        Assert.Single(rec.Reconcile(Snap(Toggle("t", true))).ToApply);
    }

    [Fact]
    public void Separate_bursts_with_a_pause_between_them_are_not_one_fight()
    {
        var (_, rec, clock) = LockedToggle();
        for (var burst = 0; burst < 4; burst++)
        {
            for (var i = 0; i < 10; i++) { rec.Reconcile(Snap(Toggle("t", true))); clock.Advance(0.5); } // a 5 s drag
            clock.Advance(12);                                                                         // then a pause
        }
        Assert.Equal(DriftState.Restoring, rec.Reconcile(Snap(Toggle("t", true))).Status["t"].State);
    }

    [Fact]
    public void Restores_right_after_a_user_change_never_count_as_fighting()
    {
        var (_, rec, clock) = LockedToggle();
        for (var i = 0; i < 40; i++)
        {
            if (i % 4 == 0) rec.UserChanged("t"); // e.g. dragging a slider on a slow device
            Assert.Equal(DriftState.Restoring, rec.Reconcile(Snap(Toggle("t", true))).Status["t"].State);
            clock.Advance(1);
        }
    }

    [Fact]
    public void Level_the_device_rounds_to_becomes_the_desired_value()
    {
        var store = new DesiredStore(null);
        store.SetValue("l", SettingValue.Of(-10.0));
        store.SetLocked("l", true);
        var clock = new FakeClock();
        var rec = new Reconciler(store, () => clock.Now);

        rec.UserChanged("l");
        var r = rec.Reconcile(Snap(Level("l", -11.2, step: 1.5)));
        Assert.Equal(DriftState.None, r.Status["l"].State);
        Assert.Equal(-11.2, store.Get("l")!.Value.Number);

        // Outside the grace period a change is drift again, and the lock restores it.
        clock.Advance(5);
        r = rec.Reconcile(Snap(Level("l", -20, step: 1.5)));
        Assert.Equal(-11.2, Assert.Single(r.ToApply).Value.Number);
    }

    [Fact]
    public void Default_pointing_at_missing_device_is_unavailable_not_drift()
    {
        var store = new DesiredStore(null);
        store.SetValue(Ids.Default(Flow.Render, false), SettingValue.Of("dell"));
        store.SetLocked(Ids.Default(Flow.Render, false), true);
        var r = new Reconciler(store).Reconcile(Snap(DefaultOut("aioc", "aioc", "realtek")));
        Assert.Equal(DriftState.Unavailable, r.Status[Ids.Default(Flow.Render, false)].State);
        Assert.Empty(r.ToApply);
    }

    [Fact]
    public void Locked_default_is_reverted_when_hijacked()
    {
        var store = new DesiredStore(null);
        store.SetValue(Ids.Default(Flow.Render, false), SettingValue.Of("dell"));
        store.SetLocked(Ids.Default(Flow.Render, false), true);
        var r = new Reconciler(store).Reconcile(Snap(DefaultOut("aioc", "aioc", "dell")));
        Assert.Equal("dell", Assert.Single(r.ToApply).Value.Text);
    }

    [Fact]
    public void Device_on_new_port_offers_adopt_and_adopt_moves_settings()
    {
        var store = new DesiredStore(null);
        var rec = new Reconciler(store);
        rec.Reconcile(new Snapshot([Dev("old")], [], [Level(Ids.Dev("old", "level"), -20) with { Owner = "old" }]));
        store.SetValue(Ids.Default(Flow.Render, false), SettingValue.Of("old"));

        var r = rec.Reconcile(new Snapshot([Dev("new")], [], [Level(Ids.Dev("new", "level"), 0) with { Owner = "new" }]));
        var offer = Assert.Single(r.AdoptOffers);
        Assert.Equal(("new", "old"), (offer.NewId, offer.OldId));

        store.Adopt("old", "new");
        Assert.Equal(-20, store.Get(Ids.Dev("new", "level"))!.Value.Number);
        Assert.Null(store.Get(Ids.Dev("old", "level")));
        Assert.Equal("new", store.Get(Ids.Default(Flow.Render, false))!.Value.Text);
    }

    [Fact]
    public void Devices_present_on_first_run_are_never_offered_for_adopt()
    {
        var store = new DesiredStore(null);
        var r = new Reconciler(store).Reconcile(new Snapshot([Dev("a"), Dev("b")], [], []));
        Assert.Empty(r.AdoptOffers);
        Assert.All(store.Data.Devices.Values, d => Assert.True(d.AdoptResolved));
    }

    [Fact]
    public void Store_round_trips_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"altmixer-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DesiredStore(path);
            store.SetValue("x", SettingValue.Of(-3.5));
            store.SetLocked("x", true);
            store.SetCollapsed("dev1", true);
            store.Save();

            var again = new DesiredStore(path);
            Assert.Equal(-3.5, again.Get("x")!.Value.Number);
            Assert.True(again.Get("x")!.Locked);
            Assert.True(again.GetCollapsed("dev1"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Migration_drops_v1_per_flow_app_levels_but_keeps_routes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"altmixer-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "Version": 1, "ActiveProfile": "Default", "Profiles": { "Default": { "Settings": {
                  "app/c:\\x.exe/capture/level": { "Value": { "Number": 0 } },
                  "app/c:\\x.exe/render/mute": { "Value": { "Flag": false } },
                  "app/c:\\x.exe/render/route": { "Value": { "Text": "default" }, "Locked": true } } } } }
                """);
            var store = new DesiredStore(path);
            Assert.Null(store.Get(@"app/c:\x.exe/capture/level"));
            Assert.Null(store.Get(@"app/c:\x.exe/render/mute"));
            Assert.True(store.Get(@"app/c:\x.exe/render/route")!.Locked);
            Assert.Equal(DesiredStore.CurrentVersion, store.Data.Version);
        }
        finally { File.Delete(path); }
    }
}

using System.Text;
using System.Windows;
using AltMixer.Core;
using AltMixer.Core.State;
using AltMixer.ViewModels;

namespace AltMixer;

/// <remarks>
/// Command line:
///   --tray             start hidden in the notification area (used by Start at login)
///   --dump &lt;file&gt;   write what AltMixer sees (devices, settings, drift) to a text file and exit; never changes anything
/// </remarks>
public partial class App : Application
{
    const string InstanceName = "AltMixer.SingleInstance";

    Mutex? _instance;
    EventWaitHandle? _showSignal;
    Engine? _engine;
    Tray? _tray;
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var dump = Array.IndexOf(args, "--dump");
        if (dump >= 0 && dump + 1 < args.Length)
        {
            Dump(args[dump + 1]);
            Shutdown();
            return;
        }

        _instance = new Mutex(true, InstanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!first)
        {
            _showSignal.Set(); // bring the running copy to the front
            Shutdown();
            return;
        }
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true }.Start();

        var store = new DesiredStore(DesiredStore.DefaultPath);
        _engine = new Engine(store);
        var vm = new MainViewModel(_engine);
        _window = new MainWindow { DataContext = vm };
        _tray = new Tray(ShowWindow, _engine.RestoreAll, Quit);
        vm.NewDrift += text => { if (!_window.IsVisible) _tray.Notify(text); };
        _engine.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            vm.Update(state);
            _tray.Update(state.DriftCount);
        });
        _engine.Start();

        if (!args.Contains("--tray")) ShowWindow();
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
        if (_window != null) { _window.ReallyClose = true; _window.Close(); }
        Shutdown();
    }

    /// <summary>Logoff, shutdown, or an installer asking us to close: really close rather than hide to the tray.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_window != null) _window.ReallyClose = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Diagnostics: one read + reconcile against the saved desired state, written to a file. Changes nothing.</summary>
    static void Dump(string path)
    {
        var store = new DesiredStore(DesiredStore.DefaultPath, readOnly: true);
        foreach (var e in store.Data.Profiles.Values.SelectMany(p => p.Settings.Values)) e.Locked = false; // never restore while dumping
        using var engine = new Engine(store);
        var done = new ManualResetEventSlim();
        EngineState? state = null;
        engine.StateChanged += s => { state ??= s; done.Set(); };
        engine.Start();
        done.Wait(TimeSpan.FromSeconds(20));

        var sb = new StringBuilder();
        if (state == null) sb.AppendLine("no state within 20 s");
        else
        {
            foreach (var group in state.Snapshot.Settings.GroupBy(s => s.Owner))
            {
                var dev = state.Snapshot.Devices.FirstOrDefault(d => d.Id == group.Key);
                sb.AppendLine(dev != null ? $"== {dev.Name} [{dev.Flow} {dev.Status}{(dev.IsDefault ? " default" : "")}{(dev.IsComms ? " comms" : "")}] {dev.Fingerprint}" : $"== {group.Key}");
                foreach (var s in group)
                {
                    var st = state.Status.GetValueOrDefault(s.Id);
                    var range = s.Kind == Core.Model.SettingKind.Level ? $" [{s.Min:0.##} .. {s.Max:0.##} step {s.Step:0.###}, cap {s.Cap:0.##}]" : "";
                    var choices = s.Choices.Count > 0 ? $" ({s.Choices.Count} choices)" : "";
                    sb.AppendLine($"   {s.Label,-45} {s.Describe(s.Current),-30}{range}{choices}  {st?.State}{(st != null && st.State != DriftState.None ? $" want {s.Describe(st.Desired)}" : "")}  {s.Id}");
                    if (s.Note != null) sb.AppendLine($"      note: {s.Note}");
                }
            }
            sb.AppendLine($"adopt offers: {state.AdoptOffers.Count}; drift: {state.DriftCount}; error: {state.LastError}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}

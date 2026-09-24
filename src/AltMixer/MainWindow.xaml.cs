using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AltMixer.Core.State;
using AltMixer.ViewModels;

namespace AltMixer;

public partial class MainWindow : Window
{
    public bool ReallyClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps AltMixer running in the tray so it can keep watching.
        if (!ReallyClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeviceViewModel d) d.IsCollapsed = !d.IsCollapsed;
    }

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        StartAtLoginItem.IsChecked = StartupRegistration.IsEnabled;
        b.ContextMenu!.PlacementTarget = b;
        b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
        b.ContextMenu.IsOpen = true;
    }

    void StartAtLogin_Click(object sender, RoutedEventArgs e) => StartupRegistration.Set(StartAtLoginItem.IsChecked);

    void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(DesiredStore.DefaultPath)!) { UseShellExecute = true });

    void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();
}


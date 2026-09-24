using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ---- drag a device card by its handle to reorder it

    Point _dragStart;
    MainViewModel Vm => (MainViewModel)DataContext;

    void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        ((UIElement)sender).Focus();
    }

    void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ((FrameworkElement)sender).DataContext is not DeviceViewModel d) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(DeviceViewModel), d), DragDropEffects.Move);
        Vm.ClearDropHints();
    }

    void Handle_KeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DeviceViewModel d) return;
        if (e.Key == Key.Up) { d.MoveUpCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Down) { d.MoveDownCommand.Execute(null); e.Handled = true; }
    }

    (DeviceViewModel dragged, DeviceViewModel target, bool below)? DropInfo(object sender, DragEventArgs e)
    {
        var card = (FrameworkElement)sender;
        if (e.Data.GetData(typeof(DeviceViewModel)) is not DeviceViewModel dragged || card.DataContext is not DeviceViewModel target) return null;
        return (dragged, target, e.GetPosition(card).Y > card.ActualHeight / 2);
    }

    void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropInfo(sender, e) is { } i && Vm.DragOver(i.dragged, i.target, i.below) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Card_Drop(object sender, DragEventArgs e)
    {
        if (DropInfo(sender, e) is { } i) Vm.Drop(i.dragged, i.target, i.below);
        e.Handled = true;
    }

    /// <summary>Scroll while dragging near the top or bottom edge.</summary>
    void Scroller_DragOver(object sender, DragEventArgs e)
    {
        var y = e.GetPosition(Scroller).Y;
        const double edge = 48, step = 14;
        if (y < edge) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - step);
        else if (y > Scroller.ActualHeight - edge) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + step);
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


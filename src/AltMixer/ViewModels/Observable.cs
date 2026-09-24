using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AltMixer.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Command(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

static class CollectionSync
{
    /// <summary>Makes <paramref name="target"/> match <paramref name="source"/> by key, reusing existing view models and keeping order.</summary>
    public static void Sync<TSrc, TVm>(ObservableCollection<TVm> target, IReadOnlyList<TSrc> source, Func<TSrc, string> srcKey, Func<TVm, string> vmKey, Func<TSrc, TVm> create, Action<TVm, TSrc> update)
    {
        var existing = target.ToDictionary(vmKey);
        for (var i = 0; i < source.Count; i++)
        {
            var key = srcKey(source[i]);
            if (existing.Remove(key, out var vm))
            {
                var at = target.IndexOf(vm);
                if (at != i) target.Move(at, i);
            }
            else
            {
                vm = create(source[i]);
                target.Insert(i, vm);
            }
            update(vm, source[i]);
        }
        foreach (var gone in existing.Values) target.Remove(gone);
    }
}

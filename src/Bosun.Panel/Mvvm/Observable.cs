using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bosun.Panel.Mvvm;

/// <summary>The base of a view model: a property that changes says so, and one set to the value it has says nothing.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a property's field; true, with <see cref="PropertyChanged"/> raised, when the value changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Says a property changed whose value is worked out from others.</summary>
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

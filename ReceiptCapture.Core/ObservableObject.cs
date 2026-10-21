using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ReceiptCapture.Core;

/// <summary>
/// Base class for anything the UI binds to. When a property changes it raises
/// PropertyChanged, which is how WPF knows to refresh what's on screen.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

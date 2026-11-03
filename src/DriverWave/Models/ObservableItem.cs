using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DriverWave.Models;

/// <summary>Base for list rows that update the screen when their values change.</summary>
public abstract class ObservableItem : INotifyPropertyChanged
{
    private bool _selected;
    private string _status = "";

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

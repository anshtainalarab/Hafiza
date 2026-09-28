using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hafiza.Models;

public sealed class ClipboardTab : INotifyPropertyChanged
{
    private string _name = "تبويب جديد";
    private int _order;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public int Order
    {
        get => _order;
        set
        {
            if (_order == value) return;
            _order = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

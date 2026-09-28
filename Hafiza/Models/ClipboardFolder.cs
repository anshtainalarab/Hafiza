using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Hafiza.Models;

public sealed class ClipboardFolder : INotifyPropertyChanged
{
    private string _name = "فولدر جديد";
    private int _order;
    private bool _isPinned;
    private bool _isExpanded;
    private bool _isSelected;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TabId { get; set; } = string.Empty;

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

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

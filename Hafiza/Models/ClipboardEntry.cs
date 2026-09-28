using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Hafiza.Models;

public sealed class ClipboardEntry : INotifyPropertyChanged
{
    public const string AllTabPinScope = "__all__";
    private bool _isPinnedInActiveTab;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
    public string? Rtf { get; set; }
    public string? Html { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public string? ThumbnailPath { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public string Hash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool IsInAllTab { get; set; } = true;
    public List<string> TabIds { get; set; } = new();
    public List<string> PinnedTabIds { get; set; } = new();
    public Dictionary<string, string> FolderIdsByTab { get; set; } = new();
    public uint PasteHotKeyModifiers { get; set; }
    public uint PasteHotKeyVirtualKey { get; set; }

    // Reads the old, global pin value from existing history files during migration.
    [JsonPropertyName("IsPinned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyIsPinned { get; set; }

    [JsonIgnore]
    public bool IsPinnedInActiveTab => _isPinnedInActiveTab;

    [JsonIgnore]
    public bool IsPinnedAnywhere => PinnedTabIds.Count > 0;

    public bool IsPinnedInTab(string? tabId) =>
        PinnedTabIds.Contains(tabId ?? AllTabPinScope);

    public void SetActivePinTab(string? tabId)
    {
        var value = IsPinnedInTab(tabId);
        if (_isPinnedInActiveTab == value) return;
        _isPinnedInActiveTab = value;
        OnPropertyChanged(nameof(IsPinnedInActiveTab));
    }

    public void SetPinnedInTab(string? tabId, bool pinned)
    {
        var scope = tabId ?? AllTabPinScope;
        if (pinned)
        {
            if (!PinnedTabIds.Contains(scope)) PinnedTabIds.Add(scope);
        }
        else
        {
            PinnedTabIds.Remove(scope);
        }
        SetActivePinTab(tabId);
        OnPropertyChanged(nameof(IsPinnedAnywhere));
    }

    public string DisplayImagePath => ThumbnailPath ?? ImagePath ?? string.Empty;
    public bool IsImageMissing => IsImage && (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath));
    public string DisplayText => Kind == "files"
        ? string.Join(Environment.NewLine, FilePaths.Select(System.IO.Path.GetFileName))
        : Text;
    public bool IsText => Kind == "text";
    public bool IsImage => Kind == "image";
    public bool IsFiles => Kind == "files";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

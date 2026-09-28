using System.Collections.Generic;

namespace Hafiza.Models;

public sealed class AppState
{
    public List<ClipboardEntry> Entries { get; set; } = new();
    public List<ClipboardTab> Tabs { get; set; } = new();
    public List<ClipboardFolder> Folders { get; set; } = new();
    public int MaxEntries { get; set; } = 100;
    public string CardDensity { get; set; } = "compact";
    public string PopupPosition { get; set; } = "bottom-right";
    public bool StartWithWindows { get; set; }
    public bool ClearTemporaryOnExit { get; set; }
    public bool CleanAllTabAfterWindowsRestart { get; set; } = true;
    public string LastWindowsBootMarker { get; set; } = string.Empty;
    public string Theme { get; set; } = "dark";
    public string Language { get; set; } = "en";
    public bool AnimationsEnabled { get; set; } = true;
    public bool KeepWindowOpenAfterPaste { get; set; }
    public uint HotKeyModifiers { get; set; } = 6;
    public uint HotKeyVirtualKey { get; set; } = 0x56;
    public double WindowWidth { get; set; } = 610;
    public double WindowHeight { get; set; } = 735;
    public Dictionary<string, List<string>> TabEntryOrders { get; set; } = new();
    public Dictionary<string, List<string>> TabLibraryOrders { get; set; } = new();
    public List<DeletedBatch> UndoBatches { get; set; } = new();
}

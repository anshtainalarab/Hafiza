using System;
using System.Collections.Generic;

namespace Hafiza.Models;

public sealed class DeletedBatch
{
    public DateTime DeletedAt { get; set; } = DateTime.Now;
    public List<DeletedEntrySnapshot> Entries { get; set; } = new();
    public List<ClipboardFolder> DeletedFolders { get; set; } = new();
}

public sealed class DeletedEntrySnapshot
{
    public ClipboardEntry Entry { get; set; } = new();
    public int OriginalIndex { get; set; }
    public int OriginalLibraryIndex { get; set; } = -1;
    public string? TabId { get; set; }
    public string? FolderId { get; set; }
    public bool WasPinned { get; set; }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hafiza.Models;
using Hafiza.Services;

namespace Hafiza;

public partial class MainWindow
{
    private List<ClipboardFolder> FolderTargets(ClipboardFolder clicked)
    {
        var selected = VisibleFolders.Where(folder => folder.IsSelected).ToList();
        return selected.Contains(clicked) ? selected : new() { clicked };
    }

    private void FolderPin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder }) return;
        var pinned = !folder.IsPinned;
        foreach (var item in FolderTargets(folder)) item.IsPinned = pinned;
        SaveState();
        RefreshEntries();
    }

    private void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder }) return;
        foreach (var item in FolderTargets(folder).ToList())
        {
            if (!DeleteFolder(item)) break;
        }
    }

    private void FolderMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ClipboardFolder folder } button) return;
        OpenFolderManagementMenu(folder, button);
    }

    private void OpenFolderManagementMenu(ClipboardFolder folder, Button button)
    {
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        var rename = new MenuItem { Header = LocalizationService.Get("MenuRenameFolder") };
        rename.Click += (_, _) => RenameFolder(folder);
        menu.Items.Add(rename);

        var move = new MenuItem { Header = LocalizationService.Get("MenuMoveFolderToTab") };
        foreach (var tab in Tabs.Where(tab => tab.Id != folder.TabId))
        {
            var target = new MenuItem { Header = tab.Name };
            target.Click += (_, _) => MoveFolderToTab(folder, tab.Id);
            move.Items.Add(target);
        }
        if (move.Items.Count == 0) move.IsEnabled = false;
        menu.Items.Add(move);

        var moveUp = new MenuItem { Header = LocalizationService.Get("MenuMoveFolderUp") };
        moveUp.Click += (_, _) => MoveFoldersBy(FolderTargets(folder), -1);
        var moveDown = new MenuItem { Header = LocalizationService.Get("MenuMoveFolderDown") };
        moveDown.Click += (_, _) => MoveFoldersBy(FolderTargets(folder), 1);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);

        var first = new MenuItem { Header = LocalizationService.Get("MenuMoveFolderToTop") };
        first.Click += (_, _) => MoveFolderToEdge(folder, true);
        var last = new MenuItem { Header = LocalizationService.Get("MenuMoveFolderToBottom") };
        last.Click += (_, _) => MoveFolderToEdge(folder, false);
        menu.Items.Add(first);
        menu.Items.Add(last);
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = LocalizationService.Get("MenuDeleteFolder"), Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush") };
        delete.Click += (_, _) => DeleteFolder(folder);
        menu.Items.Add(delete);
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void MoveFolderToTab(ClipboardFolder folder, string destinationTabId)
    {
        var sourceTabId = folder.TabId;
        if (sourceTabId == destinationTabId) return;
        var contents = _entries.Where(entry => entry.FolderIdsByTab.TryGetValue(sourceTabId, out var id) && id == folder.Id).ToList();
        foreach (var entry in contents)
        {
            var wasPinned = entry.PinnedTabIds.Contains(sourceTabId);
            entry.FolderIdsByTab.Remove(sourceTabId);
            entry.TabIds.Remove(sourceTabId);
            entry.PinnedTabIds.Remove(sourceTabId);
            if (!entry.TabIds.Contains(destinationTabId)) entry.TabIds.Add(destinationTabId);
            if (wasPinned && !entry.PinnedTabIds.Contains(destinationTabId)) entry.PinnedTabIds.Add(destinationTabId);
            entry.FolderIdsByTab[destinationTabId] = folder.Id;
        }
        if (_tabEntryOrders.TryGetValue(sourceTabId, out var sourceEntryOrder))
        {
            foreach (var entry in contents) sourceEntryOrder.Remove(entry.Id);
        }
        folder.TabId = destinationTabId;
        if (_tabLibraryOrders.TryGetValue(sourceTabId, out var sourceOrder)) sourceOrder.Remove("folder:" + folder.Id);
        if (!_tabLibraryOrders.TryGetValue(destinationTabId, out var destinationOrder))
            _tabLibraryOrders[destinationTabId] = destinationOrder = new List<string>();
        destinationOrder.Remove("folder:" + folder.Id);
        destinationOrder.Insert(0, "folder:" + folder.Id);
        PlaceFolderAtEdge(folder, true);
        MoveEntriesToFront(contents, destinationTabId);
        SaveState();
        RefreshEntries();
        StatusText.Text = LocalizationService.Get("StatusFolderMoved", contents.Count);
    }

    private void MoveFolderToEdge(ClipboardFolder folder, bool first)
    {
        PlaceFolderAtEdge(folder, first);
        if (!_tabLibraryOrders.TryGetValue(folder.TabId, out var order))
            _tabLibraryOrders[folder.TabId] = order = new List<string>();
        var key = "folder:" + folder.Id;
        order.Remove(key);
        if (first) order.Insert(0, key); else order.Add(key);
        SyncFolderOrdersFromLibrary(folder.TabId, order);
        SyncTabEntryOrdersFromLibrary(folder.TabId, order);
        SaveState();
        RefreshEntries();
    }

    private void PlaceFolderAtEdge(ClipboardFolder folder, bool first)
    {
        var ordered = _folders.Where(item => item.TabId == folder.TabId && item != folder).OrderBy(item => item.Order).ToList();
        if (first) ordered.Insert(0, folder); else ordered.Add(folder);
        for (var index = 0; index < ordered.Count; index++) ordered[index].Order = index;
    }

    private void FolderDrag_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder } handle) return;
        _dragFolderCandidate = folder;
        _folderDragStart = e.GetPosition(this);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void FolderDrag_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragFolderCandidate is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _folderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _folderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var folder = _dragFolderCandidate;
        if (sender is UIElement handle) handle.ReleaseMouseCapture();
        _dragFolderCandidate = null;
        _suppressPasteUntil = DateTime.UtcNow.AddSeconds(1);
        StatusText.Text = LocalizationService.Get("StatusFolderDropHint", folder.Name);
        try
        {
            DragDrop.DoDragDrop(HistoryList, new DataObject(typeof(ClipboardFolder), folder), DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
            _suppressPasteUntil = DateTime.UtcNow.AddMilliseconds(600);
        }
        e.Handled = true;
    }

    private void FolderDrag_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is UIElement handle) handle.ReleaseMouseCapture();
        _dragFolderCandidate = null;
        e.Handled = true;
    }


    private void FolderExpand_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder }) return;
        folder.IsExpanded = !folder.IsExpanded;
        RefreshEntries();
    }

    private async void FolderCopy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder }) return;
        var entries = GetOrderedTabEntries(folder.TabId)
            .Where(entry => entry.FolderIdsByTab.TryGetValue(folder.TabId, out var id) && id == folder.Id).ToList();
        if (entries.Count == 0) { StatusText.Text = LocalizationService.Get("StatusFolderEmpty"); return; }
        try
        {
            await CopyEntriesToClipboardAsync(entries, LocalizationService.Get("StatusFolderContentsCopied", folder.Name));
        }
        catch (Exception error)
        {
            _storage.LogError(error, "FolderCopy_Click");
        }
    }

    private void ReturnCardToTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ClipboardEntry entry })
            AssignEntriesToFolder(GetActionEntries(entry), null);
    }

    private async void CopyCard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        var entries = GetActionEntries(entry);
        var msg = entries.Count == 1 ? LocalizationService.Get("StatusCopied") :
            entries.All(item => item.Kind == "text") ? LocalizationService.Get("StatusCopiedMultiple", entries.Count) :
            LocalizationService.Get("StatusCopiedSelected");
        try
        {
            await CopyEntriesToClipboardAsync(entries, msg);
        }
        catch (Exception error)
        {
            _storage.LogError(error, "CopyCard_Click");
        }
    }

    private async Task CopyEntriesToClipboardAsync(IReadOnlyCollection<ClipboardEntry> entries, string message)
    {
        if (entries.Count == 0) return;
        if (entries.Count == 1)
        {
            _ownClipboardHash = entries.First().Hash;
            _ownClipboardHashUntil = DateTime.UtcNow.AddSeconds(3);
        }
        else if (entries.All(item => item.Kind == "text"))
        {
            var combinedText = string.Join(Environment.NewLine + Environment.NewLine, entries.Select(item => item.Text));
            _ownClipboardHash = TextContentHash(combinedText);
            _ownClipboardHashUntil = DateTime.UtcNow.AddSeconds(3);
        }
        else
        {
            _ownClipboardHash = entries.First().Hash;
            _ownClipboardHashUntil = DateTime.UtcNow.AddSeconds(3);
        }

        _writingClipboard = true;
        try
        {
            await PutEntriesOnClipboardAsync(entries);
            StatusText.Visibility = Visibility.Visible;
            BulkActions.Visibility = Visibility.Collapsed;
            StatusText.Text = message;
        }
        catch (Exception error) { ReportError(error, "نسخ البطاقات المحددة"); }
        finally
        {
            _ownClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            _writingClipboard = false;
        }
    }


    private static async Task PutEntriesOnClipboardAsync(IReadOnlyCollection<ClipboardEntry> entries)
    {
        if (entries.Count == 1) { await PutEntryOnClipboardAsync(entries.First()); return; }
        var data = new DataObject();
        var texts = entries.Where(item => item.Kind == "text").Select(item => item.Text).ToList();
        var paths = entries.SelectMany(item => item.Kind == "files" ? item.FilePaths
            : item.Kind == "image" && item.ImagePath is not null ? new List<string> { item.ImagePath }
            : new List<string>()).Distinct().ToList();
        if (paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
            throw new FileNotFoundException("أحد الملفات أو الصور المحددة غير متاح.");
        if (texts.Count > 0) data.SetText(string.Join(Environment.NewLine + Environment.NewLine, texts));
        if (paths.Count > 0)
        {
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            data.SetFileDropList(files);
            data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 5, 0, 0, 0 }), false);
        }
        System.Windows.Clipboard.SetDataObject(data, true);
        System.Windows.Clipboard.Flush();
    }
}

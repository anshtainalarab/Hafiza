using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clowd.Clipboard;
using Hafiza.Models;
using Hafiza.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Hafiza;

public partial class MainWindow : Window
{
    private const int HotKeyId = 7319;
    private const int EntryHotKeyStart = 9000;
    private readonly StorageService _storage = new();
    private readonly AppState _state;
    private readonly List<ClipboardEntry> _entries;
    private HwndSource? _source;
    private IntPtr _foregroundHook;
    private readonly NativeMethods.WinEventDelegate _foregroundEventHandler;
    private IntPtr _previousWindow;
    private IntPtr _previousFocusWindow;
    private string? _ownClipboardHash;
    private DateTime _ownClipboardHashUntil = DateTime.MinValue;
    private bool _writingClipboard;
    private bool _pasteInProgress;
    private ClipboardFolder? _dragFolderCandidate;
    private Point _folderDragStart;
    private uint? _ownClipboardSequence;
    private bool _captureInProgress;
    private bool _capturePending;
    private bool _capturePaused;
    private bool _reallyExit;
    private Forms.NotifyIcon? _trayIcon;
    private ContextMenu? _trayMenu;
    private string? _selectedTabId;
    private Point _dragStartPoint;
    private ClipboardEntry? _dragCandidate;
    private ListBoxItem? _dropTargetContainer;
    private ScrollViewer? _historyScrollViewer;
    private DateTime _lastDragScrollAt = DateTime.MinValue;
    private DateTime _suppressPasteUntil = DateTime.MinValue;
    private readonly Dictionary<string, double> _scrollOffsets = new();
    private bool _selectionMode;
    private ClipboardTab? _dragTabCandidate;
    private Point _tabDragStart;
    private DateTime _suppressTabClickUntil = DateTime.MinValue;
    private int _clipboardRetryCount;
    private readonly Dictionary<string, List<string>> _tabEntryOrders;
    private readonly Dictionary<string, List<string>> _tabLibraryOrders;
    private readonly List<DeletedBatch> _undoBatches;
    private readonly List<ClipboardFolder> _folders;
    private readonly Dictionary<int, ClipboardEntry> _entryHotKeys = new();
    private string? _selectedFolderId;
    public static readonly DependencyProperty IsInsideFolderProperty = DependencyProperty.Register(
        nameof(IsInsideFolder), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsInsideFolder { get => (bool)GetValue(IsInsideFolderProperty); set => SetValue(IsInsideFolderProperty, value); }
    private readonly DispatcherTimer _windowSizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private ObservableCollection<ClipboardTab> _tabs = new();
    public ObservableCollection<ClipboardTab> Tabs
    {
        get => _tabs;
        init
        {
            _tabs = value;
            _tabs.CollectionChanged += (_, _) => UpdateTabBuckets();
            UpdateTabBuckets();
        }
    }
    public ObservableCollection<ClipboardTab> PrimaryTabs { get; } = new();
    public ObservableCollection<ClipboardTab> OverflowTabs { get; } = new();
    public ObservableCollection<ClipboardEntry> FilteredEntries { get; } = new();
    public ObservableCollection<object> VisibleItems { get; } = new();
    public ObservableCollection<ClipboardFolder> VisibleFolders { get; } = new();

    public MainWindow()
    {
        _foregroundEventHandler = ForegroundWindowChanged;
        _state = _storage.Load();
        var migratedExplorerUnsafeHotKey =
            _state.HotKeyModifiers == (NativeMethods.ModControl | NativeMethods.ModAlt) &&
            _state.HotKeyVirtualKey == NativeMethods.VkV;
        if (migratedExplorerUnsafeHotKey)
            _state.HotKeyModifiers = NativeMethods.ModControl | NativeMethods.ModShift;
        _entries = _state.Entries ?? new List<ClipboardEntry>();
        _tabEntryOrders = _state.TabEntryOrders ?? new Dictionary<string, List<string>>();
        _tabLibraryOrders = _state.TabLibraryOrders ?? new Dictionary<string, List<string>>();
        _undoBatches = _state.UndoBatches ?? new List<DeletedBatch>();
        _folders = _state.Folders ?? new List<ClipboardFolder>();
        Tabs = new ObservableCollection<ClipboardTab>((_state.Tabs ?? new List<ClipboardTab>()).OrderBy(t => t.Order));
        Tabs.CollectionChanged += (_, _) => UpdateTabBuckets();
        var migratedPins = MigratePinData();
        LocalizationService.ApplyLanguage(_state.Language ?? LocalizationService.English);
        ThemeService.ApplyTheme(_state.Theme ?? ThemeService.Dark);
        InitializeComponent();
        UpdateTabBuckets();
        FitWindowToWorkArea();
        UndoDeleteButton.Visibility = _undoBatches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateUndoButton();
        ErrorButton.Visibility = _storage.HasErrors ? Visibility.Visible : Visibility.Collapsed;
        RememberForegroundTarget();
        DataContext = this;
        try { CreateTrayIcon(); }
        catch (Exception error)
        {
            // A temporary Windows shell/SystemEvents failure must not prevent the
            // clipboard window, hotkeys, or paste engine from starting.
            _storage.LogError(error, "إنشاء أيقونة شريط المهام");
        }
        ApplySettings();
        KeepOpenButton.Tag = _state.KeepWindowOpenAfterPaste ? "selected" : null;
        UpdatePinnedWindowFrame();
        CleanAllTabAfterWindowsRestart();
        RefreshEntries();
        if (migratedExplorerUnsafeHotKey) SaveState();
        if (migratedPins)
        {
            // Commit the repaired, de-duplicated state to both history files,
            // then remove image files that no active or undo entry references.
            SaveState();
            SaveState();
            _storage.CleanupOrphanImages(_entries.Concat(
                _undoBatches.SelectMany(batch => batch.Entries.Select(snapshot => snapshot.Entry))));
        }

        Loaded += (_, _) => PositionPalette();
        _windowSizeSaveTimer.Tick += (_, _) =>
        {
            _windowSizeSaveTimer.Stop();
            SaveState();
        };
    }

    private void FitWindowToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        (MinWidth, Width) = FitDimension(_state.WindowWidth, MinWidth, area.Width, 24);
        (MinHeight, Height) = FitDimension(_state.WindowHeight, MinHeight, area.Height, 16);
    }

    private static (double minimum, double value) FitDimension(double preferred, double minimum, double available, double margin)
    {
        var maximum = Math.Max(1, available - margin);
        var fittedMinimum = Math.Min(minimum, maximum);
        return (fittedMinimum, Math.Clamp(preferred, fittedMinimum, maximum));
    }

    private bool MigratePinData()
    {
        var changed = false;
        foreach (var entry in _entries)
        {
            try
            {
                var oldThumbnail = entry.ThumbnailPath;
                _storage.EnsureThumbnail(entry);
                if (oldThumbnail != entry.ThumbnailPath) changed = true;
            }
            catch (Exception error)
            {
                _storage.LogError(error, "إنشاء معاينة مصغّرة لصورة قديمة");
            }

            if (entry.PinnedTabIds is null)
            {
                entry.PinnedTabIds = new List<string>();
                changed = true;
            }
            entry.FolderIdsByTab ??= new Dictionary<string, string>();

            if (!entry.LegacyIsPinned) continue;
            if (!entry.PinnedTabIds.Contains(ClipboardEntry.AllTabPinScope))
                entry.PinnedTabIds.Add(ClipboardEntry.AllTabPinScope);
            entry.LegacyIsPinned = false;
            changed = true;
        }

        var unique = new Dictionary<string, ClipboardEntry>(StringComparer.Ordinal);
        foreach (var entry in _entries.ToList())
        {
            try
            {
                var canonicalHash = entry.Hash;
                if (string.IsNullOrWhiteSpace(canonicalHash))
                {
                    canonicalHash = entry.Kind switch
                    {
                        "text" => TextContentHash(entry.Text),
                        "files" => FilesContentHash(entry.FilePaths),
                        "image" when !string.IsNullOrWhiteSpace(entry.ImagePath) && File.Exists(entry.ImagePath)
                            => ImageContentHash(LoadBitmap(entry.ImagePath)),
                        _ => entry.Hash
                    };
                    entry.Hash = canonicalHash;
                    changed = true;
                }

                if (!unique.TryGetValue(entry.Kind + ":" + canonicalHash, out var keeper))
                {
                    unique[entry.Kind + ":" + canonicalHash] = entry;
                    continue;
                }

                keeper.IsInAllTab |= entry.IsInAllTab;
                keeper.CreatedAt = keeper.CreatedAt >= entry.CreatedAt ? keeper.CreatedAt : entry.CreatedAt;
                foreach (var tabId in entry.TabIds)
                    if (!keeper.TabIds.Contains(tabId)) keeper.TabIds.Add(tabId);
                foreach (var tabId in entry.PinnedTabIds)
                    if (!keeper.PinnedTabIds.Contains(tabId)) keeper.PinnedTabIds.Add(tabId);
                foreach (var assignment in entry.FolderIdsByTab)
                    keeper.FolderIdsByTab.TryAdd(assignment.Key, assignment.Value);
                if (string.IsNullOrWhiteSpace(keeper.Label)) keeper.Label = entry.Label;
                if (string.IsNullOrWhiteSpace(keeper.Rtf)) keeper.Rtf = entry.Rtf;
                if (string.IsNullOrWhiteSpace(keeper.Html)) keeper.Html = entry.Html;
                if (keeper.PasteHotKeyVirtualKey == 0)
                {
                    keeper.PasteHotKeyModifiers = entry.PasteHotKeyModifiers;
                    keeper.PasteHotKeyVirtualKey = entry.PasteHotKeyVirtualKey;
                }
                _entries.Remove(entry);
                RemoveEntryFromOrders(entry);
                _storage.DeleteImage(entry);
                changed = true;
            }
            catch (Exception error)
            {
                _storage.LogError(error, "فحص العناصر المكررة القديمة");
            }
        }
        return changed;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WindowMessageHook);
        NativeMethods.AddClipboardFormatListener(handle);
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground,
            IntPtr.Zero, _foregroundEventHandler, 0, 0, NativeMethods.WineventOutOfContext);
        if (!NativeMethods.RegisterHotKey(handle, HotKeyId, _state.HotKeyModifiers, _state.HotKeyVirtualKey))
            StatusText.Text = $"تعذّر حجز {HotKeyFormatter.Format(_state.HotKeyModifiers, _state.HotKeyVirtualKey)} — افتح من أيقونة شريط المهام";
        RegisterEntryHotKeys();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest && WindowState == WindowState.Normal)
        {
            var screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var point = PointFromScreen(new Point(screenX, screenY));
            const double edge = 10;
            var left = point.X <= edge;
            var right = point.X >= ActualWidth - edge;
            var top = point.Y <= edge;
            var bottom = point.Y >= ActualHeight - edge;
            var hit = top && left ? NativeMethods.HtTopLeft
                : top && right ? NativeMethods.HtTopRight
                : bottom && left ? NativeMethods.HtBottomLeft
                : bottom && right ? NativeMethods.HtBottomRight
                : left ? NativeMethods.HtLeft
                : right ? NativeMethods.HtRight
                : top ? NativeMethods.HtTop
                : bottom ? NativeMethods.HtBottom
                : 0;
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }
        else if (message == NativeMethods.WmClipboardUpdate)
        {
            RememberForegroundTarget();
            if (!_capturePaused && !_writingClipboard &&
                _ownClipboardSequence != NativeMethods.GetClipboardSequenceNumber())
                _ = Dispatcher.BeginInvoke(CaptureClipboard, DispatcherPriority.Background);
        }
        else if (message == NativeMethods.WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            RememberForegroundTarget();
            TogglePalette(true);
            handled = true;
        }
        else if (message == NativeMethods.WmHotKey && _entryHotKeys.TryGetValue(wParam.ToInt32(), out var entry))
        {
            RememberForegroundTarget();
            _ = PasteEntryAsync(entry);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void CaptureClipboard()
    {
        if (_writingClipboard || _ownClipboardSequence == NativeMethods.GetClipboardSequenceNumber()) return;
        if (_captureInProgress)
        {
            _capturePending = true;
            return;
        }
        _captureInProgress = true;
        try
        {
            // Explorer also exposes thumbnails as bitmaps. Prefer the actual file
            // list so clicking the card can paste the image file into a folder.
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var paths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
                if (paths.Count > 0) AddFilesEntry(paths);
                _clipboardRetryCount = 0;
                return;
            }

            var clipboardImage = await ClipboardWpf.GetImageAsync();
            if (clipboardImage is not null)
            {
                _clipboardRetryCount = 0;
                await AddImageEntryAsync(clipboardImage);
                return;
            }

            if (System.Windows.Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText);
                var rtf = System.Windows.Clipboard.ContainsData(DataFormats.Rtf)
                    ? System.Windows.Clipboard.GetData(DataFormats.Rtf) as string : null;
                var html = System.Windows.Clipboard.ContainsData(DataFormats.Html)
                    ? System.Windows.Clipboard.GetData(DataFormats.Html) as string : null;
                if (!string.IsNullOrEmpty(text)) AddTextEntry(text, rtf, html);
                _clipboardRetryCount = 0;
            }
        }
        catch (COMException error)
        {
            if (++_clipboardRetryCount > 5)
            {
                _clipboardRetryCount = 0;
                ReportError(error, "الحافظة مشغولة بعد عدة محاولات");
                return;
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CaptureClipboard();
            };
            timer.Start();
        }
        catch (Exception error)
        {
            ReportError(error, "قراءة محتوى الحافظة");
        }
        finally
        {
            _captureInProgress = false;
            if (_capturePending)
            {
                _capturePending = false;
                _ = Dispatcher.BeginInvoke(CaptureClipboard, DispatcherPriority.Background);
            }
        }
    }

    private void AddTextEntry(string text, string? rtf = null, string? html = null)
    {
        var hash = TextContentHash(text);
        if (ShouldIgnoreOwnClipboard(hash)) return;
        var duplicate = _entries.FirstOrDefault(e => e.Hash == hash);
        if (duplicate is not null)
        {
            duplicate.CreatedAt = DateTime.Now;
            duplicate.IsInAllTab = true;
            duplicate.Rtf = rtf;
            duplicate.Html = html;
            MoveToFront(duplicate);
        }
        else
        {
            _entries.Insert(0, new ClipboardEntry
            {
                Kind = "text",
                Text = text,
                Rtf = rtf,
                Html = html,
                Hash = hash,
                CreatedAt = DateTime.Now
            });
        }
        CompleteCapture();
    }

    private void AddFilesEntry(List<string> paths)
    {
        var normalized = paths.Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var hash = FilesContentHash(normalized);
        if (ShouldIgnoreOwnClipboard(hash)) return;
        var duplicate = _entries.FirstOrDefault(e => e.Hash == hash);
        if (duplicate is not null)
        {
            duplicate.CreatedAt = DateTime.Now;
            duplicate.IsInAllTab = true;
            MoveToFront(duplicate);
        }
        else
        {
            _entries.Insert(0, new ClipboardEntry
            {
                Kind = "files",
                FilePaths = paths,
                Hash = hash,
                CreatedAt = DateTime.Now
            });
        }
        CompleteCapture();
    }

    private async Task AddImageEntryAsync(BitmapSource image)
    {
        // Clipboard bitmap decoders remain owned by the UI dispatcher even when
        // Freeze appears to succeed. Materialize the pixels before leaving it.
        var png = EncodePng(image);
        var hash = ImageContentHash(image);
        if (ShouldIgnoreOwnClipboard(hash)) return;
        var duplicate = _entries.FirstOrDefault(e => e.Hash == hash);
        if (duplicate is not null)
        {
            duplicate.CreatedAt = DateTime.Now;
            duplicate.IsInAllTab = true;
            MoveToFront(duplicate);
        }
        else
        {
            var entry = new ClipboardEntry { Kind = "image", Hash = hash, CreatedAt = DateTime.Now };
            // Only independent byte data is handled in the background. The entry
            // is not added until both files have been written successfully.
            var paths = await Task.Run(() => _storage.SaveImageFiles(png, entry.Id));
            entry.ImagePath = paths.original;
            entry.ThumbnailPath = paths.thumbnail;
            _entries.Insert(0, entry);
        }
        CompleteCapture();
    }

    private bool ShouldIgnoreOwnClipboard(string hash)
    {
        if (_ownClipboardHash is null) return false;
        if (DateTime.UtcNow <= _ownClipboardHashUntil)
            return string.Equals(hash, _ownClipboardHash, StringComparison.Ordinal);
        _ownClipboardHash = null;
        return false;
    }

    private void CompleteCapture()
    {
        TrimHistory();
        SaveState();
        RefreshEntries();
    }

    private void MoveToFront(ClipboardEntry entry)
    {
        _entries.Remove(entry);
        _entries.Insert(0, entry);
        MoveEntriesToFront(new[] { entry }, null);
    }

    private static string OrderScope(string? tabId) => tabId ?? ClipboardEntry.AllTabPinScope;

    private bool IsEntryInTab(ClipboardEntry entry, string? tabId) =>
        tabId is null ? entry.IsInAllTab : entry.TabIds.Contains(tabId);

    private List<ClipboardEntry> GetOrderedTabEntries(string? tabId)
    {
        var members = _entries.Where(entry => IsEntryInTab(entry, tabId)).ToList();
        var scope = OrderScope(tabId);
        if (!_tabEntryOrders.TryGetValue(scope, out var ids))
        {
            ids = members.Select(entry => entry.Id).ToList();
            _tabEntryOrders[scope] = ids;
            return members;
        }

        var memberIds = members.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        ids.RemoveAll(id => !memberIds.Contains(id));
        var known = ids.ToHashSet(StringComparer.Ordinal);
        var missing = members.Where(entry => !known.Contains(entry.Id)).ToList();
        if (missing.Count > 0) ids.InsertRange(0, missing.Select(entry => entry.Id));
        var byId = members.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private void SetTabOrder(string? tabId, IEnumerable<ClipboardEntry> entries) =>
        _tabEntryOrders[OrderScope(tabId)] = entries.Select(entry => entry.Id).Distinct().ToList();

    private void MoveEntriesToFront(IEnumerable<ClipboardEntry> entries, string? tabId)
    {
        var requested = entries.Where(entry => IsEntryInTab(entry, tabId)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) return;
        var current = GetOrderedTabEntries(tabId);
        var ordered = current.Where(entry => requested.Contains(entry.Id))
            .Concat(current.Where(entry => !requested.Contains(entry.Id)))
            .ToList();
        SetTabOrder(tabId, ordered);
    }

    private void RemoveEntryFromOrders(ClipboardEntry entry)
    {
        foreach (var ids in _tabEntryOrders.Values) ids.Remove(entry.Id);
    }

    private void TrimHistory()
    {
        while (_entries.Count > Math.Max(10, _state.MaxEntries))
        {
            var removable = _entries.LastOrDefault(e => !e.IsPinnedAnywhere && e.TabIds.Count == 0 &&
                string.IsNullOrWhiteSpace(e.Label) && e.PasteHotKeyVirtualKey == 0);
            if (removable is null) break;
            _entries.Remove(removable);
            RemoveEntryFromOrders(removable);
            _storage.DeleteImage(removable);
        }
    }

    private void RefreshEntries()
    {
        IsInsideFolder = _selectedFolderId is not null;
        foreach (var entry in _entries) entry.SetActivePinTab(_selectedTabId);

        var query = GetOrderedTabEntries(_selectedTabId).AsEnumerable();
        var search = SearchBox?.Text?.Trim();

        if (_selectedTabId is not null)
        {
            if (_selectedFolderId is not null)
                query = query.Where(entry => entry.FolderIdsByTab.TryGetValue(_selectedTabId, out var folderId) && folderId == _selectedFolderId);
            else if (string.IsNullOrWhiteSpace(search))
                query = query.Where(entry => !entry.FolderIdsByTab.ContainsKey(_selectedTabId));
        }

        VisibleFolders.Clear();
        if (_selectedTabId is not null && _selectedFolderId is null)
            foreach (var folder in _folders.Where(folder => folder.TabId == _selectedTabId).OrderBy(folder => folder.Order))
                VisibleFolders.Add(folder);
        UpdateFolderNavigation();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e =>
                e.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                (e.Kind == "text" && e.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)) ||
                (e.Kind == "files" && e.FilePaths.Any(path => Path.GetFileName(path).Contains(search, StringComparison.CurrentCultureIgnoreCase))));

        var selected = query.ToList();
        FilteredEntries.Clear();
        VisibleItems.Clear();
        if (_selectedTabId is not null && _selectedFolderId is null && string.IsNullOrWhiteSpace(search))
        {
            var folders = VisibleFolders.ToList();
            foreach (var key in GetLibraryOrder(_selectedTabId, selected, folders))
            {
                if (key.StartsWith("entry:", StringComparison.Ordinal))
                {
                    var entry = selected.FirstOrDefault(item => key == "entry:" + item.Id);
                    if (entry is not null) { VisibleItems.Add(entry); FilteredEntries.Add(entry); }
                    continue;
                }
                var folder = folders.FirstOrDefault(item => key == "folder:" + item.Id);
                if (folder is null) continue;
                VisibleItems.Add(folder);
                if (!folder.IsExpanded) continue;
                foreach (var child in GetOrderedTabEntries(_selectedTabId)
                    .Where(item => item.FolderIdsByTab.TryGetValue(_selectedTabId, out var id) && id == folder.Id))
                {
                    VisibleItems.Add(child);
                    FilteredEntries.Add(child);
                }
            }
        }
        else foreach (var entry in selected) { FilteredEntries.Add(entry); VisibleItems.Add(entry); }

        if (EmptyState is not null)
        {
            EmptyState.Visibility = VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(search))
            {
                EmptyTitle.Text = LocalizationService.Get("EmptySearchTitle");
                if (EmptySubtitle is not null) EmptySubtitle.Text = LocalizationService.Get("EmptySearchSubtitle");
            }
            else if (_selectedFolderId is not null)
            {
                EmptyTitle.Text = LocalizationService.IsArabic ? "هذا الفولدر فارغ" : "This folder is empty";
                if (EmptySubtitle is not null)
                    EmptySubtitle.Text = LocalizationService.IsArabic
                        ? "انقل بطاقات إلى هذا الفولدر عبر خيارات البطاقة ••• أو اسحبها فوقه."
                        : "Move items to this folder via item options ••• or drop them here.";
            }
            else if (_selectedTabId is null)
            {
                EmptyTitle.Text = LocalizationService.Get("EmptyTitle");
                if (EmptySubtitle is not null) EmptySubtitle.Text = LocalizationService.Get("EmptySubtitle");
            }
            else
            {
                EmptyTitle.Text = LocalizationService.IsArabic ? "لا توجد عناصر في هذا التبويب" : "No items in this tab";
                if (EmptySubtitle is not null)
                    EmptySubtitle.Text = LocalizationService.IsArabic
                        ? "أضف بطاقات لهذا التبويب من قائمة الخيارات ••• على أي بطاقة."
                        : "Add items to this tab from the options menu ••• on any card.";
            }
            ClearButton.Content = LocalizationService.Get("ClearUnpinned");
            StatusText.Text = _capturePaused
                ? LocalizationService.Get("StatusPaused")
                : (LocalizationService.IsArabic
                    ? $"{_entries.Count} عنصر محفوظ — الالتقاط يعمل"
                    : $"{_entries.Count} items saved — Capture active");
        }
    }

    private List<string> GetLibraryOrder(string tabId, IReadOnlyCollection<ClipboardEntry> entries, IReadOnlyCollection<ClipboardFolder> folders)
    {
        if (!_tabLibraryOrders.TryGetValue(tabId, out var order))
        {
            order = folders.OrderBy(folder => folder.Order).Select(folder => "folder:" + folder.Id)
                .Concat(entries.Select(entry => "entry:" + entry.Id)).ToList();
            _tabLibraryOrders[tabId] = order;
        }
        var valid = folders.Select(folder => "folder:" + folder.Id)
            .Concat(entries.Select(entry => "entry:" + entry.Id)).ToHashSet(StringComparer.Ordinal);
        order.RemoveAll(key => !valid.Contains(key));
        foreach (var key in valid.Where(key => !order.Contains(key, StringComparer.Ordinal))) order.Add(key);
        return order.ToList();
    }

    private void UpdateFolderNavigation()
    {
        if (FolderArea is null || FolderBreadcrumb is null) return;
        var folder = _folders.FirstOrDefault(item => item.Id == _selectedFolderId && item.TabId == _selectedTabId);
        if (_selectedFolderId is not null && folder is null) _selectedFolderId = null;
        folder = _folders.FirstOrDefault(item => item.Id == _selectedFolderId && item.TabId == _selectedTabId);
        FolderArea.Visibility = folder is not null ? Visibility.Visible : Visibility.Collapsed;
        FolderBreadcrumb.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        if (folder is not null) CurrentFolderName.Text = folder.Name;
    }

    private void SaveState()
    {
        _state.Entries = _entries;
        _state.TabEntryOrders = _tabEntryOrders;
        _state.TabLibraryOrders = _tabLibraryOrders;
        _state.UndoBatches = _undoBatches;
        _state.Folders = _folders;
        _state.Tabs = Tabs.Select((tab, index) =>
        {
            tab.Order = index;
            return tab;
        }).ToList();
        try
        {
            _storage.Save(_state);
        }
        catch (Exception error)
        {
            ReportError(error, "حفظ بيانات الحافظة");
        }
    }

    internal void ReportError(Exception error, string context)
    {
        _storage.LogError(error, context);
        if (ErrorButton is not null) ErrorButton.Visibility = Visibility.Visible;
        if (StatusText is not null) StatusText.Text = "حدثت مشكلة — يمكن عرض التفاصيل";
    }

    internal void ShowLoggedErrorNotice()
    {
        if (ErrorButton is not null) ErrorButton.Visibility = Visibility.Visible;
        if (StatusText is not null) StatusText.Text = "حدثت مشكلة — يمكن عرض التفاصيل";
    }

    private void ErrorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_storage.ErrorLogPath) { UseShellExecute = true });
            _storage.AcknowledgeErrors();
            ErrorButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            _storage.LogError(error, "فتح سجل الأخطاء");
        }
    }

    private Point? _stylusScrollStartPoint;
    private double _stylusScrollStartOffset;
    private bool _isStylusScrolling;

    private void HistoryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _historyScrollViewer ??= FindVisualChildren<ScrollViewer>(HistoryList).FirstOrDefault();
        if (_historyScrollViewer is null) return;
        var scrollDelta = -e.Delta * 0.75;
        var newOffset = Math.Clamp(_historyScrollViewer.VerticalOffset + scrollDelta, 0, _historyScrollViewer.ScrollableHeight);
        _historyScrollViewer.ScrollToVerticalOffset(newOffset);
        e.Handled = true;
    }

    private void HistoryList_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        _historyScrollViewer ??= FindVisualChildren<ScrollViewer>(HistoryList).FirstOrDefault();
        _stylusScrollStartPoint = e.GetPosition(HistoryList);
        _stylusScrollStartOffset = _historyScrollViewer?.VerticalOffset ?? 0;
        _isStylusScrolling = false;
    }

    private void HistoryList_PreviewStylusMove(object sender, StylusEventArgs e)
    {
        if (_stylusScrollStartPoint is null || _historyScrollViewer is null) return;
        var currentPoint = e.GetPosition(HistoryList);
        var deltaY = currentPoint.Y - _stylusScrollStartPoint.Value.Y;
        if (Math.Abs(deltaY) > 5 || _isStylusScrolling)
        {
            _isStylusScrolling = true;
            _suppressPasteUntil = DateTime.UtcNow.AddMilliseconds(500);
            var newOffset = Math.Clamp(_stylusScrollStartOffset - deltaY, 0, _historyScrollViewer.ScrollableHeight);
            _historyScrollViewer.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
        }
    }

    private void HistoryList_PreviewStylusUp(object sender, StylusEventArgs e)
    {
        if (_isStylusScrolling)
        {
            _suppressPasteUntil = DateTime.UtcNow.AddMilliseconds(500);
            e.Handled = true;
        }
        _stylusScrollStartPoint = null;
        _isStylusScrolling = false;
    }

    private async void HistoryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source is null || FindVisualParentOrSelf<Button>(source) is not null ||
            FindVisualParentOrSelf<CheckBox>(source) is not null || HasVisualAncestorWithTag(source, "drag-handle") ||
            HasVisualAncestorWithTag(source, "folder-drag-handle")) return;

        var item = FindVisualParentOrSelf<ListBoxItem>(source);
        if (item is null) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            e.Handled = true;
            return;
        }

        if (_isStylusScrolling || DateTime.UtcNow < _suppressPasteUntil)
        {
            e.Handled = true;
            return;
        }

        // Execute paste from the same preview event that reserves a plain card click.
        // Handling mouse-down and waiting for mouse-up used to swallow the later event
        // on no-activate windows, which made cards appear unresponsive.
        e.Handled = true;
        if (DateTime.UtcNow < _suppressPasteUntil) return;
        if (item.DataContext is ClipboardEntry entry)
            await PasteEntriesAsync(GetActionEntries(entry));
    }

    private void CardSelectionCheck_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UpdateSelectionUi();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionUi();

    private void UpdateSelectionUi()
    {
        if (HistoryList is null || BulkActions is null || StatusText is null || SelectionModeButton is null) return;
        _selectionMode = HistoryList.SelectedItems.Count > 0;
        BulkActions.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = _selectionMode ? Visibility.Collapsed : Visibility.Visible;
        SelectionModeButton.Tag = _selectionMode ? "selected" : null;
        SelectionModeButton.Content = _selectionMode ? "☑" : "☐";
        SelectionModeButton.ToolTip = _selectionMode
            ? (LocalizationService.IsArabic ? "إلغاء تحديد الكل" : "Deselect all")
            : (LocalizationService.IsArabic ? "حدّد من مربع البطاقة أو باستخدام Ctrl" : "Select from card checkbox or using Ctrl");
    }

    private void SelectionModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItems.OfType<ClipboardEntry>().Any()) HistoryList.UnselectAll();
        else
            foreach (var entry in FilteredEntries)
                HistoryList.SelectedItems.Add(entry);
        UpdateSelectionUi();
    }

    private List<ClipboardEntry> GetActionEntries(ClipboardEntry clicked)
    {
        var selected = FilteredEntries.Where(e => HistoryList.SelectedItems.Contains(e)).ToList();
        return selected.Count > 1 && selected.Contains(clicked)
            ? selected
            : new List<ClipboardEntry> { clicked };
    }

    private void BulkMove_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilteredEntries.Where(e => HistoryList.SelectedItems.Contains(e)).ToList();
        if (selected.Count == 0) return;
        var button = (Button)sender;
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        var all = new MenuItem { Header = LocalizationService.Get("AllTab") };
        all.Click += (_, _) => MoveSelectedToTab(selected, null);
        menu.Items.Add(all);
        foreach (var tab in Tabs)
        {
            var item = new MenuItem { Header = tab.Name };
            item.Click += (_, _) => MoveSelectedToTab(selected, tab.Id);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void MoveSelectedToTab(IReadOnlyCollection<ClipboardEntry> selected, string? destinationTabId)
    {
        if (selected.Count == 0 || destinationTabId == _selectedTabId) return;

        foreach (var entry in selected)
        {
            if (destinationTabId is null)
            {
                entry.IsInAllTab = true;
            }
            else if (!entry.TabIds.Contains(destinationTabId))
            {
                entry.TabIds.Add(destinationTabId);
            }

            if (_selectedTabId is null)
            {
                entry.IsInAllTab = false;
                entry.PinnedTabIds.Remove(ClipboardEntry.AllTabPinScope);
            }
            else
            {
                entry.TabIds.Remove(_selectedTabId);
                entry.PinnedTabIds.Remove(_selectedTabId);
                entry.FolderIdsByTab.Remove(_selectedTabId);
            }
        }

        MoveEntriesToFront(selected, destinationTabId);
        DeleteOrphanedEntries();
        SaveState();
        RefreshEntries();
    }

    private void DeleteEntryFromActiveTab(ClipboardEntry entry)
    {
        if (_selectedTabId is null)
        {
            entry.IsInAllTab = false;
            entry.PinnedTabIds.Remove(ClipboardEntry.AllTabPinScope);
        }
        else
        {
            entry.TabIds.Remove(_selectedTabId);
            entry.PinnedTabIds.Remove(_selectedTabId);
            entry.FolderIdsByTab.Remove(_selectedTabId);
        }
    }

    private async Task PasteEntryAsync(ClipboardEntry entry)
        => await PastePreparedAsync(entry, () => PutEntryOnClipboardAsync(entry), "لصق عنصر الحافظة",
            entry.Kind == "text");

    private async Task PasteEntriesAsync(IReadOnlyCollection<ClipboardEntry> entries)
    {
        if (entries.Count == 0) return;
        var anchor = entries.First();
        await PastePreparedAsync(anchor, () => PutEntriesOnClipboardAsync(entries),
            entries.Count > 1 ? "لصق العناصر المحددة" : "لصق عنصر الحافظة",
            entries.All(item => item.Kind == "text"));
    }

    private async Task PastePreparedAsync(ClipboardEntry entry, Func<Task> prepareClipboard, string context,
        bool preferDirectTextPaste)
    {
        if (_pasteInProgress) return;
        _pasteInProgress = true;
        try
        {
            var targetWindow = _previousWindow;
            var targetFocus = _previousFocusWindow;
            _ownClipboardHash = entry.Hash;
            _ownClipboardHashUntil = DateTime.UtcNow.AddSeconds(3);
            // Writing a saved entry back to Windows can raise several clipboard
            // notifications and can change its encoded bytes. Ignore only this
            // brief self-generated burst so pasting never reorders the history.
            _writingClipboard = true;
            try { await prepareClipboard(); }
            finally
            {
                _ownClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                _writingClipboard = false;
            }
            await RestoreTargetAndPasteAsync(targetWindow, targetFocus, preferDirectTextPaste);
        }
        catch (Exception error)
        {
            if (!IsVisible) ShowPalette();
            StatusText.Text = "تعذّر اللصق — تأكد من فتح نافذة يمكن الكتابة داخلها";
            ReportError(error, context);
        }
        finally
        {
            _pasteInProgress = false;
        }
    }

    private static async Task PutEntryOnClipboardAsync(ClipboardEntry entry)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (entry.Kind == "text")
                {
                    var data = new DataObject();
                    data.SetText(entry.Text);
                    if (!string.IsNullOrWhiteSpace(entry.Rtf)) data.SetData(DataFormats.Rtf, entry.Rtf);
                    if (!string.IsNullOrWhiteSpace(entry.Html)) data.SetData(DataFormats.Html, entry.Html);
                    System.Windows.Clipboard.SetDataObject(data, true);
                    System.Windows.Clipboard.Flush();
                    return;
                }

                if (entry.Kind == "files")
                {
                    var files = new StringCollection();
                    files.AddRange(entry.FilePaths.Where(File.Exists).Concat(entry.FilePaths.Where(Directory.Exists)).ToArray());
                    if (files.Count == 0) throw new FileNotFoundException("لم يعد الملف أو المجلد موجودًا.");
                    var data = new DataObject();
                    data.SetFileDropList(files);
                    data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 5, 0, 0, 0 }), false);
                    System.Windows.Clipboard.SetDataObject(data, true);
                    System.Windows.Clipboard.Flush();
                    return;
                }

                if (entry.ImagePath is not null && File.Exists(entry.ImagePath))
                {
                    SetCompatibleImageClipboard(LoadBitmap(entry.ImagePath));
                    return;
                }

                throw new FileNotFoundException("تعذّر العثور على ملف الصورة المحفوظة.", entry.ImagePath);
            }
            catch (COMException error)
            {
                lastError = error;
                await Task.Delay(60 + (attempt * 40));
            }
        }

        throw lastError ?? new InvalidOperationException("تعذّر الوصول إلى الحافظة.");
    }

    private static void SetCompatibleImageClipboard(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32)
        {
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            bitmap.Freeze();
        }
        var pngBytes = EncodePng(bitmap);
        var data = new DataObject();
        data.SetImage(bitmap);
        data.SetData(DataFormats.Bitmap, bitmap, true);
        data.SetData("PNG", new MemoryStream(pngBytes, writable: false), false);
        data.SetData("image/png", new MemoryStream(pngBytes, writable: false), false);
        System.Windows.Clipboard.SetDataObject(data, true);
        System.Windows.Clipboard.Flush();
    }

    private void RestorePreviousWindow(IntPtr targetWindow, IntPtr targetFocus)
    {
        if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow)) return;

        var currentThread = NativeMethods.GetCurrentThreadId();
        var focusThread = targetFocus != IntPtr.Zero && NativeMethods.IsWindow(targetFocus)
            ? NativeMethods.GetWindowThreadProcessId(targetFocus, out _)
            : 0;
        var targetThread = focusThread != 0
            ? focusThread
            : NativeMethods.GetWindowThreadProcessId(targetWindow, out _);
        var attached = targetThread != 0 && targetThread != currentThread &&
                       NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            if (NativeMethods.IsIconic(targetWindow)) NativeMethods.ShowWindow(targetWindow, 9);
            NativeMethods.BringWindowToTop(targetWindow);
            NativeMethods.SetForegroundWindow(targetWindow);
            if (targetFocus != IntPtr.Zero && NativeMethods.IsWindow(targetFocus))
            {
                var currentFocus = NativeMethods.GetFocus();
                if (currentFocus != targetFocus)
                    NativeMethods.SetFocus(targetFocus);
            }
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private async Task RestoreTargetAndPasteAsync(IntPtr targetWindow, IntPtr targetFocus,
        bool preferDirectTextPaste = false)
    {
        if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
            throw new InvalidOperationException("تعذّر العثور على نافذة البرنامج المطلوب اللصق داخله.");

        var isWhatsApp = IsWhatsAppWindow(targetWindow);
        // A card starts its action on mouse-down. Do not return focus to Explorer
        // until mouse-up, otherwise its rename/address editor can interpret that
        // release as an outside click and close the field before Ctrl+V arrives.
        await WaitForPrimaryButtonReleaseAsync();
        if (!_state.KeepWindowOpenAfterPaste) Hide();
        await Task.Delay(isWhatsApp ? 55 : 18);
        RestorePreviousWindow(targetWindow, targetFocus);

        // Most programs regain focus immediately. WebView-based apps such as
        // WhatsApp Desktop sometimes need one short focus confirmation cycle.
        for (var attempt = 0; attempt < 10 &&
             !BelongsToSameProcess(NativeMethods.GetForegroundWindow(), targetWindow); attempt++)
        {
            await Task.Delay(12);
            RestorePreviousWindow(targetWindow, targetFocus);
        }

        if (!BelongsToSameProcess(NativeMethods.GetForegroundWindow(), targetWindow))
            throw new InvalidOperationException("لم تستعد نافذة البرنامج السابق التركيز بعد.");

        await Task.Delay(isWhatsApp ? 45 : 16);
        SendPasteShortcut();

        if (_state.KeepWindowOpenAfterPaste)
        {
            Topmost = true;
            KeepOpenButton.Tag = "selected";
        }
    }

    private static bool IsEditableWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return false;
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(window, className, className.Capacity);
        var value = className.ToString();
        return value.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForPrimaryButtonReleaseAsync()
    {
        for (var attempt = 0; attempt < 50 &&
             (NativeMethods.GetAsyncKeyState(NativeMethods.VkLeftButton) & 0x8000) != 0; attempt++)
            await Task.Delay(10);
    }

    private static bool IsWhatsAppWindow(IntPtr window)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return false;
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool BelongsToSameProcess(IntPtr candidate, IntPtr target)
    {
        if (candidate == IntPtr.Zero || target == IntPtr.Zero) return false;
        if (candidate == target) return true;
        NativeMethods.GetWindowThreadProcessId(candidate, out var candidateProcess);
        NativeMethods.GetWindowThreadProcessId(target, out var targetProcess);
        return candidateProcess != 0 && candidateProcess == targetProcess;
    }

    private void ForegroundWindowChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime) => RememberForegroundTarget(hwnd);

    private void RememberForegroundTarget(IntPtr candidate = default)
    {
        var foreground = candidate != IntPtr.Zero ? candidate : NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || !NativeMethods.IsWindow(foreground)) return;

        var className = new StringBuilder(128);
        NativeMethods.GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow") return;

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId) return;

        _previousWindow = foreground;
        _previousFocusWindow = foreground;
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        // Passing zero asks Windows for the real foreground input queue. This is
        // essential on the desktop: Progman/WorkerW may be the foreground HWND,
        // while the file-name Edit control belongs to another Explorer thread.
        var infoThread = candidate == IntPtr.Zero ? 0u : threadId;
        if (NativeMethods.GetGUIThreadInfo(infoThread, ref info))
        {
            if (info.Active != IntPtr.Zero && NativeMethods.IsWindow(info.Active))
            {
                NativeMethods.GetWindowThreadProcessId(info.Active, out var activeProcess);
                if (activeProcess != (uint)Environment.ProcessId) _previousWindow = info.Active;
            }
            // hwndCaret is the actual editor for Explorer desktop rename and its
            // address bar, while hwndFocus can still be the surrounding list/view.
            if (IsEditableWindow(info.Caret)) _previousFocusWindow = info.Caret;
            else if (info.Focus != IntPtr.Zero) _previousFocusWindow = info.Focus;
            else if (info.Active != IntPtr.Zero) _previousFocusWindow = info.Active;
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        var pin = !entry.IsPinnedInTab(_selectedTabId);
        foreach (var target in GetActionEntries(entry)) target.SetPinnedInTab(_selectedTabId, pin);
        SaveState();
    }

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry } handle) return;
        _dragCandidate = entry;
        _dragStartPoint = e.GetPosition(this);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is UIElement handle) handle.ReleaseMouseCapture();
        var entry = _dragCandidate;
        _dragCandidate = null;
        _suppressPasteUntil = DateTime.UtcNow.AddSeconds(1);
        StatusText.Text = "أفلت البطاقة في مكانها الجديد";
        try
        {
            DragDrop.DoDragDrop(HistoryList, new DataObject(typeof(ClipboardEntry), entry), DragDropEffects.Move);
        }
        catch
        {
            StatusText.Text = "تعذّر نقل البطاقة — حاول سحبها مرة أخرى";
        }
        finally
        {
            ClearDropIndicator();
            _suppressPasteUntil = DateTime.UtcNow.AddMilliseconds(600);
        }
        e.Handled = true;
    }

    private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement handle) handle.ReleaseMouseCapture();
        _dragCandidate = null;
        e.Handled = true;
    }

    private void HistoryList_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(typeof(ClipboardEntry)) && !e.Data.GetDataPresent(typeof(ClipboardFolder)))
            {
                e.Effects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            e.Effects = DragDropEffects.Move;
            var targetContainer = GetDropTarget(e.OriginalSource as DependencyObject);
            if (targetContainer is not null)
            {
                var insertAfter = e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
                ShowDropIndicator(targetContainer, insertAfter);
            }
            else
            {
                ClearDropIndicator();
            }

            AutoScrollWhileDragging(e.GetPosition(HistoryList));
        }
        catch
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void HistoryList_Drop(object sender, DragEventArgs e)
    {
        _suppressPasteUntil = DateTime.UtcNow.AddMilliseconds(600);
        var originalOrder = GetOrderedTabEntries(_selectedTabId);
        try
        {
            if (_selectedTabId is not null && _selectedFolderId is null && TryReorderLibraryDrop(e)) return;
            var dragged = e.Data.GetData(typeof(ClipboardEntry)) as ClipboardEntry;
            if (dragged is null) return;
            var draggedEntries = HistoryList.SelectedItems.Contains(dragged)
                ? FilteredEntries.Where(entry => HistoryList.SelectedItems.Contains(entry)).ToList()
                : new List<ClipboardEntry> { dragged };
            if (draggedEntries.Count == 0 || draggedEntries.Any(entry => !_entries.Contains(entry))) return;

            var targetContainer = GetDropTarget(e.OriginalSource as DependencyObject);
            var target = targetContainer?.DataContext as ClipboardEntry;
            if (target is not null && draggedEntries.Contains(target)) return;

            var insertAfter = targetContainer is not null &&
                              e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
            var reordered = originalOrder.ToList();
            foreach (var entry in draggedEntries) reordered.Remove(entry);
            if (target is null)
            {
                reordered.AddRange(draggedEntries);
            }
            else
            {
                var targetIndex = reordered.IndexOf(target);
                if (targetIndex < 0) throw new InvalidOperationException("تعذّر تحديد موضع البطاقة الهدف.");
                var insertIndex = targetIndex + (insertAfter ? 1 : 0);
                reordered.InsertRange(Math.Clamp(insertIndex, 0, reordered.Count), draggedEntries);
            }

            SetTabOrder(_selectedTabId, reordered);
            SaveState();
            RefreshEntries();
            if (draggedEntries.Count > 1)
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var entry in draggedEntries)
                        if (FilteredEntries.Contains(entry)) HistoryList.SelectedItems.Add(entry);
                }, DispatcherPriority.Loaded);
            StatusText.Text = draggedEntries.Count > 1 ? "تم نقل العناصر المحددة معًا" : "تم حفظ ترتيب البطاقة";
            e.Effects = DragDropEffects.Move;
        }
        catch
        {
            SetTabOrder(_selectedTabId, originalOrder);
            RefreshEntries();
            StatusText.Text = "تعذّر نقل البطاقة — لم يتغير ترتيب العناصر";
        }
        finally
        {
            ClearDropIndicator();
            e.Handled = true;
        }
    }

    private bool TryReorderLibraryDrop(DragEventArgs e)
    {
        if (_selectedTabId is null) return false;
        var movingKeys = new List<string>();
        if (e.Data.GetData(typeof(ClipboardFolder)) is ClipboardFolder folder)
            movingKeys.Add("folder:" + folder.Id);
        else if (e.Data.GetData(typeof(ClipboardEntry)) is ClipboardEntry dragged &&
                 !dragged.FolderIdsByTab.ContainsKey(_selectedTabId))
        {
            var selected = HistoryList.SelectedItems.OfType<ClipboardEntry>()
                .Where(item => !item.FolderIdsByTab.ContainsKey(_selectedTabId)).ToList();
            if (!selected.Contains(dragged)) selected = new List<ClipboardEntry> { dragged };
            movingKeys.AddRange(selected.Select(item => "entry:" + item.Id));
        }
        else return false;

        var targetContainer = GetDropTarget(e.OriginalSource as DependencyObject);
        var targetKey = targetContainer?.DataContext switch
        {
            ClipboardFolder targetFolder => "folder:" + targetFolder.Id,
            ClipboardEntry targetEntry when !targetEntry.FolderIdsByTab.ContainsKey(_selectedTabId) => "entry:" + targetEntry.Id,
            ClipboardEntry targetEntry when targetEntry.FolderIdsByTab.TryGetValue(_selectedTabId, out var parentFolderId)
                => "folder:" + parentFolderId,
            _ => null
        };
        var roots = GetOrderedTabEntries(_selectedTabId).Where(item => !item.FolderIdsByTab.ContainsKey(_selectedTabId)).ToList();
        var folders = _folders.Where(item => item.TabId == _selectedTabId).ToList();
        var order = GetLibraryOrder(_selectedTabId, roots, folders);
        order.RemoveAll(movingKeys.Contains);
        var index = targetKey is null ? order.Count : order.IndexOf(targetKey);
        if (index < 0) index = order.Count;
        else if (targetContainer is not null && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2) index++;
        order.InsertRange(Math.Clamp(index, 0, order.Count), movingKeys);
        _tabLibraryOrders[_selectedTabId] = order;
        SyncFolderOrdersFromLibrary(_selectedTabId, order);
        SyncTabEntryOrdersFromLibrary(_selectedTabId, order);
        SaveState();
        RefreshEntries();
        StatusText.Text = movingKeys.Count > 1 ? "تم حفظ ترتيب العناصر المحددة" : "تم حفظ ترتيب القائمة";
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        return true;
    }

    private void SyncTabEntryOrdersFromLibrary(string tabId, IReadOnlyList<string> libraryOrder)
    {
        var entryKeys = libraryOrder.Where(key => key.StartsWith("entry:", StringComparison.Ordinal))
            .Select(key => key.Substring(6)).ToList();
        if (entryKeys.Count == 0) return;
        var rootIdSet = entryKeys.ToHashSet(StringComparer.Ordinal);
        var current = GetOrderedTabEntries(tabId);
        var nonRoots = current.Where(entry => !rootIdSet.Contains(entry.Id)).ToList();
        var byId = current.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var ordered = entryKeys.Where(byId.ContainsKey).Select(id => byId[id])
            .Concat(nonRoots).ToList();
        SetTabOrder(tabId, ordered);
    }

    private void SyncFolderOrdersFromLibrary(string tabId, IReadOnlyList<string> libraryOrder)
    {
        var folderKeys = libraryOrder.Where(key => key.StartsWith("folder:", StringComparison.Ordinal)).ToList();
        foreach (var folder in _folders.Where(item => item.TabId == tabId))
        {
            var index = folderKeys.IndexOf("folder:" + folder.Id);
            folder.Order = index >= 0 ? index : folderKeys.Count;
        }
    }

    private ListBoxItem? GetDropTarget(DependencyObject? source)
    {
        if (source is null) return null;
        try
        {
            return ItemsControl.ContainerFromElement(HistoryList, source) as ListBoxItem;
        }
        catch
        {
            return null;
        }
    }

    private void ShowDropIndicator(ListBoxItem target, bool insertAfter)
    {
        if (_dropTargetContainer != target) ClearDropIndicator();
        _dropTargetContainer = target;
        target.Tag = insertAfter ? "drop-after" : "drop-before";
    }

    private void ClearDropIndicator()
    {
        if (_dropTargetContainer is not null) _dropTargetContainer.Tag = null;
        _dropTargetContainer = null;
    }

    private void AutoScrollWhileDragging(Point pointer)
    {
        if (DateTime.UtcNow - _lastDragScrollAt < TimeSpan.FromMilliseconds(75)) return;
        _historyScrollViewer ??= FindVisualChildren<ScrollViewer>(HistoryList).FirstOrDefault();
        if (_historyScrollViewer is null) return;

        const double edge = 42;
        if (pointer.Y < edge)
        {
            _historyScrollViewer.LineUp();
            _lastDragScrollAt = DateTime.UtcNow;
        }
        else if (pointer.Y > HistoryList.ActualHeight - edge)
        {
            _historyScrollViewer.LineDown();
            _lastDragScrollAt = DateTime.UtcNow;
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not ClipboardEntry entry) return;
        var targets = GetActionEntries(entry);
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        menu.Items.Add(new MenuItem { Header = LocalizationService.Get("MenuAddToTab"), IsEnabled = false });

        var allTabItem = new MenuItem { Header = LocalizationService.Get("AllTab"), IsCheckable = true, IsChecked = entry.IsInAllTab };
        allTabItem.Click += (_, _) =>
        {
            foreach (var target in targets)
            {
                target.IsInAllTab = allTabItem.IsChecked;
                if (!allTabItem.IsChecked)
                    target.PinnedTabIds.Remove(ClipboardEntry.AllTabPinScope);
            }
            if (allTabItem.IsChecked) MoveEntriesToFront(targets, null);
            DeleteOrphanedEntries();
            SaveState();
            RefreshEntries();
        };
        menu.Items.Add(allTabItem);

        if (Tabs.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = LocalizationService.Get("MenuNoTabsYet"), IsEnabled = false });
        }
        else
        {
            foreach (var tab in Tabs)
            {
                var tabItem = new MenuItem { Header = tab.Name, IsCheckable = true, IsChecked = entry.TabIds.Contains(tab.Id) };
                tabItem.Click += (_, _) =>
                {
                    foreach (var target in targets)
                    {
                        if (tabItem.IsChecked && !target.TabIds.Contains(tab.Id)) target.TabIds.Add(tab.Id);
                        if (!tabItem.IsChecked)
                        {
                            target.TabIds.Remove(tab.Id);
                            target.PinnedTabIds.Remove(tab.Id);
                            target.FolderIdsByTab.Remove(tab.Id);
                        }
                    }
                    if (tabItem.IsChecked) MoveEntriesToFront(targets, tab.Id);
                    DeleteOrphanedEntries();
                    SaveState();
                    RefreshEntries();
                };
                menu.Items.Add(tabItem);
            }
        }

        if (entry.Kind == "text")
        {
            var plainPaste = new MenuItem { Header = LocalizationService.Get("MenuPastePlainText") };
            plainPaste.Click += async (_, _) => await PastePlainTextAsync(entry);
            menu.Items.Add(new Separator());
            menu.Items.Add(plainPaste);
        }

        if (entry.Kind == "image")
        {
            var preview = new MenuItem { Header = LocalizationService.Get("MenuShowImagePreview") };
            preview.Click += (_, _) => ShowImagePreview(entry);
            menu.Items.Add(new Separator());
            menu.Items.Add(preview);
        }

        var shortcutText = entry.PasteHotKeyVirtualKey == 0
            ? LocalizationService.Get("MenuSetShortcut")
            : LocalizationService.Get("MenuShortcutFormat", HotKeyFormatter.Format(entry.PasteHotKeyModifiers, entry.PasteHotKeyVirtualKey));
        var shortcut = new MenuItem { Header = shortcutText };
        shortcut.Click += (_, _) => EditEntryHotKey(entry);
        menu.Items.Add(new Separator());
        menu.Items.Add(shortcut);

        if (_selectedTabId is not null)
        {
            var folderMenu = new MenuItem { Header = targets.Count > 1 ? LocalizationService.Get("MenuMoveSelectedToFolder") : LocalizationService.Get("MenuMoveToFolder") };
            var noFolder = new MenuItem { Header = LocalizationService.Get("MenuOutsideFolders") };
            noFolder.Click += (_, _) => AssignEntriesToFolder(targets, null);
            folderMenu.Items.Add(noFolder);
            foreach (var folder in _folders.Where(folder => folder.TabId == _selectedTabId).OrderBy(folder => folder.Order))
            {
                var folderItem = new MenuItem { Header = folder.Name };
                folderItem.Click += (_, _) => AssignEntriesToFolder(targets, folder.Id);
                folderMenu.Items.Add(folderItem);
            }
            folderMenu.Items.Add(new Separator());
            var newFolder = new MenuItem { Header = LocalizationService.Get("MenuNewFolderEllipsis") };
            newFolder.Click += (_, _) => CreateFolder(targets);
            folderMenu.Items.Add(newFolder);
            menu.Items.Add(folderMenu);
        }

        var label = new MenuItem { Header = targets.Count > 1 ? LocalizationService.Get("MenuRenameSelectedCards") : LocalizationService.Get("MenuRenameCard") };
        label.Click += (_, _) => RenameEntries(targets, entry.Label);
        var effectiveFolderId = _selectedFolderId;
        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var selectedFolders = targets.Select(item => item.FolderIdsByTab.TryGetValue(_selectedTabId, out var id) ? id : null)
                .Distinct(StringComparer.Ordinal).ToList();
            if (selectedFolders.Count == 1) effectiveFolderId = selectedFolders[0];
        }

        bool canMoveUp;
        bool canMoveDown;

        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var roots = GetOrderedTabEntries(_selectedTabId).Where(item => !item.FolderIdsByTab.ContainsKey(_selectedTabId)).ToList();
            var folders = _folders.Where(item => item.TabId == _selectedTabId).ToList();
            var order = GetLibraryOrder(_selectedTabId, roots, folders);
            var index = order.IndexOf("entry:" + entry.Id);
            canMoveUp = index > 0;
            canMoveDown = index >= 0 && index < order.Count - 1;
        }
        else
        {
            var tabOrder = GetOrderedTabEntries(_selectedTabId);
            var siblings = effectiveFolderId is null
                ? tabOrder
                : tabOrder.Where(item => item.FolderIdsByTab.TryGetValue(_selectedTabId!, out var fid) && fid == effectiveFolderId).ToList();
            var index = siblings.IndexOf(entry);
            canMoveUp = index > 0;
            canMoveDown = index >= 0 && index < siblings.Count - 1;
        }

        var moveUp = new MenuItem { Header = LocalizationService.Get("MenuMoveToTop"), IsEnabled = canMoveUp };
        moveUp.Click += (_, _) => MoveEntriesToEdge(targets, true);
        var moveDown = new MenuItem { Header = LocalizationService.Get("MenuMoveToBottom"), IsEnabled = canMoveDown };
        moveDown.Click += (_, _) => MoveEntriesToEdge(targets, false);
        menu.Items.Add(new Separator());
        menu.Items.Add(label);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async Task PastePlainTextAsync(ClipboardEntry entry)
    {
        await PastePreparedAsync(entry, () =>
        {
            System.Windows.Clipboard.SetText(entry.Text);
            System.Windows.Clipboard.Flush();
            return Task.CompletedTask;
        }, "لصق كنص عادي", true);
    }

    private void RenameEntries(IReadOnlyCollection<ClipboardEntry> entries, string currentLabel)
    {
        var dialog = new TabNameDialog(currentLabel, "اسم العناصر (اتركه فارغًا لإزالة الاسم)", true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var entry in entries) entry.Label = dialog.TabName;
        SaveState();
        RefreshEntries();
    }

    private void ShowImagePreview(ClipboardEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ImagePath) || !File.Exists(entry.ImagePath)) return;
        var image = new Image { Source = LoadBitmap(entry.ImagePath), Stretch = Stretch.Uniform, Margin = new Thickness(18) };
        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(entry.Label) ? LocalizationService.Get("ImagePreviewTitle") : entry.Label,
            Owner = this, Width = 900, Height = 650, Background = (Brush)FindResource("PanelBrush"),
            Content = image, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.ShowDialog();
    }

    private void MoveEntriesToEdge(IReadOnlyCollection<ClipboardEntry> entries, bool toTop)
    {
        if (entries.Count == 0) return;
        var selected = entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var effectiveFolderId = _selectedFolderId;
        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var selectedFolders = entries.Select(item => item.FolderIdsByTab.TryGetValue(_selectedTabId, out var id) ? id : null)
                .Distinct(StringComparer.Ordinal).ToList();
            if (selectedFolders.Count == 1) effectiveFolderId = selectedFolders[0];
        }

        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var roots = GetOrderedTabEntries(_selectedTabId)
                .Where(item => !item.FolderIdsByTab.ContainsKey(_selectedTabId)).ToList();
            var folders = _folders.Where(item => item.TabId == _selectedTabId).ToList();
            var order = GetLibraryOrder(_selectedTabId, roots, folders);
            var movingKeys = order.Where(key => key.StartsWith("entry:", StringComparison.Ordinal) && selected.Contains(key[6..])).ToList();
            if (movingKeys.Count == 0)
                movingKeys = entries.Select(item => "entry:" + item.Id).ToList();
            order.RemoveAll(movingKeys.Contains);
            if (toTop) order.InsertRange(0, movingKeys);
            else order.AddRange(movingKeys);
            _tabLibraryOrders[_selectedTabId] = order;
            SyncFolderOrdersFromLibrary(_selectedTabId, order);
            SyncTabEntryOrdersFromLibrary(_selectedTabId, order);
        }
        else
        {
            var reordered = GetOrderedTabEntries(_selectedTabId);
            var positions = reordered.Select((item, index) => (item, index))
                .Where(pair => effectiveFolderId is null ||
                    pair.item.FolderIdsByTab.TryGetValue(_selectedTabId!, out var folderId) && folderId == effectiveFolderId)
                .Select(pair => pair.index).ToList();
            var visibleOrder = positions.Select(index => reordered[index]).ToList();
            var moving = visibleOrder.Where(item => selected.Contains(item.Id)).ToList();
            if (moving.Count == 0) moving = entries.Where(item => visibleOrder.Any(v => v.Id == item.Id)).ToList();
            var remaining = visibleOrder.Where(item => !selected.Contains(item.Id)).ToList();
            var newVisibleOrder = toTop ? moving.Concat(remaining).ToList() : remaining.Concat(moving).ToList();
            for (var i = 0; i < positions.Count; i++) reordered[positions[i]] = newVisibleOrder[i];
            SetTabOrder(_selectedTabId, reordered);
        }

        SaveState();
        RefreshEntries();
        StatusText.Text = entries.Count > 1
            ? (effectiveFolderId is not null
                ? (toTop ? LocalizationService.Get("StatusMovedToFolderTopPlural") : LocalizationService.Get("StatusMovedToFolderBottomPlural"))
                : (toTop ? LocalizationService.Get("StatusMovedToTopPlural") : LocalizationService.Get("StatusMovedToBottomPlural")))
            : (effectiveFolderId is not null
                ? (toTop ? LocalizationService.Get("StatusMovedToFolderTop") : LocalizationService.Get("StatusMovedToFolderBottom"))
                : (toTop ? LocalizationService.Get("StatusMovedToTop") : LocalizationService.Get("StatusMovedToBottom")));
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in entries)
                if (FilteredEntries.Contains(item)) HistoryList.SelectedItems.Add(item);
        }, DispatcherPriority.Loaded);
    }

    private void MoveEntriesBy(IReadOnlyCollection<ClipboardEntry> entries, int direction)
    {
        var selected = entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var effectiveFolderId = _selectedFolderId;
        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var selectedFolders = entries.Select(item => item.FolderIdsByTab.TryGetValue(_selectedTabId, out var id) ? id : null)
                .Distinct(StringComparer.Ordinal).ToList();
            if (selectedFolders.Count == 1) effectiveFolderId = selectedFolders[0];
        }
        if (_selectedTabId is not null && effectiveFolderId is null)
        {
            var roots = GetOrderedTabEntries(_selectedTabId)
                .Where(item => !item.FolderIdsByTab.ContainsKey(_selectedTabId)).ToList();
            var folders = _folders.Where(item => item.TabId == _selectedTabId).ToList();
            var order = GetLibraryOrder(_selectedTabId, roots, folders);
            MoveKeysOneStep(order, selected.Select(id => "entry:" + id).ToHashSet(StringComparer.Ordinal), direction);
            _tabLibraryOrders[_selectedTabId] = order;
            SyncTabEntryOrdersFromLibrary(_selectedTabId, order);
        }
        else
        {
            var reordered = GetOrderedTabEntries(_selectedTabId);
            var positions = reordered.Select((item, index) => (item, index))
                .Where(pair => effectiveFolderId is null ||
                    pair.item.FolderIdsByTab.TryGetValue(_selectedTabId!, out var folderId) && folderId == effectiveFolderId)
                .Select(pair => pair.index).ToList();
            var visibleOrder = positions.Select(index => reordered[index]).ToList();
            var ids = visibleOrder.Select(item => item.Id).ToList();
            MoveKeysOneStep(ids, selected, direction);
            var byId = visibleOrder.ToDictionary(item => item.Id, StringComparer.Ordinal);
            for (var i = 0; i < positions.Count; i++) reordered[positions[i]] = byId[ids[i]];
            SetTabOrder(_selectedTabId, reordered);
        }
        SaveState();
        RefreshEntries();
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in entries)
                if (FilteredEntries.Contains(item)) HistoryList.SelectedItems.Add(item);
        }, DispatcherPriority.Loaded);
    }

    private void MoveFoldersBy(IReadOnlyCollection<ClipboardFolder> folders, int direction)
    {
        if (folders.Count == 0) return;
        var tabId = folders.First().TabId;
        var selectedKeys = folders.Select(f => "folder:" + f.Id).ToHashSet(StringComparer.Ordinal);
        var roots = GetOrderedTabEntries(tabId)
            .Where(item => !item.FolderIdsByTab.ContainsKey(tabId)).ToList();
        var allFolders = _folders.Where(item => item.TabId == tabId).ToList();
        var order = GetLibraryOrder(tabId, roots, allFolders);
        MoveKeysOneStep(order, selectedKeys, direction);
        _tabLibraryOrders[tabId] = order;
        SyncFolderOrdersFromLibrary(tabId, order);
        SyncTabEntryOrdersFromLibrary(tabId, order);
        SaveState();
        RefreshEntries();
    }

    private static void MoveKeysOneStep(List<string> order, HashSet<string> selected, int direction)
    {
        if (direction < 0)
        {
            for (var i = 1; i < order.Count; i++)
                if (selected.Contains(order[i]) && !selected.Contains(order[i - 1]))
                    (order[i - 1], order[i]) = (order[i], order[i - 1]);
        }
        else
        {
            for (var i = order.Count - 2; i >= 0; i--)
                if (selected.Contains(order[i]) && !selected.Contains(order[i + 1]))
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
        }
    }

    private void EditEntryHotKey(ClipboardEntry entry)
    {
        var dialog = new HotKeyDialog(entry.PasteHotKeyModifiers, entry.PasteHotKeyVirtualKey) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var oldModifiers = entry.PasteHotKeyModifiers;
        var oldVirtualKey = entry.PasteHotKeyVirtualKey;
        entry.PasteHotKeyModifiers = dialog.Modifiers;
        entry.PasteHotKeyVirtualKey = dialog.VirtualKey;
        RegisterEntryHotKeys();
        if (dialog.VirtualKey != 0 && !_entryHotKeys.Values.Contains(entry))
        {
            entry.PasteHotKeyModifiers = oldModifiers;
            entry.PasteHotKeyVirtualKey = oldVirtualKey;
            RegisterEntryHotKeys();
            StatusText.Text = "الاختصار مستخدم بالفعل؛ لم يتغير اختصار البطاقة";
            return;
        }
        SaveState();
        StatusText.Text = dialog.VirtualKey == 0 ? "تمت إزالة اختصار البطاقة" : "تم حفظ اختصار لصق البطاقة";
    }

    private void AssignEntriesToFolder(IReadOnlyCollection<ClipboardEntry> entries, string? folderId)
    {
        if (_selectedTabId is null) return;
        foreach (var entry in entries)
        {
            if (folderId is null) entry.FolderIdsByTab.Remove(_selectedTabId);
            else entry.FolderIdsByTab[_selectedTabId] = folderId;
        }
        if (!_tabLibraryOrders.TryGetValue(_selectedTabId, out var libraryOrder))
            _tabLibraryOrders[_selectedTabId] = libraryOrder = new List<string>();
        foreach (var entry in entries) libraryOrder.Remove("entry:" + entry.Id);
        if (folderId is null) libraryOrder.InsertRange(0, entries.Select(entry => "entry:" + entry.Id));
        SaveState();
        RefreshEntries();
    }

    private void CreateFolder(IReadOnlyCollection<ClipboardEntry>? entries = null)
    {
        if (_selectedTabId is null) return;
        var dialog = new TabNameDialog("", "اسم الفولدر") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var folder = new ClipboardFolder
        {
            TabId = _selectedTabId,
            Name = dialog.TabName,
            Order = _folders.Count(item => item.TabId == _selectedTabId)
        };
        _folders.Add(folder);
        if (entries is not null) AssignEntriesToFolder(entries, folder.Id);
        SaveState();
        RefreshEntries();
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e) => CreateFolder();

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardFolder folder }) return;
        _selectedFolderId = folder.Id;
        RefreshEntries();
        e.Handled = true;
    }

    private void FolderBack_Click(object sender, RoutedEventArgs e)
    {
        _selectedFolderId = null;
        RefreshEntries();
    }

    private void Folder_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ClipboardFolder))) return;
        e.Effects = e.Data.GetDataPresent(typeof(ClipboardEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Folder_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ClipboardFolder))) return;
        if (_selectedTabId is null || sender is not FrameworkElement { DataContext: ClipboardFolder folder } ||
            e.Data.GetData(typeof(ClipboardEntry)) is not ClipboardEntry dragged) return;
        var entries = HistoryList.SelectedItems.Contains(dragged)
            ? FilteredEntries.Where(entry => HistoryList.SelectedItems.Contains(entry)).ToList()
            : new List<ClipboardEntry> { dragged };
        AssignEntriesToFolder(entries, folder.Id);
        StatusText.Text = entries.Count > 1 ? $"تم نقل {entries.Count} عناصر إلى {folder.Name}" : $"تم النقل إلى {folder.Name}";
        e.Handled = true;
    }

    private void FolderOptions_Click(object sender, RoutedEventArgs e)
    {
        var folder = _folders.FirstOrDefault(folder => folder.Id == _selectedFolderId);
        if (folder is null) return;
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        var rename = new MenuItem { Header = LocalizationService.Get("MenuRenameFolder") };
        rename.Click += (_, _) => RenameFolder(folder);
        var delete = new MenuItem { Header = LocalizationService.Get("MenuDeleteFolder"), Foreground = (Brush)FindResource("DangerBrush") };
        delete.Click += (_, _) => DeleteFolder(folder);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        if (sender is Button button)
        {
            button.ContextMenu = menu;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void RenameFolder(ClipboardFolder folder)
    {
        var dialog = new TabNameDialog(folder.Name, LocalizationService.Get("DialogFolderNameTitle")) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        folder.Name = dialog.TabName;
        SaveState();
        RefreshEntries();
    }

    private bool DeleteFolder(ClipboardFolder folder)
    {
        if (_selectedTabId is null) return false;
        var contents = _entries.Where(entry => entry.FolderIdsByTab.TryGetValue(_selectedTabId, out var id) && id == folder.Id).ToList();
        var choice = System.Windows.MessageBox.Show(
            LocalizationService.Get("DeleteFolderPrompt"),
            LocalizationService.Get("DeleteFolderTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.No && contents.Count >= 10 && System.Windows.MessageBox.Show(
            LocalizationService.Get("DeleteCardsCountPrompt", contents.Count), LocalizationService.Get("DeleteConfirmTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
        if (choice == MessageBoxResult.Yes)
        {
            foreach (var entry in contents) entry.FolderIdsByTab.Remove(folder.TabId);
            if (!_tabLibraryOrders.TryGetValue(folder.TabId, out var order))
                _tabLibraryOrders[folder.TabId] = order = new List<string>();
            order.InsertRange(0, contents.Select(entry => "entry:" + entry.Id));
            SyncTabEntryOrdersFromLibrary(folder.TabId, order);
        }
        else
        {
            DeleteEntries(contents, false);
            if (_undoBatches.Count > 0)
                _undoBatches[^1].DeletedFolders.Add(folder);
        }
        _folders.Remove(folder);
        if (_tabLibraryOrders.TryGetValue(folder.TabId, out var libraryOrder))
            libraryOrder.Remove("folder:" + folder.Id);
        _selectedFolderId = null;
        SaveState();
        RefreshEntries();
        return true;
    }

    private void DeleteEntries(IEnumerable<ClipboardEntry> entries, bool confirmLargeBatch = true)
    {
        var unique = entries.Where(_entries.Contains).Distinct().ToList();
        if (unique.Count == 0) return;
        if (confirmLargeBatch && unique.Count >= 10 && System.Windows.MessageBox.Show(
                LocalizationService.Get("DeleteBatchConfirm", unique.Count),
                LocalizationService.Get("DeleteBatchTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var visibleOrder = GetOrderedTabEntries(_selectedTabId);
        var libraryOrder = _selectedTabId is not null && _tabLibraryOrders.TryGetValue(_selectedTabId, out var storedLibrary)
            ? storedLibrary : null;
        _undoBatches.Add(new DeletedBatch
        {
            Entries = unique.Select(entry => new DeletedEntrySnapshot
            {
                Entry = entry,
                OriginalIndex = visibleOrder.IndexOf(entry),
                OriginalLibraryIndex = libraryOrder?.IndexOf("entry:" + entry.Id) ?? -1,
                TabId = _selectedTabId,
                FolderId = _selectedTabId is not null && entry.FolderIdsByTab.TryGetValue(_selectedTabId, out var folderId)
                    ? folderId : null,
                WasPinned = entry.IsPinnedInTab(_selectedTabId)
            }).ToList()
        });
        while (_undoBatches.Count > 10)
        {
            CleanupUndoBatch(_undoBatches[0]);
            _undoBatches.RemoveAt(0);
        }

        foreach (var entry in unique) DeleteEntryFromActiveTab(entry);
        foreach (var orphan in unique.Where(entry => !entry.IsInAllTab && entry.TabIds.Count == 0))
        {
            _entries.Remove(orphan);
            RemoveEntryFromOrders(orphan);
            _storage.DeleteImage(orphan);
        }
        UpdateUndoButton();
        SaveState();
        RegisterEntryHotKeys();
        RefreshEntries();
        StatusText.Text = unique.Count == 1 ? "تم الحذف — يمكنك التراجع" : $"تم حذف {unique.Count} عناصر — يمكنك التراجع";
    }

    private void UndoDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoBatches.Count == 0) return;
        var batch = _undoBatches[^1];
        _undoBatches.RemoveAt(_undoBatches.Count - 1);

        foreach (var folder in batch.DeletedFolders)
        {
            if (Tabs.Any(tab => tab.Id == folder.TabId) && !_folders.Any(f => f.Id == folder.Id))
            {
                _folders.Add(folder);
                if (!_tabLibraryOrders.TryGetValue(folder.TabId, out var library))
                    _tabLibraryOrders[folder.TabId] = library = new List<string>();
                var key = "folder:" + folder.Id;
                if (!library.Contains(key))
                    library.Insert(Math.Clamp(folder.Order, 0, library.Count), key);
                SyncFolderOrdersFromLibrary(folder.TabId, library);
            }
        }

        var restoredByScope = new Dictionary<string, List<(DeletedEntrySnapshot Snapshot, ClipboardEntry Entry)>>();
        foreach (var state in batch.Entries.OrderBy(state => state.OriginalIndex))
        {
            var targetTabId = state.TabId is not null && Tabs.Any(tab => tab.Id == state.TabId) ? state.TabId : null;
            var entry = _entries.FirstOrDefault(item => item.Id == state.Entry.Id)
                ?? _entries.FirstOrDefault(item => item.Kind == state.Entry.Kind && item.Hash == state.Entry.Hash)
                ?? state.Entry;
            if (!_entries.Contains(entry)) _entries.Insert(Math.Clamp(state.OriginalIndex, 0, _entries.Count), entry);
            if (targetTabId is null) entry.IsInAllTab = true;
            else if (!entry.TabIds.Contains(targetTabId)) entry.TabIds.Add(targetTabId);
            if (state.WasPinned) entry.SetPinnedInTab(targetTabId, true);
            if (targetTabId is not null && state.FolderId is not null &&
                _folders.Any(folder => folder.Id == state.FolderId && folder.TabId == targetTabId))
                entry.FolderIdsByTab[targetTabId] = state.FolderId;

            var scope = OrderScope(targetTabId);
            if (!restoredByScope.TryGetValue(scope, out var restored))
                restoredByScope[scope] = restored = new List<(DeletedEntrySnapshot, ClipboardEntry)>();
            restored.Add((state, entry));

            if (targetTabId is not null && state.FolderId is null && state.OriginalLibraryIndex >= 0)
            {
                if (!_tabLibraryOrders.TryGetValue(targetTabId, out var library))
                    _tabLibraryOrders[targetTabId] = library = new List<string>();
                var key = "entry:" + entry.Id;
                library.Remove(key);
                library.Insert(Math.Clamp(state.OriginalLibraryIndex, 0, library.Count), key);
            }
        }

        foreach (var restored in restoredByScope.Values)
        {
            var targetTabId = restored[0].Snapshot.TabId is not null && Tabs.Any(tab => tab.Id == restored[0].Snapshot.TabId)
                ? restored[0].Snapshot.TabId : null;
            var order = GetOrderedTabEntries(targetTabId);
            foreach (var pair in restored) order.Remove(pair.Entry);
            foreach (var pair in restored.OrderBy(pair => pair.Snapshot.OriginalIndex))
                order.Insert(Math.Clamp(pair.Snapshot.OriginalIndex, 0, order.Count), pair.Entry);
            SetTabOrder(targetTabId, order);
        }

        UpdateUndoButton();
        SaveState();
        RegisterEntryHotKeys();
        RefreshEntries();
        StatusText.Text = "تم التراجع عن الحذف";
    }

    private void CleanupUndoBatch(DeletedBatch batch)
    {
        foreach (var state in batch.Entries.Where(state => _entries.All(entry => entry.Id != state.Entry.Id)))
            _storage.DeleteImage(state.Entry);
    }

    private void UpdateUndoButton()
    {
        if (UndoDeleteButton is null) return;
        UndoDeleteButton.Visibility = _undoBatches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoDeleteButton.Content = _undoBatches.Count > 1 ? $"↶ {_undoBatches.Count}" : "↶";
        UndoDeleteButton.ToolTip = _undoBatches.Count > 0
            ? LocalizationService.Get("TooltipUndoWithCount", _undoBatches.Count)
            : LocalizationService.Get("TooltipUndo");
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TabNameDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var tab = new ClipboardTab { Name = dialog.TabName, Order = Tabs.Count };
        Tabs.Add(tab);
        SaveState();
        SelectTab(tab.Id);
    }

    private void AllTab_Click(object sender, RoutedEventArgs e) => SelectTab(null);

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _suppressTabClickUntil) return;
        if (sender is Button { DataContext: ClipboardTab tab }) SelectTab(tab.Id);
    }

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardTab tab })
        {
            _dragTabCandidate = tab;
            _tabDragStart = e.GetPosition(this);
        }
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTabCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tab = _dragTabCandidate;
        _dragTabCandidate = null;
        _suppressTabClickUntil = DateTime.UtcNow.AddMilliseconds(600);
        DragDrop.DoDragDrop(TabsControl, new DataObject(typeof(ClipboardTab), tab), DragDropEffects.Move);
    }

    private void TabsControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ClipboardTab)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabsControl_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ClipboardTab)) is not ClipboardTab dragged) return;
        var container = ItemsControl.ContainerFromElement(TabsControl, e.OriginalSource as DependencyObject) as ContentPresenter;
        var target = container?.DataContext as ClipboardTab;
        if (target is null || target == dragged) return;
        var oldIndex = Tabs.IndexOf(dragged);
        var targetIndex = Tabs.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0) return;
        Tabs.Move(oldIndex, targetIndex);
        SaveState();
        SelectTab(_selectedTabId);
        e.Handled = true;
    }

    private void UpdateTabBuckets()
    {
        PrimaryTabs.Clear();
        OverflowTabs.Clear();
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (i < 5) PrimaryTabs.Add(Tabs[i]);
            else OverflowTabs.Add(Tabs[i]);
        }
        if (MoreTabsButton is not null)
            MoreTabsButton.Visibility = OverflowTabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabSelectionVisuals();
    }

    private void UpdateTabSelectionVisuals()
    {
        if (AllTabButton is not null)
            AllTabButton.Tag = _selectedTabId is null ? "selected" : null;

        if (TabsControl is not null)
        {
            foreach (var button in FindVisualChildren<Button>(TabsControl))
            {
                if (button.DataContext is ClipboardTab tab)
                    button.Tag = tab.Id == _selectedTabId ? "selected" : null;
            }
        }

        if (MoreTabsButton is not null)
        {
            var selectedOverflow = OverflowTabs.FirstOrDefault(t => t.Id == _selectedTabId);
            MoreTabsButton.Tag = selectedOverflow is not null ? "selected" : null;
            MoreTabsButton.Content = "\u2022\u2022\u2022";
        }
    }

    private void SelectTab(string? tabId)
    {
        _historyScrollViewer ??= FindVisualChildren<ScrollViewer>(HistoryList).FirstOrDefault();
        if (_historyScrollViewer is not null)
            _scrollOffsets[_selectedTabId ?? ClipboardEntry.AllTabPinScope] = _historyScrollViewer.VerticalOffset;
        _selectedTabId = tabId;
        _selectedFolderId = null;
        NewFolderButton.Visibility = tabId is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateTabSelectionVisuals();
        RefreshEntries();
        Dispatcher.BeginInvoke(() =>
        {
            _historyScrollViewer ??= FindVisualChildren<ScrollViewer>(HistoryList).FirstOrDefault();
            if (_historyScrollViewer is not null &&
                _scrollOffsets.TryGetValue(tabId ?? ClipboardEntry.AllTabPinScope, out var offset))
                _historyScrollViewer.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private void MoreTabsButton_Click(object sender, RoutedEventArgs e)
    {
        if (OverflowTabs.Count == 0) return;
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        foreach (var tab in OverflowTabs)
        {
            var item = new MenuItem
            {
                Header = (tab.Id == _selectedTabId ? "✓  " : "    ") + tab.Name,
                FontWeight = tab.Id == _selectedTabId ? FontWeights.SemiBold : FontWeights.Normal
            };
            item.Click += (_, _) => SelectTab(tab.Id);
            item.PreviewMouseRightButtonUp += (_, args) =>
            {
                args.Handled = true;
                menu.IsOpen = false;
                ShowTabContextMenu(tab, MoreTabsButton);
            };
            menu.Items.Add(item);
        }
        if (sender is Button btn)
        {
            btn.ContextMenu = menu;
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    private void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && button.DataContext is ClipboardTab tab)
            ShowTabContextMenu(tab, button);
    }

    private void ShowTabContextMenu(ClipboardTab tab, UIElement target)
    {
        var menu = new ContextMenu { FlowDirection = LocalizationService.CurrentFlowDirection };
        var rename = new MenuItem { Header = LocalizationService.IsArabic ? "إعادة التسمية" : "Rename" };
        rename.Click += (_, _) => RenameTab(tab);
        var delete = new MenuItem
        {
            Header = LocalizationService.IsArabic ? "حذف التبويب" : "Delete tab",
            Foreground = (Brush)FindResource("DangerBrush")
        };
        delete.Click += (_, _) => DeleteTab(tab);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void RenameTab(ClipboardTab tab)
    {
        var dialog = new TabNameDialog(tab.Name, LocalizationService.Get("DialogTabNameTitle")) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        tab.Name = dialog.TabName;
        SaveState();
        SelectTab(tab.Id);
    }

    private void DeleteTab(ClipboardTab tab)
    {
        foreach (var batch in _undoBatches)
        {
            batch.DeletedFolders.RemoveAll(folder => folder.TabId == tab.Id);
            foreach (var snapshot in batch.Entries)
            {
                if (snapshot.TabId == tab.Id)
                {
                    snapshot.TabId = null;
                    snapshot.FolderId = null;
                    snapshot.OriginalLibraryIndex = -1;
                }
                snapshot.Entry.TabIds.Remove(tab.Id);
                snapshot.Entry.PinnedTabIds.Remove(tab.Id);
                snapshot.Entry.FolderIdsByTab.Remove(tab.Id);
            }
        }
        Tabs.Remove(tab);
        _tabEntryOrders.Remove(tab.Id);
        _tabLibraryOrders.Remove(tab.Id);
        _folders.RemoveAll(folder => folder.TabId == tab.Id);
        foreach (var entry in _entries)
        {
            entry.TabIds.Remove(tab.Id);
            entry.PinnedTabIds.Remove(tab.Id);
            entry.FolderIdsByTab.Remove(tab.Id);
        }
        DeleteOrphanedEntries();
        if (_selectedTabId == tab.Id) _selectedTabId = null;
        SaveState();
        SelectTab(_selectedTabId);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        List<ClipboardEntry> toRemove;
        if (_selectedTabId is null)
        {
            toRemove = _entries
                .Where(entry => entry.IsInAllTab && !entry.IsPinnedInTab(null))
                .ToList();
        }
        else
        {
            var tabId = _selectedTabId;
            toRemove = _entries
                .Where(entry => entry.TabIds.Contains(tabId) && !entry.IsPinnedInTab(tabId))
                .Where(entry => !entry.FolderIdsByTab.TryGetValue(tabId!, out var folderId) ||
                    !_folders.Any(folder => folder.Id == folderId && folder.IsPinned))
                .Where(entry => _selectedFolderId is null ||
                    (entry.FolderIdsByTab.TryGetValue(tabId!, out var folderId) && folderId == _selectedFolderId))
                .ToList();
        }
        DeleteEntries(toRemove);
    }

    private void DeleteOrphanedEntries()
    {
        var orphans = _entries
            .Where(entry => !entry.IsInAllTab && entry.TabIds.Count == 0)
            .ToList();
        foreach (var entry in orphans)
        {
            _entries.Remove(entry);
            RemoveEntryFromOrders(entry);
            _storage.DeleteImage(entry);
        }
        if (orphans.Count > 0) RegisterEntryHotKeys();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _capturePaused = !_capturePaused;
        PauseButton.Tag = _capturePaused ? "selected" : null;
        PauseBarOne.Visibility = _capturePaused ? Visibility.Collapsed : Visibility.Visible;
        PauseBarTwo.Visibility = _capturePaused ? Visibility.Collapsed : Visibility.Visible;
        ResumeGlyph.Visibility = _capturePaused ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.ToolTip = LocalizationService.Get(_capturePaused ? "TooltipResume" : "TooltipPause");
        RefreshEntries();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (SearchClearButton is not null) SearchClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshEntries();
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ClipboardEntry entry }) DeleteEntries(GetActionEntries(entry));
    }

    private void TogglePalette(bool preservePreviousFocus = false)
    {
        if (ShouldHidePalette(IsVisible)) Hide();
        else ShowPalette(preservePreviousFocus);
    }

    private static bool ShouldHidePalette(bool isVisible) => isVisible;

    private void ShowPalette(bool preservePreviousFocus = false)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        PositionPalette();
        SetNoActivateMode(preservePreviousFocus);
        if (_state.AnimationsEnabled)
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            Opacity = 1;
        }

        Show();
        if (!preservePreviousFocus)
        {
            Activate();
            SearchBox.Focus();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, 9 /* SW_RESTORE */);
                NativeMethods.SetForegroundWindow(handle);
                NativeMethods.BringWindowToTop(handle);
            }
        }
    }

    private void SetNoActivateMode(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        var updated = enabled ? style | NativeMethods.WsExNoActivate : style & ~NativeMethods.WsExNoActivate;
        if (updated == style) return;
        NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, updated);
        // Applying WS_EX_NOACTIVATE to an already-created WPF window requires a
        // non-sizing frame refresh. Without it Explorer can still lose its edit
        // control when the palette receives the first mouse click.
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void PositionPalette()
    {
        var area = SystemParameters.WorkArea;
        if (_state.PopupPosition == "bottom-left")
        {
            Left = area.Left + 12;
            Top = area.Bottom - Height - 8;
        }
        else if (_state.PopupPosition == "center")
        {
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        else
        {
            Left = area.Right - Width - 12;
            Top = area.Bottom - Height - 8;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var oldHotKeyModifiers = _state.HotKeyModifiers;
        var oldHotKeyVirtualKey = _state.HotKeyVirtualKey;
        var dialog = new SettingsWindow(_state, _storage) { Owner = this };
        var accepted = dialog.ShowDialog();
        if (dialog.ImportedBackup)
        {
            var restored = _storage.Load();
            CopySettings(_state, restored);
            _entries.Clear();
            _entries.AddRange(restored.Entries);
            Tabs.Clear();
            foreach (var tab in restored.Tabs) Tabs.Add(tab);
            _tabEntryOrders.Clear();
            foreach (var order in restored.TabEntryOrders) _tabEntryOrders[order.Key] = order.Value;
            _tabLibraryOrders.Clear();
            foreach (var order in restored.TabLibraryOrders) _tabLibraryOrders[order.Key] = order.Value;
            _undoBatches.Clear();
            _undoBatches.AddRange(restored.UndoBatches);
            _folders.Clear();
            _folders.AddRange(restored.Folders ?? new List<ClipboardFolder>());
            _selectedTabId = null;
            MigratePinData();
            FitWindowToWorkArea();
            KeepOpenButton.Tag = _state.KeepWindowOpenAfterPaste ? "selected" : null;
            UpdatePinnedWindowFrame();
            if (!ApplySettings())
            {
                _state.HotKeyModifiers = oldHotKeyModifiers;
                _state.HotKeyVirtualKey = oldHotKeyVirtualKey;
                ApplySettings();
                StatusText.Text = "اختصار النسخة المستعادة مستخدم بالفعل؛ تم الاحتفاظ بالاختصار السابق";
            }
            UpdateUndoButton();
            RegisterEntryHotKeys();
            SaveState();
            SelectTab(null);
            return;
        }
        if (accepted != true) return;
        if (!ApplySettings())
        {
            _state.HotKeyModifiers = oldHotKeyModifiers;
            _state.HotKeyVirtualKey = oldHotKeyVirtualKey;
            ApplySettings();
            StatusText.Text = "اختصار الفتح مستخدم بالفعل؛ تم الاحتفاظ بالاختصار السابق";
        }
        TrimHistory();
        SaveState();
        RefreshEntries();
        PositionPalette();
    }

    private static void CopySettings(AppState target, AppState source)
    {
        target.MaxEntries = source.MaxEntries;
        target.CardDensity = source.CardDensity;
        target.PopupPosition = source.PopupPosition;
        target.Theme = source.Theme;
        target.Language = source.Language;
        target.StartWithWindows = source.StartWithWindows;
        target.ClearTemporaryOnExit = source.ClearTemporaryOnExit;
        target.CleanAllTabAfterWindowsRestart = source.CleanAllTabAfterWindowsRestart;
        target.LastWindowsBootMarker = source.LastWindowsBootMarker;
        target.AnimationsEnabled = source.AnimationsEnabled;
        target.KeepWindowOpenAfterPaste = source.KeepWindowOpenAfterPaste;
        target.HotKeyModifiers = source.HotKeyModifiers;
        target.HotKeyVirtualKey = source.HotKeyVirtualKey;
        target.WindowWidth = source.WindowWidth;
        target.WindowHeight = source.WindowHeight;
    }

    private bool ApplySettings()
    {
        ApplyTheme(_state.Theme);
        ApplyLanguage(_state.Language);
        var hotKeyRegistered = true;
        var values = _state.CardDensity switch
        {
            "large" => (preview: 142d, text: 160d, padding: 13d),
            "medium" => (preview: 116d, text: 128d, padding: 11d),
            _ => (preview: 95d, text: 96d, padding: 10d)
        };
        Resources["PreviewHeight"] = values.preview;
        Resources["TextPreviewHeight"] = values.text;
        Resources["CardPadding"] = new Thickness(values.padding);

        if (_source is not null)
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.UnregisterHotKey(handle, HotKeyId);
            hotKeyRegistered = NativeMethods.RegisterHotKey(handle, HotKeyId, _state.HotKeyModifiers, _state.HotKeyVirtualKey);
            if (!hotKeyRegistered)
                ReportError(new InvalidOperationException("الاختصار مستخدم بواسطة برنامج آخر."), "تسجيل اختصار فتح حافظة");
            RegisterEntryHotKeys();
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_state.StartWithWindows && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
                key.SetValue("Hafiza", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("Hafiza", false);
        }
        catch (Exception error)
        {
            ReportError(error, "تحديث إعداد التشغيل مع ويندوز");
        }
        UpdateTrayMenu();
        return hotKeyRegistered;
    }

    private void ApplyTheme(string? theme)
    {
        ThemeService.ApplyTheme(theme);
        var isDark = ThemeService.IsDark;
        if (ThemeIconText is not null)
            ThemeIconText.Text = isDark ? "☀️" : "🌙";
        if (ThemeToggleButton is not null)
            ThemeToggleButton.ToolTip = LocalizationService.Get(isDark ? "TooltipThemeDark" : "TooltipThemeLight");
        UpdatePinnedWindowFrame();
    }

    private void ApplyLanguage(string? lang)
    {
        LocalizationService.ApplyLanguage(lang);
        FlowDirection = LocalizationService.CurrentFlowDirection;

        if (AppTitleText is not null) AppTitleText.Text = LocalizationService.Get("AppTitle");
        if (AppSubtitleText is not null) AppSubtitleText.Text = LocalizationService.Get("AppSubtitle");
        if (SearchBox is not null) SearchBox.ToolTip = LocalizationService.Get("SearchTooltip");
        if (SearchHint is not null) SearchHint.Text = LocalizationService.Get("SearchHint");
        if (SearchClearButton is not null) SearchClearButton.ToolTip = LocalizationService.Get("SearchClearTooltip");
        if (NewFolderButton is not null) NewFolderButton.ToolTip = LocalizationService.Get("TooltipNewFolder");
        if (FolderBackButton is not null) FolderBackButton.ToolTip = LocalizationService.Get("TooltipFolderBack");
        if (FolderOptionsButton is not null) FolderOptionsButton.ToolTip = LocalizationService.Get("TooltipFolderOptions");
        if (ClearButton is not null) ClearButton.Content = LocalizationService.Get("ClearUnpinned");
        if (BulkMoveButton is not null) BulkMoveButton.Content = LocalizationService.Get("BulkMove");
        if (ErrorButton is not null) ErrorButton.Content = LocalizationService.Get("ErrorButton");
        if (EmptyTitle is not null) EmptyTitle.Text = LocalizationService.Get(string.IsNullOrWhiteSpace(SearchBox?.Text) ? "EmptyTitle" : "EmptySearchTitle");
        if (EmptySubtitle is not null) EmptySubtitle.Text = LocalizationService.Get(string.IsNullOrWhiteSpace(SearchBox?.Text) ? "EmptySubtitle" : "EmptySearchSubtitle");
        if (AllTabButton is not null)
        {
            AllTabButton.Content = LocalizationService.Get("AllTab");
            AllTabButton.ToolTip = LocalizationService.Get("AllTab");
        }
        if (NewTabButton is not null) NewTabButton.ToolTip = LocalizationService.Get("TooltipNewTab");
        if (SettingsButton is not null) SettingsButton.ToolTip = LocalizationService.Get("TooltipSettings");
        if (KeepOpenButton is not null) KeepOpenButton.ToolTip = LocalizationService.Get("TooltipKeepOpen");
        if (UndoDeleteButton is not null) UndoDeleteButton.ToolTip = LocalizationService.Get("TooltipUndo");
        if (SelectionModeButton is not null) SelectionModeButton.ToolTip = LocalizationService.Get("TooltipSelection");
        if (PauseButton is not null) PauseButton.ToolTip = LocalizationService.Get(_capturePaused ? "TooltipResume" : "TooltipPause");
        if (HideButton is not null) HideButton.ToolTip = LocalizationService.Get("TooltipHide");

        if (LanguageBadgeText is not null)
            LanguageBadgeText.Text = LocalizationService.IsArabic ? "EN" : "AR";
        if (LanguageToggleButton is not null)
            LanguageToggleButton.ToolTip = LocalizationService.Get("TooltipLanguage");

        var isDark = ThemeService.IsDark;
        if (ThemeToggleButton is not null)
            ThemeToggleButton.ToolTip = LocalizationService.Get(isDark ? "TooltipThemeDark" : "TooltipThemeLight");

        UpdateTrayMenu();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var nextTheme = ThemeService.IsDark ? ThemeService.Light : ThemeService.Dark;
        _state.Theme = nextTheme;
        ApplyTheme(nextTheme);
        SaveState();
    }

    private void LanguageToggle_Click(object sender, RoutedEventArgs e)
    {
        var nextLang = LocalizationService.IsArabic ? LocalizationService.English : LocalizationService.Arabic;
        _state.Language = nextLang;
        ApplyLanguage(nextLang);
        SaveState();
        RefreshEntries();
    }

    private void RegisterEntryHotKeys()
    {
        if (_source is null) return;
        var handle = new WindowInteropHelper(this).Handle;
        foreach (var registeredId in _entryHotKeys.Keys.ToList())
            NativeMethods.UnregisterHotKey(handle, registeredId);
        _entryHotKeys.Clear();

        var id = EntryHotKeyStart;
        foreach (var entry in _entries.Where(entry => entry.PasteHotKeyVirtualKey != 0))
        {
            if (NativeMethods.RegisterHotKey(handle, id, entry.PasteHotKeyModifiers, entry.PasteHotKeyVirtualKey))
                _entryHotKeys[id] = entry;
            else
                StatusText.Text = LocalizationService.IsArabic
                    ? "أحد اختصارات البطاقات مستخدم في برنامج آخر"
                    : "One of the card shortcuts is already used by another application";
            id++;
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? System.Drawing.SystemIcons.Application
                : System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _trayMenu = new ContextMenu();
        UpdateTrayMenu();

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) HandleTrayLeftClick();
            else if (args.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
        _trayIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) HandleTrayLeftClick();
        };
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) HandleTrayLeftClick();
            else if (args.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon is not null)
            _trayIcon.Text = $"{LocalizationService.Get("AppTitle")} — {HotKeyFormatter.Format(_state.HotKeyModifiers, _state.HotKeyVirtualKey)}";
        if (_trayMenu is null) return;
        _trayMenu.FlowDirection = LocalizationService.CurrentFlowDirection;
        _trayMenu.Items.Clear();
        var open = new MenuItem { Header = LocalizationService.Get("TrayOpen") };
        open.Click += (_, _) => ShowPalette();
        var pause = new MenuItem { Header = LocalizationService.Get("TrayPauseResume") };
        pause.Click += (_, _) => PauseButton_Click(this, new RoutedEventArgs());
        var exit = new MenuItem { Header = LocalizationService.Get("TrayExit") };
        exit.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(open);
        _trayMenu.Items.Add(pause);
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(exit);
    }

    private DateTime _lastTrayClickTime = DateTime.MinValue;

    private void HandleTrayLeftClick()
    {
        if (DateTime.UtcNow - _lastTrayClickTime < TimeSpan.FromMilliseconds(250)) return;
        _lastTrayClickTime = DateTime.UtcNow;
        Dispatcher.BeginInvoke(() => ShowPalette());
    }

    private void ShowTrayMenu()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_trayMenu is null) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
            _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _trayMenu.IsOpen = true;
        });
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    internal void PrepareForSystemExit()
    {
        _reallyExit = true;
    }

    private void CleanAllTabAfterWindowsRestart()
    {
        // The All tab is session-only. Custom tabs are the permanent library.
        _state.CleanAllTabAfterWindowsRestart = true;
        var currentMarker = WindowsBootSession.CurrentMarker();
        // Installing the feature or updating the program is not a Windows
        // restart. Establish the first baseline without deleting anything.
        if (string.IsNullOrWhiteSpace(_state.LastWindowsBootMarker))
        {
            _state.LastWindowsBootMarker = currentMarker;
            SaveState();
            return;
        }
        if (string.Equals(_state.LastWindowsBootMarker, currentMarker, StringComparison.Ordinal)) return;

        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries.Where(entry => entry.IsInAllTab).ToList())
        {
            entry.IsInAllTab = false;
            entry.PinnedTabIds.Remove(ClipboardEntry.AllTabPinScope);
            if (entry.TabIds.Count > 0) continue;
            deletedIds.Add(entry.Id);
            _entries.Remove(entry);
            RemoveEntryFromOrders(entry);
            _storage.DeleteImage(entry);
        }

        // Restart cleanup is permanent: it must not remain recoverable through
        // an old undo batch or leave an orphaned image on disk.
        if (deletedIds.Count > 0)
        {
            foreach (var batch in _undoBatches)
                batch.Entries.RemoveAll(snapshot => deletedIds.Contains(snapshot.Entry.Id));
            _undoBatches.RemoveAll(batch => batch.Entries.Count == 0);
        }

        _state.LastWindowsBootMarker = currentMarker;
        SaveState();
        // Replace the fallback history with the cleaned state before removing
        // files, so an automatic recovery cannot resurrect All-tab entries.
        SaveState();
        _storage.CleanupOrphanImages(_entries.Concat(
            _undoBatches.SelectMany(batch => batch.Entries.Select(snapshot => snapshot.Entry))));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.RemoveClipboardFormatListener(handle);
        NativeMethods.UnregisterHotKey(handle, HotKeyId);
        foreach (var registeredId in _entryHotKeys.Keys)
            NativeMethods.UnregisterHotKey(handle, registeredId);
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        _source?.RemoveHook(WindowMessageHook);
        _trayIcon?.Dispose();
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var num = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                _ => -1
            };
            if (num >= 0 && num < FilteredEntries.Count)
            {
                await PasteEntryAsync(FilteredEntries[num]);
                e.Handled = true;
                return;
            }
        }

        if (FilteredEntries.Count == 0) return;

        if (e.Key is Key.Down or Key.Up)
        {
            var current = HistoryList.SelectedIndex;
            var step = e.Key == Key.Down ? 1 : -1;
            var next = FindNextEntryIndex(VisibleItems, current, step);
            if (next >= 0 && next < VisibleItems.Count)
            {
                HistoryList.SelectedIndex = next;
                HistoryList.ScrollIntoView(HistoryList.Items[next]);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && HistoryList.SelectedItem is ClipboardEntry entry)
        {
            await PasteEntryAsync(entry);
            e.Handled = true;
            return;
        }
    }

    private static int FindNextEntryIndex(IReadOnlyList<object> items, int current, int step)
    {
        var next = current < 0 ? (step > 0 ? 0 : items.Count - 1) : current + step;
        while (next >= 0 && next < items.Count && items[next] is not ClipboardEntry) next += step;
        return next >= 0 && next < items.Count ? next : -1;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        _state.WindowWidth = ActualWidth;
        _state.WindowHeight = ActualHeight;
        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void KeepOpenButton_Click(object sender, RoutedEventArgs e)
    {
        _state.KeepWindowOpenAfterPaste = !_state.KeepWindowOpenAfterPaste;
        KeepOpenButton.Tag = _state.KeepWindowOpenAfterPaste ? "selected" : null;
        KeepOpenButton.ToolTip = _state.KeepWindowOpenAfterPaste
            ? LocalizationService.Get("TooltipKeepOpenPinned")
            : LocalizationService.Get("TooltipKeepOpen");
        Topmost = true;
        UpdatePinnedWindowFrame();
        SaveState();
    }

    private void UpdatePinnedWindowFrame()
    {
        if (RootShell is null) return;
        RootShell.BorderBrush = (Brush)FindResource(_state.KeepWindowOpenAfterPaste
            ? "AccentBrush"
            : "SurfaceBorderBrush");
        RootShell.BorderThickness = new Thickness(_state.KeepWindowOpenAfterPaste ? 2 : 1);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindVisualParentOrSelf<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool HasVisualAncestorWithTag(DependencyObject source, object tag)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && Equals(element.Tag, tag)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string TextContentHash(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
        return HashBytes(Encoding.UTF8.GetBytes("text:" + normalized));
    }

    private static string FilesContentHash(IEnumerable<string> paths)
    {
        var normalized = paths.Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => path.ToUpperInvariant());
        return HashBytes(Encoding.UTF8.GetBytes("files:" + string.Join("\n", normalized)));
    }

    private static string ImageContentHash(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return $"image:{bitmap.PixelWidth}x{bitmap.PixelHeight}:{HashBytes(pixels)}";
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void SendPasteShortcut()
    {
        var inputs = new[]
        {
            // The palette is often opened with Ctrl+Alt+V or Enter. Explicit releases prevent
            // target controls from seeing residual keys instead of a plain Ctrl+V.
            KeyboardInput(NativeMethods.VkAlt, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkShift, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkLeftWindows, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkRightWindows, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkReturn, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkControl, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkControl, 0),
            KeyboardInput(NativeMethods.VkVKey, 0),
            KeyboardInput(NativeMethods.VkVKey, NativeMethods.KeyEventKeyUp),
            KeyboardInput(NativeMethods.VkControl, NativeMethods.KeyEventKeyUp)
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent != (uint)inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "تعذّر إرسال اختصار اللصق إلى البرنامج السابق.");
    }

    private static NativeMethods.Input KeyboardInput(ushort key, uint flags) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput { VirtualKey = key, Flags = flags }
        }
    };
}

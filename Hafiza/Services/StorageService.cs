using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Hafiza.Models;

namespace Hafiza.Services;

public sealed class StorageService
{
    private readonly string _dataDirectory;
    private readonly string _imagesDirectory;
    private readonly string _statePath;
    private readonly string _backupPath;
    private readonly string _errorLogPath;
    private readonly string _errorFlagPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };
    private readonly List<ClipboardEntry> _pendingImageDeletes = new();

    public StorageService()
    {
        _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hafiza");
        _imagesDirectory = Path.Combine(_dataDirectory, "Images");
        _statePath = Path.Combine(_dataDirectory, "history.json");
        _backupPath = Path.Combine(_dataDirectory, "history.backup.json");
        _errorLogPath = Path.Combine(_dataDirectory, "errors.log");
        _errorFlagPath = Path.Combine(_dataDirectory, "has-errors.flag");
        Directory.CreateDirectory(_imagesDirectory);
        try
        {
            if (File.Exists(_errorLogPath) && new FileInfo(_errorLogPath).Length > 2 * 1024 * 1024)
                File.WriteAllText(_errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] تم تنظيف سجل أخطاء قديم تجاوز الحد المسموح.{Environment.NewLine}");
        }
        catch (Exception error) { LogError(error, "صيانة سجل الأخطاء"); }
    }

    public AppState Load()
    {
        foreach (var path in new[] { _statePath, _backupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), _jsonOptions);
                if (state is not null) return NormalizeState(state);
            }
            catch (Exception error)
            {
                LogError(error, $"تعذّر قراءة {Path.GetFileName(path)}");
            }
        }
        return new AppState();
    }

    private static AppState NormalizeState(AppState state)
    {
        state.Entries ??= new List<ClipboardEntry>();
        state.Tabs ??= new List<ClipboardTab>();
        state.Folders ??= new List<ClipboardFolder>();
        state.TabEntryOrders ??= new Dictionary<string, List<string>>();
        state.TabLibraryOrders ??= new Dictionary<string, List<string>>();
        state.UndoBatches ??= new List<DeletedBatch>();
        foreach (var batch in state.UndoBatches) batch.DeletedFolders ??= new List<ClipboardFolder>();
        state.MaxEntries = Math.Clamp(state.MaxEntries, 10, 5000);
        if (state.CardDensity is not ("compact" or "medium" or "large")) state.CardDensity = "compact";
        if (state.PopupPosition is not ("bottom-right" or "bottom-left" or "center")) state.PopupPosition = "bottom-right";
        if (!double.IsFinite(state.WindowWidth) || state.WindowWidth <= 0) state.WindowWidth = 610;
        if (!double.IsFinite(state.WindowHeight) || state.WindowHeight <= 0) state.WindowHeight = 735;

        state.Tabs = state.Tabs.Where(tab => !string.IsNullOrWhiteSpace(tab.Id))
            .GroupBy(tab => tab.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var tabIds = state.Tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in state.TabEntryOrders.Keys.ToList()) state.TabEntryOrders[key] ??= new List<string>();
        foreach (var key in state.TabLibraryOrders.Keys.ToList()) state.TabLibraryOrders[key] ??= new List<string>();
        state.Folders = state.Folders.Where(folder => !string.IsNullOrWhiteSpace(folder.Id) && tabIds.Contains(folder.TabId))
            .GroupBy(folder => folder.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var folderIds = state.Folders.ToDictionary(folder => folder.Id, folder => folder.TabId, StringComparer.Ordinal);
        state.Entries = state.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        foreach (var entry in state.Entries.Concat(state.UndoBatches.SelectMany(batch => batch.Entries.Select(item => item.Entry))))
        {
            entry.TabIds ??= new List<string>();
            entry.PinnedTabIds ??= new List<string>();
            entry.FolderIdsByTab ??= new Dictionary<string, string>();
            entry.FilePaths ??= new List<string>();
            entry.TabIds.RemoveAll(id => !tabIds.Contains(id));
            entry.PinnedTabIds.RemoveAll(id => id != ClipboardEntry.AllTabPinScope && !tabIds.Contains(id));
            foreach (var key in entry.FolderIdsByTab.Keys.Where(tabId => !tabIds.Contains(tabId) ||
                         !folderIds.TryGetValue(entry.FolderIdsByTab[tabId], out var owner) || owner != tabId).ToList())
                entry.FolderIdsByTab.Remove(key);
        }
        return state;
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(_dataDirectory);
        var temporaryPath = _statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, _jsonOptions));
        if (File.Exists(_statePath))
            File.Replace(temporaryPath, _statePath, _backupPath, true);
        else
            File.Move(temporaryPath, _statePath);
        // Files are eligible only after committing the history. Keep every file
        // referenced by either the current state, its undo history or the backup.
        var retained = ImageReferences(state).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_backupPath))
            {
                var backup = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_backupPath), _jsonOptions);
                if (backup is not null) retained.UnionWith(ImageReferences(backup));
            }
            var stillPending = new List<ClipboardEntry>();
            foreach (var entry in _pendingImageDeletes.ToArray())
            {
                var retainedAnyPath = false;
                foreach (var path in new[] { entry.ImagePath, entry.ThumbnailPath })
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var full = Path.GetFullPath(path);
                    if (retained.Contains(full)) { retainedAnyPath = true; continue; }
                    if (!string.Equals(Path.GetDirectoryName(full), _imagesDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(full)) File.Delete(full);
                }
                if (retainedAnyPath) stillPending.Add(entry);
            }
            _pendingImageDeletes.Clear();
            _pendingImageDeletes.AddRange(stillPending);
            foreach (var file in Directory.EnumerateFiles(_imagesDirectory, "*.png"))
            {
                var full = Path.GetFullPath(file);
                if (!retained.Contains(full)) File.Delete(full);
            }
        }
        catch (Exception error) { LogError(error, "تنظيف ملفات بعد حفظ السجل"); }
    }

    private static IEnumerable<string> ImageReferences(AppState state) =>
        state.Entries.Concat(state.UndoBatches.SelectMany(batch => batch.Entries.Select(item => item.Entry)))
            .Where(entry => entry.Kind == "image")
            .SelectMany(entry => new[] { entry.ImagePath, entry.ThumbnailPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));

    public string SaveImageBytes(byte[] pngBytes, string id)
    {
        var path = Path.Combine(_imagesDirectory, id + ".png");
        File.WriteAllBytes(path, pngBytes);
        return path;
    }

    public (string original, string thumbnail) SaveImageFiles(byte[] pngBytes, string id)
    {
        var originalPath = Path.Combine(_imagesDirectory, id + ".png");
        var thumbnailPath = Path.Combine(_imagesDirectory, id + ".thumb.png");
        var originalTemporaryPath = originalPath + ".tmp";
        var thumbnailTemporaryPath = thumbnailPath + ".tmp";
        try
        {
            File.WriteAllBytes(originalTemporaryPath, pngBytes);

            using var input = new MemoryStream(pngBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();

            var scale = Math.Min(1d, Math.Min(480d / image.PixelWidth, 220d / image.PixelHeight));
            BitmapSource thumbnail = scale < 1d
                ? new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale))
                : image;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            using (var output = File.Create(thumbnailTemporaryPath)) encoder.Save(output);

            File.Move(originalTemporaryPath, originalPath, true);
            File.Move(thumbnailTemporaryPath, thumbnailPath, true);
            return (originalPath, thumbnailPath);
        }
        catch
        {
            foreach (var path in new[] { originalTemporaryPath, thumbnailTemporaryPath, originalPath, thumbnailPath })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
            throw;
        }
    }

    public string SaveThumbnail(BitmapSource image, string id)
    {
        var path = Path.Combine(_imagesDirectory, id + ".thumb.png");
        var scale = Math.Min(1d, Math.Min(480d / image.PixelWidth, 220d / image.PixelHeight));
        var thumbnail = scale < 1d
            ? new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale))
            : image;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(thumbnail));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public void EnsureThumbnail(ClipboardEntry entry)
    {
        if (entry.Kind != "image" || string.IsNullOrWhiteSpace(entry.ImagePath) || !File.Exists(entry.ImagePath)) return;
        if (!string.IsNullOrWhiteSpace(entry.ThumbnailPath) && File.Exists(entry.ThumbnailPath)) return;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(entry.ImagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        entry.ThumbnailPath = SaveThumbnail(image, entry.Id);
    }

    public void DeleteImage(ClipboardEntry entry)
    {
        if (entry.Kind == "image") _pendingImageDeletes.Add(entry);
    }

    public void LogError(Exception error, string context)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (File.Exists(_errorLogPath) && new FileInfo(_errorLogPath).Length > 2 * 1024 * 1024)
                File.WriteAllText(_errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] بدأ سجل جديد بعد بلوغ الحد الأقصى.{Environment.NewLine}");
            File.AppendAllText(_errorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{error}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}");
            File.WriteAllText(_errorFlagPath, DateTime.Now.ToString("O"));
        }
        catch { }
    }

    public bool HasErrors => File.Exists(_errorFlagPath);
    public void AcknowledgeErrors()
    {
        try { if (File.Exists(_errorFlagPath)) File.Delete(_errorFlagPath); }
        catch { }
    }
    public string ErrorLogPath => _errorLogPath;
    public string DataDirectory => _dataDirectory;
    public string StatePath => _statePath;

    public void CleanupOrphanImages(IEnumerable<ClipboardEntry> activeEntries)
    {
        var retained = activeEntries
            .Where(entry => entry.Kind == "image")
            .SelectMany(entry => new[] { entry.ImagePath, entry.ThumbnailPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_imagesDirectory, "*.png"))
        {
            try { if (!retained.Contains(Path.GetFullPath(file))) File.Delete(file); }
            catch (Exception error) { LogError(error, "تنظيف صورة غير مستخدمة"); }
        }
    }

    public void ExportBackup(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(destination);
        if (!File.Exists(_statePath)) Save(new AppState());
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(_statePath, "history.json", CompressionLevel.Optimal);
        if (Directory.Exists(_imagesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_imagesDirectory))
                archive.CreateEntryFromFile(file, "Images/" + Path.GetFileName(file), CompressionLevel.Optimal);
        }
    }

    public void ImportBackup(string source)
    {
        if (Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var imported = NormalizeState(JsonSerializer.Deserialize<AppState>(File.ReadAllText(source), _jsonOptions)
                ?? throw new InvalidDataException("بيانات النسخة غير صالحة."));
            foreach (var entry in imported.Entries.Concat(imported.UndoBatches.SelectMany(batch => batch.Entries.Select(item => item.Entry))).Where(entry => entry.Kind == "image"))
            {
                if (!string.IsNullOrWhiteSpace(entry.ImagePath))
                {
                    var localPath = Path.Combine(_imagesDirectory, Path.GetFileName(entry.ImagePath));
                    if (File.Exists(localPath)) entry.ImagePath = localPath;
                }
                if (!string.IsNullOrWhiteSpace(entry.ThumbnailPath))
                {
                    var localThumb = Path.Combine(_imagesDirectory, Path.GetFileName(entry.ThumbnailPath));
                    if (File.Exists(localThumb)) entry.ThumbnailPath = localThumb;
                }
            }
            Save(imported);
            return;
        }

        var temporary = Path.Combine(Path.GetTempPath(), "HafizaImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var copiedImages = new List<string>();
        var committed = false;
        try
        {
            ZipFile.ExtractToDirectory(source, temporary);
            var importedState = Path.Combine(temporary, "history.json");
            if (!File.Exists(importedState)) throw new InvalidDataException("النسخة لا تحتوي على بيانات حافظة.");
            var imported = NormalizeState(JsonSerializer.Deserialize<AppState>(File.ReadAllText(importedState), _jsonOptions)
                ?? throw new InvalidDataException("بيانات النسخة غير صالحة."));
            var importedImages = Path.Combine(temporary, "Images");
            // Give imported files new names. The previous history remains in the
            // rolling backup, and its image files must keep their original bytes.
            var importedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string RelocateImage(string originalPath)
            {
                var name = Path.GetFileName(originalPath);
                if (importedPaths.TryGetValue(name, out var existing)) return existing;
                var destination = Path.Combine(_imagesDirectory, Guid.NewGuid().ToString("N") + ".png");
                var archivedFile = Path.Combine(importedImages, name);
                if (File.Exists(archivedFile))
                {
                    File.Copy(archivedFile, destination);
                    copiedImages.Add(destination);
                }
                importedPaths[name] = destination;
                return destination;
            }
            foreach (var entry in imported.Entries.Concat(imported.UndoBatches.SelectMany(batch => batch.Entries.Select(item => item.Entry))).Where(entry => entry.Kind == "image"))
            {
                if (!string.IsNullOrWhiteSpace(entry.ImagePath))
                    entry.ImagePath = RelocateImage(entry.ImagePath);
                if (!string.IsNullOrWhiteSpace(entry.ThumbnailPath))
                    entry.ThumbnailPath = RelocateImage(entry.ThumbnailPath);
            }
            Save(imported);
            committed = true;
        }
        finally
        {
            if (!committed)
                foreach (var path in copiedImages)
                    try { File.Delete(path); }
                    catch (Exception error) { LogError(error, "تنظيف صورة من استعادة فاشلة"); }
            try { Directory.Delete(temporary, true); }
            catch (Exception error) { LogError(error, "تنظيف ملفات الاستعادة المؤقتة"); }
        }
    }
}

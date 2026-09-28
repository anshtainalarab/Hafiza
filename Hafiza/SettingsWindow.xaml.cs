using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hafiza.Models;
using Hafiza.Services;
using Microsoft.Win32;

namespace Hafiza;

public partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly StorageService _storage;
    public bool ImportedBackup { get; private set; }
    private uint _hotKeyModifiers;
    private uint _hotKeyVirtualKey;

    public SettingsWindow(AppState state, StorageService storage)
    {
        InitializeComponent();
        FlowDirection = LocalizationService.CurrentFlowDirection;
        _state = state;
        _storage = storage;
        SelectTag(LanguageBox, state.Language ?? LocalizationService.English);
        SelectTag(ThemeBox, state.Theme ?? ThemeService.Dark);
        MaxEntriesBox.Text = state.MaxEntries.ToString();
        SelectTag(DensityBox, state.CardDensity);
        SelectTag(PositionBox, state.PopupPosition);
        _hotKeyModifiers = state.HotKeyModifiers;
        _hotKeyVirtualKey = state.HotKeyVirtualKey;
        HotKeyBox.Text = HotKeyFormatter.Format(_hotKeyModifiers, _hotKeyVirtualKey);
        AnimationsBox.IsChecked = state.AnimationsEnabled;
        StartupBox.IsChecked = state.StartWithWindows;
        BootCleanupBox.IsChecked = state.CleanAllTabAfterWindowsRestart;
    }

    private static void SelectTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag)) ?? box.Items[0];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxEntriesBox.Text, out var maximum) || maximum < 10 || maximum > 5000)
        {
            NoticeText.Text = LocalizationService.IsArabic ? "اكتب عددًا من 10 إلى 5000." : "Enter a number between 10 and 5000.";
            return;
        }
        _state.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? LocalizationService.English;
        _state.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ThemeService.Dark;
        _state.MaxEntries = maximum;
        _state.CardDensity = (DensityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "compact";
        _state.PopupPosition = (PositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "bottom-right";
        _state.AnimationsEnabled = AnimationsBox.IsChecked == true;
        _state.HotKeyModifiers = _hotKeyModifiers;
        _state.HotKeyVirtualKey = _hotKeyVirtualKey;
        _state.StartWithWindows = StartupBox.IsChecked == true;
        _state.CleanAllTabAfterWindowsRestart = BootCleanupBox.IsChecked == true;
        _state.ClearTemporaryOnExit = false;
        if (_state.CleanAllTabAfterWindowsRestart)
            _state.LastWindowsBootMarker = WindowsBootSession.CurrentMarker();
        DialogResult = true;
    }

    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            DialogResult = false;
            return;
        }
        if (key == Key.Tab) return;
        if (key == Key.Enter)
        {
            Save_Click(this, new RoutedEventArgs());
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;

        e.Handled = true;
        var modifiers = Keyboard.Modifiers;
        if (!HotKeyFormatter.TryGetModifiers(modifiers,
                Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin), out var nativeModifiers))
        {
            NoticeText.Text = LocalizationService.Get("SettingsNoticeModifierOnly");
            return;
        }
        _hotKeyModifiers = nativeModifiers;
        _hotKeyVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        HotKeyBox.Text = HotKeyFormatter.Format(_hotKeyModifiers, _hotKeyVirtualKey);
        NoticeText.Text = LocalizationService.Get("SettingsNoticeShortcutCaptured");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = LocalizationService.Get("SettingsBackupFilter"), FileName = $"Hafiza-{DateTime.Now:yyyy-MM-dd}.hafiza" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _storage.Save(_state);
            _storage.ExportBackup(dialog.FileName);
            NoticeText.Text = LocalizationService.Get("SettingsNoticeExportSuccess");
        }
        catch (Exception error)
        {
            _storage.LogError(error, "تصدير نسخة احتياطية");
            NoticeText.Text = LocalizationService.Get("SettingsNoticeExportFailed");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = LocalizationService.Get("SettingsRestoreFilter") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _storage.ImportBackup(dialog.FileName);
            ImportedBackup = true;
            DialogResult = true;
        }
        catch (Exception error)
        {
            _storage.LogError(error, "استعادة نسخة احتياطية");
            NoticeText.Text = LocalizationService.Get("SettingsNoticeImportFailed");
        }
    }
}

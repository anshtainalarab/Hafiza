using System.Windows;
using System.Windows.Input;
using Hafiza.Services;

namespace Hafiza;

public partial class HotKeyDialog : Window
{
    public uint Modifiers { get; private set; }
    public uint VirtualKey { get; private set; }

    public HotKeyDialog(uint modifiers, uint virtualKey)
    {
        InitializeComponent();
        FlowDirection = LocalizationService.CurrentFlowDirection;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        if (VirtualKey != 0) ShortcutText.Text = HotKeyFormatter.Format(Modifiers, VirtualKey);
        else ShortcutText.Text = LocalizationService.IsArabic ? "اضغط الاختصار الآن" : "Press shortcut now";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Enter || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var modifiers = Keyboard.Modifiers;
        if (!HotKeyFormatter.TryGetModifiers(modifiers,
                Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin), out var nativeModifiers)) return;
        Modifiers = nativeModifiers;
        VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        ShortcutText.Text = HotKeyFormatter.Format(Modifiers, VirtualKey);
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (VirtualKey != 0) DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Modifiers = 0;
        VirtualKey = 0;
        DialogResult = true;
    }
}

using System.Collections.Generic;
using System.Windows.Input;

namespace Hafiza.Services;

public static class HotKeyFormatter
{
    public static bool TryGetModifiers(ModifierKeys keys, bool windowsPressed, out uint modifiers)
    {
        modifiers = 0;
        if (windowsPressed) return false;
        if (keys.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.ModAlt;
        if (keys.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.ModControl;
        if (keys.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.ModShift;
        return modifiers != 0;
    }

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)virtualKey).ToString());
        return string.Join(" + ", parts);
    }
}

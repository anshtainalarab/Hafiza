using System;
using Microsoft.Win32;

namespace Hafiza.Services;

internal static class WindowsBootSession
{
    internal static string CurrentMarker()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Windows");
            if (key?.GetValue("ShutdownTime") is byte[] shutdownTime && shutdownTime.Length > 0)
                return "shutdown:" + Convert.ToHexString(shutdownTime);
        }
        catch { }

        // Stable within the same Windows boot under normal clock adjustments.
        var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return "boot:" + (bootUtc.Ticks / TimeSpan.TicksPerMinute);
    }
}

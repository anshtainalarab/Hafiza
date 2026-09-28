using System;
using System.Runtime.InteropServices;

namespace Hafiza.Services;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmHotKey = 0x0312;
    internal const uint WmPaste = 0x0302;
    internal const int WmNcHitTest = 0x0084;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const uint ModControl = 0x0002;
    internal const uint ModAlt = 0x0001;
    internal const uint ModShift = 0x0004;
    internal const uint VkV = 0x56;
    internal const uint InputKeyboard = 1;
    internal const ushort VkControl = 0x11;
    internal const ushort VkShift = 0x10;
    internal const ushort VkAlt = 0x12;
    internal const ushort VkLeftWindows = 0x5B;
    internal const ushort VkRightWindows = 0x5C;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkVKey = 0x56;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint WineventOutOfContext = 0x0000;
    internal const int GwlExStyle = -20;
    internal const int WsExNoActivate = 0x08000000;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    internal const int VkLeftButton = 0x01;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint attachingThreadId, uint targetThreadId, bool attach);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    internal const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    // INPUT is a native union. MOUSEINPUT is its largest member on 64-bit Windows;
    // including it keeps Marshal.SizeOf<Input>() at the required 40 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal int Size;
        internal uint Flags;
        internal IntPtr Active;
        internal IntPtr Focus;
        internal IntPtr Capture;
        internal IntPtr MenuOwner;
        internal IntPtr MoveSize;
        internal IntPtr Caret;
        internal NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}

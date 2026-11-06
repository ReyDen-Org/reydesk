using System;
using System.Runtime.InteropServices;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Input;

/// <summary>
/// Injects remote mouse/keyboard input via Win32 SendInput. Pointer coordinates
/// arrive normalized [0,1] against the captured display (see docs/protocol.md
/// "Platform neutrality") - this maps them to that display's actual pixel resolution
/// and injects as absolute mouse input against the primary monitor, which is the only
/// display ScreenCapture ever captures for now (see Media/Direct3D11Interop.cs).
/// Text uses SendInput's KEYEVENTF_UNICODE path rather than VK-code key events, so
/// arbitrary committed IME text types correctly regardless of the host's keyboard
/// layout.
///
/// </summary>
internal static class InputInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x01000;

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;

    /// <summary>USB HID usage code (Keyboard/Keypad page) -> Win32 virtual-key code.
    /// Deliberately minimal - only the keys that actually need a real, non-Unicode key
    /// event are wired up (TypeText's KEYEVENTF_UNICODE path has no way to express
    /// "delete" or a real Enter keypress - a Unicode newline character is NOT the same
    /// thing as VK_RETURN to most Windows UI, confirmed on real hardware: it did not
    /// submit anything - see docs/protocol.md "Platform neutrality" for why the wire
    /// format is HID codes, not VK codes). Extend this table as more non-text keys are
    /// wired up.</summary>
    private static ushort? HidUsageToVirtualKey(uint hidUsageCode) => hidUsageCode switch
    {
        0x28 => VK_RETURN, // Keyboard Return (ENTER)
        0x2A => VK_BACK, // Keyboard DELETE (Backspace)
        _ => null,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Moves the cursor to a normalized [0,1] position on the primary
    /// monitor.</summary>
    public static void MoveTo(float normalizedX, float normalizedY)
    {
        var (absX, absY) = ToAbsoluteCoordinates(normalizedX, normalizedY);
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, absX, absY);
    }

    public static void SendMouseButtonEvent(MouseButton button, bool isDown, float normalizedX, float normalizedY)
    {
        var (absX, absY) = ToAbsoluteCoordinates(normalizedX, normalizedY);
        var flags = button switch
        {
            MouseButton.Left => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        if (flags == 0)
        {
            return;
        }
        // Button coordinates only relocate the pointer when MOVE is included.
        // Keep move and button in one input event to avoid cross-channel races.
        SendMouse(flags | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, absX, absY);
    }

    public static void Scroll(float deltaX, float deltaY)
    {
        // WHEEL_DELTA (120) per notch is the Win32 convention; deltaY/X here are
        // already in "notches" from the sender (see input.proto MouseScroll).
        if (deltaY != 0)
        {
            SendMouseWheel(MOUSEEVENTF_WHEEL, (int)(deltaY * 120));
        }
        if (deltaX != 0)
        {
            SendMouseWheel(MOUSEEVENTF_HWHEEL, (int)(deltaX * 120));
        }
    }

    /// <summary>Types committed text as Unicode key events - not real key presses, so
    /// it works regardless of the host's keyboard layout/language.</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } };
            var up = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>Injects a real (non-Unicode) key event for a HID usage code this
    /// project maps (see <see cref="HidUsageToVirtualKey"/>) - unmapped codes are
    /// silently ignored rather than guessed at.</summary>
    public static void SendKeyEvent(uint hidUsageCode, bool isDown)
    {
        var vk = HidUsageToVirtualKey(hidUsageCode);
        if (vk is null)
        {
            return;
        }
        var input = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk.Value, dwFlags = isDown ? 0 : KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static (int X, int Y) ToAbsoluteCoordinates(float normalizedX, float normalizedY)
    {
        // SendInput's absolute mode uses a 0-65535 coordinate space mapped onto the
        // primary monitor (without MOUSEEVENTF_VIRTUALDESK) - independent of actual
        // pixel resolution, so no GetSystemMetrics lookup is needed here.
        var absX = (int)(Math.Clamp(normalizedX, 0f, 1f) * 65535);
        var absY = (int)(Math.Clamp(normalizedY, 0f, 1f) * 65535);
        return (absX, absY);
    }

    private static void SendMouse(uint flags, int x, int y)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseWheel(uint flags, int amount)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)amount), dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}


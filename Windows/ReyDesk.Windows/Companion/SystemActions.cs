using System;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows.Companion;

internal static class SystemActions
{
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        // INPUT union size must include MOUSEINPUT, including pointer alignment.
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard
    { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse
    { public int X,Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool LockWorkStation();
    public static bool Execute(string operation)
    {
        if (operation == "lock") return LockWorkStation();
        ushort key = operation switch
        {
            "play_pause" => 0xB3, "next" => 0xB0, "previous" => 0xB1,
            "volume_up" => 0xAF, "volume_down" => 0xAE, "mute" => 0xAD, _ => 0,
        };
        if (key == 0) return false;
        return SendInput(2, new[] {
            new Input { Type=1, Data=new Union { Keyboard=new Keyboard { Vk=key } } },
            new Input { Type=1, Data=new Union { Keyboard=new Keyboard { Vk=key, Flags=2 } } },
        }, Marshal.SizeOf<Input>()) == 2;
    }
}

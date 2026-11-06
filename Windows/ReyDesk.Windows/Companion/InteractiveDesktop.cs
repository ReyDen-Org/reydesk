using System.Runtime.InteropServices;
using System.Text;
namespace ReyDesk.Windows.Companion;
internal static class InteractiveDesktop
{
    [DllImport("user32.dll", SetLastError=true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, uint size, out uint needed);
    public static bool IsUnlocked()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
}

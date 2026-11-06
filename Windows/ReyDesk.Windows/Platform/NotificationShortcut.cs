using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ReyDesk.Windows.Platform;

// Unpackaged notifications need a Shell-resolvable sender, including the SDK's
// actual COM activator. This writes only our per-user Start-menu shortcut.
internal static class NotificationShortcut
{
    internal sealed record Identity(string Target, string AppId, Guid Activator);
    private static readonly Guid AppModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static void SetProcessIdentity(string appId) => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
    internal static string GetProcessIdentity()
    {
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var value));
        try { return Marshal.PtrToStringUni(value) ?? ""; }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

    internal static void Ensure(string path, string executable, string appId, Guid activator)
    {
        if (File.Exists(path))
        {
            var existing = Read(path);
            if (existing.AppId != appId)
                throw new InvalidOperationException("Start-menu shortcut belongs to another app identity.");
            if (string.Equals(existing.Target, executable, StringComparison.OrdinalIgnoreCase) && existing.Activator == activator) return;
        }
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("ReyDesk");
            link.SetIconLocation(executable, 0);
            var properties = (IPropertyStore)link;
            var idKey = new PropertyKey(AppModel, 5);
            var activatorKey = new PropertyKey(AppModel, 26);
            var id = PropVariant.String(appId);
            var clsid = PropVariant.Clsid(activator);
            try
            {
                Marshal.ThrowExceptionForHR(properties.SetValue(ref idKey, ref id));
                Marshal.ThrowExceptionForHR(properties.SetValue(ref activatorKey, ref clsid));
                Marshal.ThrowExceptionForHR(properties.Commit());
                ((IPersistFile)link).Save(path, true);
            }
            finally { id.Dispose(); clsid.Dispose(); }
        }
        finally { Marshal.ReleaseComObject(link); }
        var saved = Read(path);
        if (!string.Equals(saved.Target, executable, StringComparison.OrdinalIgnoreCase) || saved.AppId != appId || saved.Activator != activator)
            throw new IOException("Notification shortcut verification failed.");
    }

    internal static Identity Read(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var properties = (IPropertyStore)link;
            var idKey = new PropertyKey(AppModel, 5);
            var activatorKey = new PropertyKey(AppModel, 26);
            Marshal.ThrowExceptionForHR(properties.GetValue(ref idKey, out var id));
            try
            {
                Marshal.ThrowExceptionForHR(properties.GetValue(ref activatorKey, out var clsid));
                try { return new(target.ToString(), id.AsString(), clsid.AsGuid()); }
                finally { clsid.Dispose(); }
            }
            finally { id.Dispose(); }
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _pointer;
        internal static PropVariant String(string value) => new() { _type = 31, _pointer = Marshal.StringToCoTaskMemUni(value) };
        internal static PropVariant Clsid(Guid value)
        {
            var pointer = Marshal.AllocCoTaskMem(16);
            Marshal.StructureToPtr(value, pointer, false);
            return new() { _type = 72, _pointer = pointer };
        }
        internal string AsString() => _type == 31 ? Marshal.PtrToStringUni(_pointer) ?? "" : "";
        internal Guid AsGuid() => _type == 72 && _pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(_pointer) : Guid.Empty;
        public void Dispose() { PropVariantClear(ref this); }
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

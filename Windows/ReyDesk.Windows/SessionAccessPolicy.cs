namespace ReyDesk.Windows;

/// <summary>
/// T18 M1: whether a live session may keep running. Kept free of app dependencies so the
/// SessionLifecycle test can link and exercise it. A remote session needs both the global
/// "Allow over internet" switch and the device's own remote permission, checked on every
/// authorization and again whenever either one changes.
/// </summary>
internal static class SessionAccessPolicy
{
    public static bool RemoteSessionPermitted(bool remote, bool globalRemoteAllowed, bool deviceRemoteAllowed) =>
        !remote || (globalRemoteAllowed && deviceRemoteAllowed);
}

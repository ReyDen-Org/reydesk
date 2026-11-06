namespace ReyDesk.Windows.Platform;

/// <summary>
/// GDI colors for the owner-drawn tray menu (contract 2.17). GDI cannot read XAML
/// brushes, so the token values are mirrored here; this class and Tokens.Dark.xaml
/// are the only places raw hex is allowed. Values are COLORREF (0x00BBGGRR).
/// Source tokens are named in the comments.
/// </summary>
internal static class TrayPalette
{
    private static uint Rgb(int rrggbb) =>
        (uint)(((rrggbb & 0xFF) << 16) | (rrggbb & 0xFF00) | ((rrggbb >> 16) & 0xFF));

    public static readonly uint Surface = Rgb(0x0E131B);      // RDcInk2
    public static readonly uint Border = Rgb(0x2A3240);       // RDcLineTray
    public static readonly uint Hover = Rgb(0x26231C);        // RDcAccentSelected
    public static readonly uint Text = Rgb(0xF3F5F8);         // RDcFg1
    public static readonly uint TextDisabled = Rgb(0x6B7383); // RDcFgDisabled
    public static readonly uint Separator = Rgb(0x222A37);    // RDcLine

    public static readonly uint Success = Rgb(0x3DDC84);      // RDcSuccess
    public static readonly uint Warning = Rgb(0xFF8A3D);      // RDcWarning
    public static readonly uint Danger = Rgb(0xFF5A5F);       // RDcDanger
    public static readonly uint Offline = Rgb(0x8590A2);      // RDcFgFaint

    public static uint ForState(TrayState state) => state switch
    {
        TrayState.Online => Success,
        TrayState.Connecting => Warning,
        TrayState.Error => Danger,
        _ => Offline,
    };
}

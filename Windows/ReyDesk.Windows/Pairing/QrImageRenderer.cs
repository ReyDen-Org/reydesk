using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace ReyDesk.Windows.Pairing;

internal static class QrImageRenderer
{
    /// <summary>Renders text as a QR code PNG and loads it as a WinUI3 BitmapImage.</summary>
    public static async Task<BitmapImage> RenderAsync(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrData);
        var pngBytes = pngQrCode.GetGraphic(pixelsPerModule);

        using var randomAccessStream = new InMemoryRandomAccessStream();
        await randomAccessStream.WriteAsync(pngBytes.AsBuffer());
        randomAccessStream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(randomAccessStream);
        return bitmap;
    }
}

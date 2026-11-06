using System.Collections.Generic;

namespace ReyDesk.Windows.Localization;

/// <summary>
/// D3 strings for the shell and Connection screen. Strings.cs is a non-partial static class owned by D4, so these
/// live in their own table and follow the same column order (En, Fa). Merge into Strings.cs when D4 rewrites it.
/// </summary>
internal static class ConnectionStrings
{
    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        return Strings.RtlSafe(row[(int)Strings.Current]);
    }

    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["idleLine"] = new[] { "Waiting for a phone", "منتظر اتصال گوشی" },
        ["connectingLine"] = new[] { "Connecting", "در حال اتصال" },
        ["deskIdLabel"] = new[] { "ReyDesk ID", "شناسه ReyDesk" },
        ["copy"] = new[] { "Copy", "کپی" },
        ["copied"] = new[] { "Copied", "کپی شد" },
        ["idNotSet"] = new[] { "Not set up yet", "هنوز راه‌اندازی نشده" },
        ["idNone"] = new[] { "No ID received yet", "هنوز شناسه‌ای دریافت نشده" },
        ["idGetting"] = new[] { "Getting your ID...", "در حال دریافت شناسه..." },
        ["idOff"] = new[] { "Turn on Internet access to get your ID", "برای دریافت شناسه، دسترسی از اینترنت را روشن کنید" },
        ["idUnreachable"] = new[] { "Server not reachable, so no ID has been issued yet", "سرور در دسترس نیست و هنوز شناسه‌ای صادر نشده" },
        ["retry"] = new[] { "Retry", "تلاش دوباره" },
        ["internet"] = new[] { "Internet connection", "اتصال از اینترنت" },
        ["srvOff"] = new[] { "Off", "خاموش" },
        ["srvConnecting"] = new[] { "Connecting to server", "در حال اتصال به سرور" },
        ["srvOnline"] = new[] { "Online", "آنلاین" },
        ["srvOffline"] = new[] { "Offline", "آفلاین" },
        ["srvNotSet"] = new[] { "Not set up", "راه‌اندازی نشده" },
        ["addPhone"] = new[] { "Add a phone from the internet", "افزودن گوشی از اینترنت" },
        ["step1"] = new[] { "Open ReyDesk on your phone.", "ReyDesk را روی گوشی باز کنید." },
        ["step2"] = new[] { "Tap Scan QR.", "روی «اسکن QR» بزنید." },
        ["step3"] = new[] { "Point the camera at this code, on the same network.", "دوربین را روی این کد بگیرید؛ هر دو دستگاه باید در یک شبکه باشند." },
        ["nearbyHint"] = new[] { "Phones on this network can connect after comparing a code.", "گوشی‌های همین شبکه می‌توانند با کد مقایسه وصل شوند." },
        ["disconnectAll"] = new[] { "Disconnect all", "قطع همه" },
        ["codeTitle"] = new[] { "Code for the internet", "کد اتصال از اینترنت" },
        ["codeOffNote"] = new[] { "Turn on the internet connection to get a code.", "برای دریافت کد، اتصال از اینترنت را روشن کنید." },
        ["codeWaiting"] = new[] { "Waiting for the server to issue a code.", "در انتظار صدور کد از سرور." },
        ["codeLeft"] = new[] { "{0} left", "{0} مانده" },
        ["pathLan"] = new[] { "LAN", "شبکه محلی" },
        ["pathDirect"] = new[] { "Direct", "مستقیم" },
        ["pathRelay"] = new[] { "Relay", "رله" },
        ["codeExpired"] = new[] { "The code expired. Generate a new one.", "کد منقضی شد. کد تازه بسازید." },
        ["codeFailed"] = new[] { "Could not create a code. Check the server connection.", "ساخت کد ممکن نشد. اتصال به سرور را بررسی کنید." },
        ["apprTitleNew"] = new[] { "Allow this phone to connect?", "به این گوشی اجازهٔ اتصال می‌دهید؟" },
        ["apprTitleTrusted"] = new[] { "A trusted phone wants to connect", "یک گوشی مورد اعتماد می‌خواهد وصل شود" },
        ["apprPhone"] = new[] { "Phone", "گوشی" },
        ["apprKeyId"] = new[] { "Key ID", "شناسهٔ کلید" },
        ["apprCompare"] = new[] { "Check that the phone shows the same code.", "مطمئن شوید گوشی همین کد را نشان می‌دهد." },
        ["cmpTitle"] = new[] { "Compare this code with the phone", "این کد را با گوشی مقایسه کنید" },
        ["cmpHint"] = new[] { "If the phone shows the same code, tap Same code on the phone. You approve the phone here next.", "اگر گوشی همین کد را نشان می‌دهد، روی گوشی «کد یکسان است» را بزنید. سپس اینجا گوشی را تأیید می‌کنید." },
        ["cmpCancel"] = new[] { "Cancel pairing", "لغو جفت‌سازی" },
        ["apprLan"] = new[] { "On your network", "در همین شبکه" },
        ["apprInternet"] = new[] { "Over the internet", "از طریق اینترنت" },
        ["apprAllowRemote"] = new[] { "Also allow this phone to connect over the internet", "به این گوشی اجازهٔ اتصال از اینترنت هم بدهید" },
        ["apprApprove"] = new[] { "Approve", "تأیید" },
        ["apprDecline"] = new[] { "Decline", "رد کردن" },
        ["apprExpires"] = new[] { "Expires in {0}", "{0} مانده" },
        ["inetServerUrl"] = new[] { "Server address", "نشانی سرور" },
        ["inetUrlHint"] = new[] { "Secure address, starts with wss://", "نشانی امن، با ⁦wss://⁩ شروع می‌شود" },
        ["inetUrlInvalid"] = new[] { "Enter a secure address that starts with wss://", "نشانی امن وارد کنید که با wss:// شروع شود" },
        ["inetApply"] = new[] { "Apply", "اعمال" },
        ["inetSaved"] = new[] { "Server address saved", "نشانی سرور ذخیره شد" },
        ["inetRelay"] = new[] { "Allow relay through the ReyDesk server", "اجازهٔ رله از طریق سرور ReyDesk" },
        ["inetRelayHelp"] = new[] { "Off by default. Turn it on only when a direct connection fails, usually when the phone is on mobile data: the stream then goes through the ReyDen relay server, which can be slower but stays encrypted end to end. Not needed on the same Wi-Fi.", "به‌طور پیش‌فرض خاموش است. فقط وقتی روشنش کنید که اتصال مستقیم برقرار نمی‌شود، معمولاً وقتی گوشی روی داده تلفن همراه است: جریان از سرور رلهٔ ReyDen می‌گذرد، ممکن است کندتر باشد ولی سرتاسری رمزگذاری‌شده می‌ماند. روی همان Wi-Fi لازم نیست." },
        ["inetAdvanced"] = new[] { "Advanced", "پیشرفته" },
        ["inetReset"] = new[] { "Reset to default", "بازنشانی به پیش‌فرض" },
        ["inetConnectsTo"] = new[] { "Connects to", "اتصال به" },
        ["srvUnreachable"] = new[] { "Server not reachable", "سرور در دسترس نیست" },
        ["inetPrivacyHost"] = new[] { "Your devices connect through ReyDesk's server ({0}), which only helps them find each other. It can see which devices connect and their IP addresses, but not your screen, files or typing. Every new phone still needs your approval on this PC.", "دستگاه‌های شما از طریق سرور ReyDesk ({0}) وصل می‌شوند که فقط به آن‌ها کمک می‌کند همدیگر را پیدا کنند. این سرور می‌تواند ببیند کدام دستگاه‌ها وصل می‌شوند و نشانی IP آن‌ها را، اما صفحه، فایل‌ها یا تایپ شما را نمی‌بیند. هر گوشی تازه باز هم به تأیید شما روی این رایانه نیاز دارد." },
        ["inetPrivacy"] = new[] { "The ReyDesk server only helps your devices find each other. It can see which devices connect and their IP addresses, but not your screen, files or typing. Every new phone still needs your approval on this PC.", "سرور ReyDesk فقط به دستگاه‌هایتان کمک می‌کند همدیگر را پیدا کنند. می‌تواند ببیند کدام دستگاه‌ها وصل می‌شوند و نشانی IP آن‌ها را، اما صفحه، فایل‌ها یا تایپ شما را نمی‌بیند. هر گوشی تازه باز هم به تأیید شما روی این رایانه نیاز دارد." },
        ["byReyDen"] = new[] { "A ReyDen service", "یک سرویس ReyDen" },
    };
}

using System.Collections.Generic;

namespace ReyDesk.Windows.Localization;

/// <summary>
/// Connection tutorial strings. Same column order as ConnectionStrings (En, Fa).
/// This table is the translation source for the Android tutorial.xml files: keys map to tut_ + key with "." as "_".
/// Every label quoted in a step is the exact value of its source key in the same locale; FA wraps labels in guillemets.
/// </summary>
internal static class TutorialStrings
{
    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        return Strings.RtlSafe(row[(int)Strings.Current]);
    }

    public static bool Has(string key) => Table.ContainsKey(key);

    private static readonly string[] Choose = { "Choose how to pair", "روش جفت‌سازی را انتخاب کنید" };
    private static readonly string[] Scan = { "Scan the QR code", "کد QR را اسکن کنید" };
    private static readonly string[] Compare = { "Compare the code", "کد را مقایسه کنید" };
    private static readonly string[] Approve = { "Approve on the PC", "روی رایانه تأیید کنید" };

    private static readonly Dictionary<string, string[]> Table = new()
    {
        // UI labels
        ["title"] = new[] { "How to connect", "نحوهٔ اتصال" },
        ["subtitle"] = new[] { "Step by step guides for pairing your phone with this PC.", "راهنمای گام‌به‌گام برای جفت‌سازی گوشی با این رایانه." },
        ["navItem"] = new[] { "How to connect", "نحوهٔ اتصال" },
        ["help"] = new[] { "Help", "راهنما" },
        ["pathWifi"] = new[] { "Same Wi-Fi", "همان Wi-Fi" },
        ["pathInternet"] = new[] { "Internet", "اینترنت" },
        ["pathMobile"] = new[] { "Mobile data", "داده تلفن همراه" },
        ["thisDevice"] = new[] { "On this PC", "روی این رایانه" },
        ["otherDevice"] = new[] { "On your phone", "روی گوشی شما" },
        ["stepOf"] = new[] { "Step {0} of {1}", "مرحلهٔ {0} از {1}" },
        ["allSteps"] = new[] { "All steps", "همهٔ مرحله‌ها" },
        ["back"] = new[] { "Back", "بازگشت" },
        ["next"] = new[] { "Next", "بعدی" },
        ["done"] = new[] { "Done", "انجام شد" },
        ["showMe"] = new[] { "Show me", "نشانم بده" },
        ["methodQr"] = new[] { "QR code", "کد QR" },
        ["methodNearby"] = new[] { "Nearby", "نزدیک" },
        ["methodCode"] = new[] { "Code", "کد" },
        ["alreadyPaired"] = new[] { "Already paired? Reconnect", "قبلاً جفت شده‌اید؟ اتصال مجدد" },
        ["openMobile"] = new[] { "Open the Mobile data guide", "راهنمای داده تلفن همراه را باز کنید" },
        ["openInternet"] = new[] { "Open the Internet guide", "راهنمای اینترنت را باز کنید" },
        ["firstRunTitle"] = new[] { "New to ReyDesk?", "با ReyDesk تازه‌کارید؟" },
        ["firstRunBody"] = new[] { "A short guide shows how to connect your phone to this PC.", "یک راهنمای کوتاه نشان می‌دهد چگونه گوشی خود را به این رایانه وصل کنید." },
        ["firstRunOpen"] = new[] { "Open the guide", "باز کردن راهنما" },
        ["tourStart"] = new[] { "Start guided tour", "شروع راهنمای گام‌به‌گام" },
        ["tourSkip"] = new[] { "Skip", "رد کردن" },
        ["dismiss"] = new[] { "Dismiss", "بستن" },
        ["settingsTitle"] = new[] { "Help", "راهنما" },
        ["settingsBody"] = new[] { "Step by step help for Wi-Fi, internet and mobile data.", "راهنمای گام‌به‌گام برای Wi-Fi، اینترنت و داده تلفن همراه." },
        ["settingsOpen"] = new[] { "Open the connection guide", "باز کردن راهنمای اتصال" },
        ["a11yStep"] = new[] { "Step {0} of {1}, {2}", "مرحلهٔ {0} از {1}، {2}" },

        // a: Same Wi-Fi
        ["a1.title"] = new[] { "Open ReyDesk on both devices", "در هر دو دستگاه ReyDesk را باز کنید" },
        ["a1.pc"] = new[] { "Open ReyDesk and stay on the Connect page. Both devices must use the same Wi-Fi.", "ReyDesk را باز کنید و در صفحهٔ «اتصال» بمانید. هر دو دستگاه باید به یک Wi-Fi وصل باشند." },
        ["a1.phone"] = new[] { "Open ReyDesk on the Connection tab. Check that the phone uses the same Wi-Fi as the PC.", "ReyDesk را در زبانهٔ «اتصال» باز کنید. مطمئن شوید گوشی به همان Wi-Fi رایانه وصل است." },

        ["a2.title"] = new[] { "Choose a mode", "یک حالت انتخاب کنید" },
        ["a2.pc"] = new[] { "Nothing to do here yet. The Connect page shows a QR code for the phone.", "اینجا هنوز کاری لازم نیست. صفحهٔ «اتصال» یک کد QR برای گوشی نشان می‌دهد." },
        ["a2.phone"] = new[] { "Choose Desktop to see and control the PC screen, or Control to use the PC without sharing its screen.", "«دسکتاپ» را انتخاب کنید تا صفحهٔ رایانه را ببینید و کنترل کنید، یا «کنترل» را برای استفاده از رایانه بدون نمایش صفحه." },

        ["a3.title"] = Choose,
        ["a3.pc"] = new[] { "Keep the Connect page open. It shows the QR code, and phones on this Wi-Fi can find the PC.", "صفحهٔ «اتصال» را باز نگه دارید. کد QR را نشان می‌دهد و گوشی‌های همین Wi-Fi می‌توانند رایانه را پیدا کنند." },
        ["a3.phone"] = new[] { "Pick QR code to scan the PC screen, or Nearby to pick the PC from a list.", "«کد QR» را بزنید تا صفحهٔ رایانه را اسکن کنید، یا «نزدیک» را بزنید تا رایانه را از فهرست انتخاب کنید." },

        ["a4q.title"] = Scan,
        ["a4q.pc"] = new[] { "Keep the QR code on screen. If it expired, click Generate new code.", "کد QR را روی صفحه نگه دارید. اگر منقضی شد، «ساخت کد جدید» را بزنید." },
        ["a4q.phone"] = new[] { "Tap Scan QR code in the ring and point the camera at the PC screen. Then tap Connect under the device name.", "در حلقه «اسکن کد QR» را بزنید و دوربین را روی صفحهٔ رایانه بگیرید. سپس زیر نام دستگاه «اتصال» را بزنید." },

        ["a4n.title"] = new[] { "Pick the PC from Nearby", "رایانه را از فهرست نزدیک انتخاب کنید" },
        ["a4n.pc"] = new[] { "Keep the Connect page open. The PC answers nearby phones only while this page is open.", "صفحهٔ «اتصال» را باز نگه دارید. رایانه فقط تا وقتی این صفحه باز است به گوشی‌های نزدیک پاسخ می‌دهد." },
        ["a4n.phone"] = new[] { "Under Nearby computers, tap Connect next to your PC. If it is missing, tap the refresh button.", "زیر «کامپیوترهای نزدیک»، کنار رایانهٔ خود «اتصال» را بزنید. اگر نبود، دکمهٔ تازه‌سازی را بزنید." },

        ["a5n.title"] = Compare,
        ["a5n.pc"] = new[] { "The PC shows Compare this code with the phone. Check that the phone shows the same 6 digits.", "رایانه «این کد را با گوشی مقایسه کنید» را نشان می‌دهد. بررسی کنید گوشی همان ۶ رقم را نشان دهد." },
        ["a5n.phone"] = new[] { "If both screens show the same 6 digits, tap Same code. If not, tap Different code.", "اگر هر دو صفحه همان ۶ رقم را نشان می‌دهند، «کد یکسان است» را بزنید. در غیر این صورت «کد متفاوت است» را بزنید." },

        ["a6.title"] = Approve,
        ["a6.pc"] = new[] { "In Allow this phone to connect?, click Approve. To reach this PC from outside later, also tick Also allow this phone to connect over the internet.", "در «به این گوشی اجازهٔ اتصال می‌دهید؟»، «تأیید» را بزنید. برای دسترسی بعدی از بیرون به این رایانه، «به این گوشی اجازهٔ اتصال از اینترنت هم بدهید» را هم تیک بزنید." },
        ["a6.phone"] = new[] { "Wait for the PC to approve. The session starts by itself when it is done.", "منتظر تأیید رایانه بمانید. جلسه پس از آن خودکار شروع می‌شود." },

        // b: Internet
        ["b1.title"] = new[] { "Turn on internet on both devices", "اینترنت را در هر دو دستگاه روشن کنید" },
        ["b1.pc"] = new[] { "On the Connect page, turn on Internet connection and wait until it shows Online.", "در صفحهٔ «اتصال»، «اتصال از اینترنت» را روشن کنید و صبر کنید «آنلاین» نشان داده شود." },
        ["b1.phone"] = new[] { "Open Settings, go to Internet connect and turn on Allow internet connections. Wait until it shows Online.", "«تنظیمات» را باز کنید، به «اتصال اینترنتی» بروید و «اجازه اتصال از اینترنت» را روشن کنید. صبر کنید «آنلاین» نشان داده شود." },

        ["b2.title"] = new[] { "Note the ReyDesk ID", "شناسهٔ ReyDesk را یادداشت کنید" },
        ["b2.pc"] = new[] { "Your ReyDesk ID is on the Connect page in three groups of four characters. Click Copy to share it.", "«شناسه ReyDesk» شما در صفحهٔ «اتصال» در سه گروه چهارکاراکتری است. برای اشتراک‌گذاری «کپی» را بزنید." },
        ["b2.phone"] = new[] { "On the phone, this ID goes into the Desk ID field, unless you scan the QR code.", "در گوشی، این شناسه در فیلد «شناسه Desk» وارد می‌شود، مگر اینکه کد QR را اسکن کنید." },

        ["b3.title"] = Choose,
        ["b3.pc"] = new[] { "You can show a 6 digit code, or let the phone scan the QR code on this page.", "می‌توانید یک کد ۶ رقمی نشان دهید یا بگذارید گوشی کد QR این صفحه را اسکن کند." },
        ["b3.phone"] = new[] { "Pick Code to type the ID and a code, or QR code if you can point the camera at the PC screen.", "«کد» را بزنید تا شناسه و یک کد وارد کنید، یا «کد QR» اگر می‌توانید دوربین را روی صفحهٔ رایانه بگیرید." },

        ["b4c.title"] = new[] { "Get a code on the PC", "روی رایانه یک کد بگیرید" },
        ["b4c.pc"] = new[] { "Click Add a phone from the internet. A 6 digit code appears with a timer; when it runs out, click Generate new code.", "«افزودن گوشی از اینترنت» را بزنید. یک کد ۶ رقمی با تایمر ظاهر می‌شود؛ وقتی تمام شد، «ساخت کد جدید» را بزنید." },
        ["b4c.phone"] = new[] { "Open the Over the internet card on the Connection tab.", "کارت «از طریق اینترنت» را در زبانهٔ «اتصال» باز کنید." },

        ["b5c.title"] = new[] { "Enter the ID and code", "شناسه و کد را وارد کنید" },
        ["b5c.pc"] = new[] { "Keep the code panel open until the phone connects.", "پنل کد را تا اتصال گوشی باز نگه دارید." },
        ["b5c.phone"] = new[] { "Type the ReyDesk ID in Desk ID and the 6 digits in Pairing code, then tap Connect.", "شناسه ReyDesk را در «شناسه Desk» و ۶ رقم را در «کد جفت‌سازی» بنویسید، سپس «اتصال» را بزنید." },

        ["b6c.title"] = Compare,
        ["b6c.pc"] = new[] { "The PC shows Compare this code with the phone. This check code is not the code you typed.", "رایانه «این کد را با گوشی مقایسه کنید» را نشان می‌دهد. این کد بررسی همان کدی نیست که وارد کردید." },
        ["b6c.phone"] = new[] { "If both screens show the same 6 digits, tap Same code. If they differ, tap Different code and start again.", "اگر هر دو صفحه همان ۶ رقم را نشان می‌دهند، «کد یکسان است» را بزنید. اگر فرق دارند، «کد متفاوت است» را بزنید و از اول شروع کنید." },

        ["b4q.title"] = Scan,
        ["b4q.pc"] = new[] { "Keep the QR code on the Connect page in view. With Internet connection on, it also works when the phone is not on your Wi-Fi.", "کد QR صفحهٔ «اتصال» را در دید نگه دارید. با روشن بودن «اتصال از اینترنت»، وقتی گوشی به Wi-Fi شما وصل نیست هم کار می‌کند." },
        ["b4q.phone"] = new[] { "Tap Scan QR code, point the camera at the PC screen, then tap Connect. You do not compare a code.", "«اسکن کد QR» را بزنید، دوربین را روی صفحهٔ رایانه بگیرید و سپس «اتصال» را بزنید. نیازی به مقایسهٔ کد نیست." },

        ["b7.title"] = Approve,
        ["b7.pc"] = new[] { "When the PC asks Allow this phone to connect?, click Approve. A phone's first pairing over the internet always needs approval on this PC.", "وقتی رایانه «به این گوشی اجازهٔ اتصال می‌دهید؟» را می‌پرسد، «تأیید» را بزنید. اولین جفت‌سازی هر گوشی از طریق اینترنت همیشه به تأیید در این رایانه نیاز دارد." },
        ["b7.phone"] = new[] { "Wait for the PC. On mobile data, if the session does not start within about 10 seconds, open the Mobile data guide.", "منتظر رایانه بمانید. در داده تلفن همراه، اگر جلسه ظرف حدود ۱۰ ثانیه شروع نشد، راهنمای «داده تلفن همراه» را باز کنید." },

        // c: Mobile data
        ["c1.title"] = new[] { "How connecting over the internet works", "اتصال از طریق اینترنت چطور کار می‌کند" },
        ["c1.pc"] = new[] { "Your phone and this PC find each other through the ReyDesk server using the Desk ID. The server only introduces them. Then the two devices try to open a direct, encrypted path between themselves. On home Wi-Fi this almost always works. Mobile carriers often put many users behind one shared address, which blocks direct paths.", "گوشی و این رایانه با شناسهٔ Desk و از طریق سرور ReyDesk یکدیگر را پیدا می‌کنند. سرور فقط آن‌ها را به هم معرفی می‌کند. سپس دو دستگاه تلاش می‌کنند یک مسیر مستقیم و رمزگذاری‌شده بین خودشان باز کنند. روی Wi-Fi خانه تقریباً همیشه جواب می‌دهد. اپراتورهای موبایل اغلب کاربران زیادی را پشت یک آدرس مشترک قرار می‌دهند و این مسیر مستقیم را می‌بندد." },
        ["c1.phone"] = new[] { "On the same Wi-Fi as the PC you need neither Internet connection nor relay. Use them only when the phone is on mobile data or another network. Turn on Internet connection on the PC first, then pair.", "اگر گوشی روی همان Wi-Fi رایانه است، نه به «اتصال از اینترنت» نیاز دارید و نه به رله. فقط وقتی از آن‌ها استفاده کنید که گوشی روی داده تلفن همراه یا شبکهٔ دیگری است. اول «اتصال از اینترنت» را روی رایانه روشن کنید، بعد جفت‌سازی کنید." },

        ["c2.title"] = new[] { "When to turn relay on", "چه وقت رله را روشن کنیم" },
        ["c2.pc"] = new[] { "Leave relay off if the connection works: it is off by default, a direct path is faster and keeps your stream off the server. Turn Allow relay on when pairing succeeds but the session stays on connecting or fails, usually on mobile data or strict networks. Open Settings, Internet and switch it on.", "اگر اتصال کار می‌کند رله را خاموش بگذارید: به‌طور پیش‌فرض خاموش است، مسیر مستقیم سریع‌تر است و جریان شما از سرور نمی‌گذرد. وقتی جفت‌سازی انجام می‌شود ولی جلسه در حالت اتصال می‌ماند یا شکست می‌خورد، معمولاً روی داده تلفن همراه یا شبکه‌های سخت‌گیر، «اجازهٔ رله» را روشن کنید. «تنظیمات»، بخش «اینترنت» را باز کنید و روشنش کنید." },
        ["c2.phone"] = new[] { "Relay is a choice you make on each device: turn Allow relay on in Settings on the phone too. If the direct path fails while relay is off, the app shows a hint after about 15 seconds. Turn relay off again when you are back on Wi-Fi.", "رله انتخابی است که روی هر دستگاه جداگانه می‌کنید: «اجازه رله» را در تنظیمات گوشی هم روشن کنید. اگر مسیر مستقیم برقرار نشود و رله خاموش باشد، برنامه بعد از حدود ۱۵ ثانیه یک راهنما نشان می‌دهد. وقتی به Wi-Fi برگشتید رله را دوباره خاموش کنید." },

        ["c3.title"] = new[] { "What relay does and costs", "رله چه می‌کند و چه هزینه‌ای دارد" },
        ["c3.pc"] = new[] { "With relay, the stream is forwarded by the ReyDesk relay server because the two devices cannot reach each other. It stays encrypted end to end, so the server cannot see your screen. It can be slower, uses the server's bandwidth, and works only while the server offers it. Direct paths are always tried first.", "با رله، جریان تصویر از طریق سرور رلهٔ ReyDesk رد می‌شود، چون دو دستگاه نمی‌توانند مستقیم به هم برسند. جریان همچنان سرتاسری رمزگذاری‌شده است و سرور صفحهٔ شما را نمی‌بیند. ممکن است کندتر باشد، از پهنای باند سرور استفاده می‌کند و فقط تا وقتی که سرور آن را ارائه می‌دهد کار می‌کند. مسیرهای مستقیم همیشه اول امتحان می‌شوند." },
        ["c3.phone"] = new[] { "While the stream goes through the server, the path badge shows Relay. If the badge never appears and the session keeps connecting, check that relay is on for both the phone and the PC.", "وقتی جریان از سرور می‌گذرد، نشان مسیر عبارت «رله» را نشان می‌دهد. اگر نشان ظاهر نشد و جلسه در حال اتصال ماند، بررسی کنید رله هم روی گوشی و هم روی رایانه روشن باشد." },

        ["c4.title"] = new[] { "Connect as usual", "مثل همیشه وصل شوید" },
        ["c4.pc"] = new[] { "Pair or reconnect as in the Internet guide, with Internet connection on.", "مانند راهنمای اینترنت جفت‌سازی یا اتصال مجدد کنید، در حالی که «اتصال از اینترنت» روشن است." },
        ["c4.phone"] = new[] { "Use Scan QR code or the Over the internet card, as in the Internet guide.", "مانند راهنمای اینترنت از «اسکن کد QR» یا کارت «از طریق اینترنت» استفاده کنید." },

        // d: after pairing
        ["d1.title"] = new[] { "Use the PC from your phone", "از گوشی از رایانه استفاده کنید" },
        ["d1.pc"] = new[] { "The Connect page shows the connected phone and how it is connected. Click Disconnect all to end every session.", "صفحهٔ «اتصال» گوشی متصل و نحوهٔ اتصال آن را نشان می‌دهد. برای پایان دادن به همهٔ جلسه‌ها «قطع همه» را بزنید." },
        ["d1.phone"] = new[] { "In Desktop mode you see the PC screen and control it by touch. Use the toolbar to end the session.", "در حالت «دسکتاپ» صفحهٔ رایانه را می‌بینید و با لمس کنترلش می‌کنید. برای پایان جلسه از نوار ابزار استفاده کنید." },

        ["d2.title"] = new[] { "Reconnect later", "بعداً دوباره وصل شوید" },
        ["d2.pc"] = new[] { "When A trusted phone wants to connect appears, click Approve. Over the internet, the phone must have been allowed for the internet when you approved it.", "وقتی «یک گوشی مورد اعتماد می‌خواهد وصل شود» ظاهر شد، «تأیید» را بزنید. از طریق اینترنت، گوشی باید هنگام تأیید برای اینترنت مجاز شده باشد." },
        ["d2.phone"] = new[] { "Open the Devices tab and tap Reconnect on your PC. Over the internet, Allow internet connections must be on on both devices.", "زبانهٔ «دستگاه‌ها» را باز کنید و روی رایانهٔ خود «اتصال مجدد» را بزنید. از طریق اینترنت، «اجازه اتصال از اینترنت» باید در هر دو دستگاه روشن باشد." },

        ["d3.title"] = new[] { "Reconnect without a prompt", "بدون پرسش دوباره وصل شوید" },
        ["d3.pc"] = new[] { "Optional: on Trusted devices, turn on Allow automatic reconnect on the phone's card and confirm. Keep it off for shared phones.", "اختیاری: در «دستگاه‌های مورد اعتماد»، «اجازهٔ اتصال مجدد خودکار» را در کارت گوشی روشن و تأیید کنید. برای گوشی‌های مشترک آن را خاموش بگذارید." },
        ["d3.phone"] = new[] { "On the same Wi-Fi, tap Reconnect next to the PC under Nearby computers. The PC then lets the phone in without asking.", "در همان Wi-Fi، زیر «کامپیوترهای نزدیک» کنار رایانه «اتصال مجدد» را بزنید. سپس رایانه بدون پرسش گوشی را راه می‌دهد." },

        ["d4.title"] = new[] { "Trusted devices", "دستگاه‌های مورد اعتماد" },
        ["d4.pc"] = new[] { "Trusted devices lists every phone you approved. You can rename one, change automatic reconnect, or forget it so it has to pair again.", "«دستگاه‌های مورد اعتماد» همهٔ گوشی‌هایی را که تأیید کرده‌اید فهرست می‌کند. می‌توانید نام یکی را تغییر دهید، اتصال مجدد خودکار را عوض کنید یا آن را حذف کنید تا دوباره جفت شود." },
        ["d4.phone"] = new[] { "The Devices tab lists the PCs this phone trusts. If you forget a PC there, pair again with the QR code or a code.", "زبانهٔ «دستگاه‌ها» رایانه‌هایی را که این گوشی به آن‌ها اعتماد دارد فهرست می‌کند. اگر رایانه‌ای را آنجا حذف کنید، دوباره با کد QR یا یک کد جفت‌سازی کنید." },
    };
}

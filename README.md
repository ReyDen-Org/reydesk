# ReyDesk

ReyDesk lets you see and control your Windows PC from your Android phone, send
files between them, and read your phone's notifications on the PC. It is made by
[Reyden](https://reyden.ir).

**Download:** https://reyden.ir/reydesk (Windows installer and Android app)

**License:** [MIT](LICENSE). You are free to view, use, fork and modify the code.
Third-party components are listed in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
The repository is public for transparency and trust.

**Security:** please report vulnerabilities privately to **support@reyden.ir**
and give us time to fix them before publishing details.

## What it does

- **Remote desktop from your phone.** View the Windows screen on Android and
  control it with touch, pointer and keyboard input. Video and input travel over
  an encrypted WebRTC connection.
- **Pairing you can verify.** A new phone is paired by scanning a QR code shown on
  the PC, and the PC must approve it. The QR carries the PC's exact public key, so
  the phone only talks to that PC. Each device keeps its own key pair; there is no
  account.
- **On the same network or over the internet.** On a local network the devices
  connect directly. Away from home, each PC has a ReyDesk ID; the ReyDesk
  rendezvous server (`rdv.reyden.ir`) only introduces the two devices and never
  makes trust decisions. The connection is peer-to-peer; relayed traffic is off by
  default.
- **Phone Hub.** See current notifications from apps you choose on the PC.
  Sharing is off until you turn it on, previews start off, and one-time codes and
  banking notifications stay hidden.
- **File transfer.** Send a file in either direction (up to 100 MiB), checked
  with SHA-256 on arrival.
- **Approved actions.** Run apps and shortcuts that you registered on the PC, each
  one confirmed on the PC. There is no remote command shell.

Languages: English and Persian, with right-to-left layout.

## Repository layout

| Folder | Contents |
|---|---|
| `Windows/ReyDesk.Windows` | Windows app (C#, WinUI 3, Windows App SDK) |
| `Android/` | Android app (Kotlin, Jetpack Compose) |
| `Protocol/` | Protobuf schema shared by all parts (`Protocol/reydesk/v1`) |
| `Backend/rendezvous` | Rendezvous (signaling) server (Go, PostgreSQL, Docker) |
| `Installer/` | Inno Setup script for the Windows installer |
| `Tests/` | Standalone test projects for Windows, Android and protocol vectors |
| `scripts/` | Version, brand and release helpers |
| `docs/` | Build notes, architecture, protocol and identity design |

Design references: [`docs/architecture.md`](docs/architecture.md),
[`docs/protocol.md`](docs/protocol.md) and
[`docs/identity-and-pairing.md`](docs/identity-and-pairing.md).

## Building from source

Full notes are in [`docs/BUILD.md`](docs/BUILD.md).

- **Windows app:** .NET 8 SDK and Windows App Runtime 2.4.
  `dotnet build Windows/ReyDesk.Windows/ReyDesk.Windows.csproj -c Debug -p:Platform=x64`
- **Android app:** JDK from Android Studio, then from `Android/`:
  `./gradlew :app:assembleDebug`. Release signing reads `Android/keystore.properties`
  or environment variables, which are not part of this repository.
- **Rendezvous server:** from `Backend/rendezvous`,
  `docker compose -f compose.yaml -f compose.standalone.yaml up -d --build`
  starts a local test instance with its own PostgreSQL (see `Backend/rendezvous/README.md`).
- **Tests:** `dotnet run --project Tests/<Name>/<Name>.csproj`, or the `run.ps1`
  script in the Kotlin test folders.

`VERSION` in the project root is the single source of the app version.

## Trademarks

The ReyDesk and ReyDen names and logos are trademarks of ReyDen. The MIT License
covers the code, not the names or logos: they are not licensed for use on derived
or modified products. If you distribute a fork, give it a different name and
different branding.

## درباره ReyDesk

ReyDesk برنامه‌ای از [ری‌دن](https://reyden.ir) است که با آن می‌توانید صفحه کامپیوتر ویندوزی خود را از گوشی اندروید ببینید و کنترل کنید، فایل جابه‌جا کنید و اعلان‌های گوشی را روی کامپیوتر ببینید. نسخه رسمی را از https://reyden.ir/reydesk دریافت کنید. کد این مخزن برای شفافیت و اعتماد منتشر شده و با مجوز MIT در دسترس است: می‌توانید آن را ببینید، استفاده کنید، فورک کنید و تغییر دهید. نام و نشان ReyDesk و ReyDen برای محصولات مشتق‌شده مجاز نیست. متن کامل مجوز در فایل `LICENSE` آمده است. مشکلات امنیتی را لطفا به صورت خصوصی به support@reyden.ir گزارش دهید.

# Third-party notices

ReyDesk is released under the MIT License (see `LICENSE`). It uses the
third-party components below, each under its own license; those licenses apply to
the components themselves. Versions are the ones referenced by the build files on
2026-11-06. Licenses marked "verify" were identified from the package's public
project page and should be checked against the exact package version before each release.

## Windows app (`Windows/ReyDesk.Windows`, NuGet)

| Component | Version | License |
|---|---|---|
| Microsoft.WindowsAppSDK (and its Runtime, WinUI, Foundation and related sub-packages) | 2.4.0 | Microsoft Software License Terms for the Windows App SDK (redistributable runtime) |
| Microsoft.Windows.SDK.BuildTools, BuildTools.WinApp, BuildTools.MSIX | build only | Microsoft Software License Terms |
| Microsoft.Web.WebView2 (transitive) | 1.0.3719.77 | Microsoft WebView2 SDK license (BSD-style), verify |
| SIPSorcery, SIPSorceryMedia.Abstractions | 10.0.16 | BSD 3-Clause with an Additional Use Restriction (full text below) |
| SIPSorcery.WebSocketSharp | 0.0.1 | MIT |
| BouncyCastle.Cryptography (transitive) | 2.7.0 | MIT |
| Concentus (transitive) | 2.2.2 | BSD 3-Clause (file `LICENSE` in the package) |
| DnsClient (transitive) | 1.8.0 | Apache 2.0 |
| Makaretu.Dns, Makaretu.Dns.Multicast (transitive) | 2.0.1 / 0.27.0 | MIT |
| IPNetwork2 (transitive) | 2.1.2 | BSD 2-Clause, verify |
| SimpleBase (transitive) | 1.3.1 | Apache 2.0 |
| Common.Logging, Common.Logging.Core (transitive) | 3.4.1 | Apache 2.0 |
| Tmds.LibC (transitive) | 0.2.0 | MIT |
| QRCoder | 1.8.0 | MIT |
| Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX, Vortice.Mathematics | 3.8.3 / 2.1.0 | MIT |
| SharpGen.Runtime, SharpGen.Runtime.COM (transitive) | 2.4.2-beta | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.11 | MIT |
| Google.Protobuf | 3.36.0 | BSD 3-Clause |
| Grpc.Tools (protobuf code generation, build only) | 2.83.0 | Apache 2.0 |

### SIPSorcery license (Windows app)

The license file (`LICENSE.md`) shipped with SIPSorcery 10.0.16 is reproduced
below, without its third section. That section is the GNU LGPL 2.1 text, which
applies only to the `SIPSorceryMedia.FFmpeg` package; ReyDesk does not use that
package. The full file is in the NuGet package.

> **1. BSD 3-Clause "New" or "Revised" License**
>
> Copyright (c) 2006-2026 Aaron Clauson
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
> 3. Neither the name "SIP Sorcery," nor "Aaron Clauson," nor the names of any contributors may be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE, ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
>
> **2. Additional Use Restriction**
>
> This software **must not be used** to further the apartheid policies of the state of Israel. Use, modification, or distribution **inside** Israel and the Occupied Territories is not permitted until the demands of the Boycott, Divestment and Sanctions (BDS) movement have been met:
>
> * Israel has ended the occupation and colonization of all Arab lands occupied in 1967 and dismantled the Wall;
> * Arab-Palestinian citizens of Israel have been granted full equality; and
> * Palestinian refugees have obtained the right to return to their homes and properties as stipulated in UN Resolution 194.
>
> For all other geographic regions **outside** of Israel and the Occupied Territories, use, modification, and distribution are permitted under the terms of the **BSD 3-Clause "New" or "Revised" License** above (Section 1), with **no additional restriction on commercial use** and **no requirement to license derivative works under any particular terms**.
>
> This restriction is **not** intended to limit the rights of Israelis or any other people residing outside of Israel and the Occupied Territories.
>
> In any instance where the BSD 3-Clause License conflicts with the above restriction, the above restriction shall be interpreted as superior, and all other non-conflicting provisions of the BSD 3-Clause license shall remain in effect.

Because the Windows app includes SIPSorcery, this restriction applies to the
SIPSorcery parts of every ReyDesk Windows build and of builds derived from it.

## Android app (`Android/`, Maven)

| Component | Version | License |
|---|---|---|
| AndroidX (core-ktx, lifecycle, activity-compose, navigation-compose, appcompat, datastore-preferences) | see `Android/gradle/libs.versions.toml` | Apache 2.0 |
| Jetpack Compose (BOM 2026.08.00: ui, ui-graphics, material3, tooling) | 2026.08.00 | Apache 2.0 |
| Kotlin standard library and Compose compiler plugin | 2.3.20 | Apache 2.0 |
| protobuf-kotlin-lite | 4.29.1 | BSD 3-Clause |
| protobuf Gradle plugin (build only) | 0.10.0 | BSD 3-Clause |
| zxing-android-embedded (and ZXing core) | 4.3.0 | Apache 2.0 |
| io.github.webrtc-sdk:android (prebuilt Google WebRTC) | 144.7559.14 | BSD 3-Clause, plus the permissive licenses of the libraries bundled in libwebrtc (BoringSSL, libvpx, libyuv, Opus, usrsctp and others), verify the full list from the AAR |
| OkHttp (and Okio) | 4.12.0 | Apache 2.0 |
| Android Gradle Plugin and Gradle wrapper (build only) | 9.3.2 / wrapper jar | Apache 2.0 |

## Rendezvous server (`Backend/rendezvous`, Go modules)

| Component | Version | License |
|---|---|---|
| github.com/coder/websocket | 1.8.13 | ISC |
| github.com/jackc/pgx/v5 | 5.12.0 | MIT |
| github.com/jackc/pgpassfile, pgservicefile, puddle/v2 | see `go.mod` | MIT |
| google.golang.org/protobuf | 1.36.12 | BSD 3-Clause |
| golang.org/x/sync, golang.org/x/text | see `go.mod` | BSD 3-Clause |

Container images referenced by the compose files (`postgres`, `coturn/coturn`,
`golang`, `gcr.io/distroless/static`) are not part of this repository and are
pulled under their own licenses.

## Fonts

| Font | Files | License |
|---|---|---|
| Inter | `Windows/ReyDesk.Windows/Assets/Fonts/Inter-*.ttf`, `Android/app/src/main/res/font/inter_*.ttf` | SIL Open Font License 1.1 (`OFL-Inter.txt`) |
| JetBrains Mono | `.../JetBrainsMono-*.ttf`, `.../jetbrains_mono_*.ttf` | SIL Open Font License 1.1 (`OFL-JetBrainsMono.txt`) |
| Vazirmatn | `.../Vazirmatn-*.ttf`, `.../vazirmatn_*.ttf` | SIL Open Font License 1.1 (`OFL-Vazirmatn.txt`) |

The fonts stay under the OFL (the license texts are in the font folders and the
Android `assets/licenses` folder); the MIT License of ReyDesk does not replace it.

## Icons and images

| Item | Location | License or rights |
|---|---|---|
| Lucide icon path data | `Windows/ReyDesk.Windows/Theme/Icons.xaml`, `Android/.../ui/components/AppGlyph.kt` | ISC License (text below), with portions MIT (Feather) |
| ReyDesk and ReyDen logos, app icons, wordmarks, installer art | `brand/`, `assets/`, `docs/assets/`, `Installer/art/`, app icon resources | Copyright (c) 2026 ReyDen. The code is MIT; the names and logos are trademarks and are not licensed for use on derived products (see README, "Trademarks") |

The device artwork in the Windows app is drawn in code and contains no third-party images.

Product names and trademarks (Windows, Android and others mentioned in the source or docs)
belong to their owners.

### Lucide license

ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

The Feather portions are under the MIT License, Copyright (c) 2013-2022 Cole Bemis; the MIT text is the same as in the `LICENSE` file of this repository with that copyright line.

## Build tools

Inno Setup (installer compiler, not shipped in this repository) is used under the
Inno Setup License. `Installer/Persian.isl` is a ReyDesk translation file.

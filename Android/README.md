# ReyDesk for Android

Kotlin and Jetpack Compose client: pairing (QR, nearby, ReyDesk ID), remote screen
viewer with touch input, Phone Hub notification sharing and file transfer.
English and Persian (RTL).

## Build

JDK 17+ (for example the one bundled with Android Studio) and the Android SDK are
required. From this folder:

```sh
./gradlew :app:assembleDebug
```

Release builds read signing details from `keystore.properties` or environment
variables (see `app/build.gradle.kts` and `../scripts/make-release.ps1`); neither
is part of the repository. Versions are pinned in [`gradle/libs.versions.toml`](gradle/libs.versions.toml).
`../VERSION` is the single source of the app version.

## Structure

```
app/src/main/kotlin/ir/reyden/reydesk/
├── identity/ pairing/ rendezvous/ webrtc/   keys, pairing flows, signaling, WebRTC session
├── companion/ settings/                     Phone Hub, file transfer, preferences
└── ui/                                      Compose screens, theme tokens (ui/theme/Color.kt), tutorial
```

The protobuf schema lives in [`../Protocol/reydesk`](../Protocol/reydesk/). The
`syncProtoSchema` Gradle task copies it into `app/src/main/proto/reydesk` at build
time (git-ignored, regenerated every build). `copyLicenseAssets` likewise copies the
root `LICENSE` and `THIRD_PARTY_NOTICES.md` into `app/src/main/assets/licenses` for the
in-app licenses screen.

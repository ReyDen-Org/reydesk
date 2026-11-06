# Build and test

Run from the project root. If NuGet or Gradle cannot reach their feeds from your network, set a proxy (see below) or restore offline.

## Windows app
    export HTTP_PROXY=... HTTPS_PROXY=... NO_PROXY=localhost,127.0.0.1   # only if NuGet gives NU1301
    dotnet build Windows/ReyDesk.Windows/ReyDesk.Windows.csproj -c Debug -p:Platform=x64
    # run: Windows/ReyDesk.Windows/bin/x64/Debug/net8.0-windows10.0.26100.0/win-x64/ReyDesk.exe
Needs Windows App Runtime 2.4 (Main+Singleton+DDLM).

## Android (from Android/)
    export JAVA_HOME="<path to the Android Studio jbr folder>"
    ./gradlew --offline :app:assembleDebug
    adb install -r app/build/outputs/apk/debug/app-debug.apk
Gradle 9.7.1 offline; if the daemon misbehaves: `./gradlew --stop`, add `--no-daemon`.

## Tests (standalone, not in a solution; pass = exit 0 and a PASS line)
    dotnet run --project Tests/<Name>/<Name>.csproj          # .NET tests
    powershell -NoProfile -ExecutionPolicy Bypass -File Tests/<Name>/run.ps1   # Kotlin tests; set JAVA_HOME and GRADLE_HOME (a Gradle install, for its bundled Kotlin compiler) or pass -JavaHome / -GradleHome
    powershell -NoProfile -ExecutionPolicy Bypass -File Tests/PairingDiscovery/run-pin.ps1
`-ExecutionPolicy Bypass` is needed only if your machine blocks unsigned local scripts.

## Rendezvous server (internet access)
Both apps ship a built-in default, `wss://rdv.reyden.ir/v1/ws` (Windows `AppSettingsService.DefaultRendezvousUrl`, Android `DEFAULT_RENDEZVOUS_URL` in `RemoteSettings.kt`). The stored value is an override; empty means the default. Users never type an address: Settings > Internet only has the on/off switch. The address field lives in a collapsed "Advanced" row with "Reset to default". For local testing paste `ws://127.0.0.1:13260/v1/ws` there (Debug builds only; Android emulator: `ws://10.0.2.2:13260/v1/ws`). If the server cannot be reached the Internet card shows "Server not reachable" when the switch is on.

## Versioning
`VERSION` at the project root ("1.0.0") is the single source of truth for Windows and Android. Windows reads it in `ReyDesk.Windows.csproj` (Version, AssemblyVersion, FileVersion; InformationalVersion = version + short git sha) and the app reads it through `Platform/AppInfo.Version`. Never hard-code a version string. To release: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bump-version.ps1 -Version 0.3.0` (edits VERSION and prints the `git tag v0.3.0` to run by hand; it does not commit or tag).

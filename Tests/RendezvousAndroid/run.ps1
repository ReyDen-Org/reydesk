param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-rendezvous-android'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'rendezvous.jar'
$rv = "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/rendezvous"
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$rv/PinnedHostKeyTrustManager.kt" "$rv/RelayPipe.kt" "$rv/TlsOverPipe.kt" "$rv/Backoff.kt" "$rv/RendezvousUrl.kt" "$rv/ReconnectPolicy.kt" "$PSScriptRoot/PolicyTests.kt" "$PSScriptRoot/TlsOverPipeTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Rendezvous compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" TlsOverPipeTestsKt "$JavaHome/bin/keytool.exe" "$outputRoot/keys"
if ($LASTEXITCODE -ne 0) { throw 'Rendezvous pipe tests failed.' }

param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-receiver-stats'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'receiver-regression.jar'
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/webrtc/ReceiverStatsCounters.kt" `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/settings/StreamPreferences.kt" `
    "$PSScriptRoot/ReceiverStatsTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Receiver regression compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" ReceiverStatsTestsKt
if ($LASTEXITCODE -ne 0) { throw 'Receiver regression tests failed.' }

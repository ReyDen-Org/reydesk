param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-companion-privacy'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'companion-privacy.jar'
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/companion/NotificationPrivacy.kt" `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/companion/FileTransferPolicy.kt" `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/settings/StreamPreferences.kt" `
    "$PSScriptRoot/PrivacyTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Companion privacy compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" PrivacyTestsKt
if ($LASTEXITCODE -ne 0) { throw 'Companion privacy tests failed.' }

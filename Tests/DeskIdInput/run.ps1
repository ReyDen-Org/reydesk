param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-deskid-input'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'deskid-input.jar'
$src = "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk"
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$src/identity/DeskIdCodec.kt" "$src/ui/text/Digits.kt" "$src/ui/connection/DeskIdInputLogic.kt" "$PSScriptRoot/DeskIdInputTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'DeskIdInput compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" DeskIdInputTestsKt
if ($LASTEXITCODE -ne 0) { throw 'DeskIdInput tests failed.' }

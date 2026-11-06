param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-viewport'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'viewport-regression.jar'
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/ui/session/VideoViewport.kt" `
    "$PSScriptRoot/ViewportTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Viewport regression compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" ViewportTestsKt
if ($LASTEXITCODE -ne 0) { throw 'Viewport regression tests failed.' }

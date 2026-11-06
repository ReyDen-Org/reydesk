param([string]$JavaHome = $env:JAVA_HOME, [string]$GradleHome = $env:GRADLE_HOME)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-pin'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
$testJar = Join-Path $outputRoot 'pin-regression.jar'
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $stdlib -d $testJar "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/pairing/LanHostMetadata.kt" "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/identity/DeskIdCodec.kt" "$PSScriptRoot/PinTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'PIN regression compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" PinTestsKt
if ($LASTEXITCODE -ne 0) { throw 'PIN regression tests failed.' }

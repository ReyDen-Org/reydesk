param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-identity-v2'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
if (-not $stdlib) { throw 'Existing Gradle Kotlin standard library not found.' }
$testJar = Join-Path $outputRoot 'identity-v2.jar'
$id = "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk/identity"
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $stdlib -d $testJar `
    "$id/DeskIdCodec.kt" "$id/IdentityPayloads.kt" "$id/IdentitySignatures.kt" "$PSScriptRoot/IdentityV2Tests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Identity v2 compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$stdlib" IdentityV2TestsKt "$projectRoot/Tests/Vectors/identity-v2.json"
if ($LASTEXITCODE -ne 0) { throw 'Identity v2 tests failed.' }

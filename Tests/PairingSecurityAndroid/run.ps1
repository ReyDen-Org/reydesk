param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome = $env:GRADLE_HOME
)
# Needs Android/app/build/generated/.../generateDebugProto (run `gradlew :app:generateDebugProto` once).
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'reydesk-pairing-security'
Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$outputRoot/proto" -Force | Out-Null
$stdlib = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlin-stdlib-*.jar' | Select-Object -First 1).FullName
$coroutines = (Get-ChildItem -LiteralPath "$GradleHome/lib" -Filter 'kotlinx-coroutines-core-jvm-*.jar' | Select-Object -First 1).FullName
$lite = (Get-ChildItem -Path "$env:USERPROFILE/.gradle/caches/modules-2/files-2.1/com.google.protobuf/protobuf-javalite" -Filter 'protobuf-javalite-4.29.1.jar' -Recurse | Select-Object -First 1).FullName
if (-not $stdlib -or -not $coroutines -or -not $lite) { throw 'Missing stdlib, coroutines or protobuf-javalite jar.' }
$genRoot = "$projectRoot/Android/app/build/generated/java/generateDebugProto/java"
if (-not (Test-Path "$genRoot/ir/reyden/reydesk/protocol/v1")) { throw 'Generated protobuf sources missing: run gradlew :app:generateDebugProto.' }
$javaSources = (Get-ChildItem -Path "$genRoot/ir/reyden/reydesk/protocol/v1" -Filter '*.java').FullName
& "$JavaHome/bin/javac.exe" -nowarn -cp $lite -d "$outputRoot/proto" @javaSources
if ($LASTEXITCODE -ne 0) { throw 'Generated protobuf compilation failed.' }
$kt = "$projectRoot/Android/app/src/main/kotlin/ir/reyden/reydesk"
$cp = "$stdlib;$coroutines;$lite;$outputRoot/proto"
$testJar = Join-Path $outputRoot 'pairing-security.jar'
& "$JavaHome/bin/java.exe" -cp "$GradleHome/lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -classpath $cp -d $testJar `
    "$kt/identity/DeskIdCodec.kt" "$kt/identity/IdentityPayloads.kt" "$kt/identity/IdentitySignatures.kt" `
    "$kt/pairing/HostTrust.kt" "$kt/pairing/LanHostMetadata.kt" "$kt/pairing/PairingSignature.kt" `
    "$kt/pairing/PairingSession.kt" "$kt/pairing/EnvelopeFraming.kt" "$PSScriptRoot/PairingSecurityTests.kt"
if ($LASTEXITCODE -ne 0) { throw 'Pairing security compilation failed.' }
& "$JavaHome/bin/java.exe" -cp "$testJar;$cp" PairingSecurityTestsKt "$projectRoot/Tests/Vectors/identity-v2.json"
if ($LASTEXITCODE -ne 0) { throw 'Pairing security tests failed.' }

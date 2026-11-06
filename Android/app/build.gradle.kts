import java.util.Properties

plugins {
    // No version here: AGP is already on the classpath via the root project's
    // buildscript{} block (see root build.gradle.kts) - Google's plugin-marker
    // artifact for this AGP release 404s, so it can't be applied via
    // alias(libs.plugins.android.application) like the other plugins below.
    id("com.android.application")
    // AGP 9+ has built-in Kotlin support - applying org.jetbrains.kotlin.android
    // alongside it fails with an opaque diagnostics error. Do not re-add it.
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.protobuf)
}

// Single source of truth: ReyDesk/VERSION (major.minor.patch). versionCode = major*10000 + minor*100 + patch.
val appVersion: String = rootDir.resolve("../VERSION").readText().trim()
val appVersionCode: Int = appVersion.split(".").map { it.toInt() }.let { (ma, mi, pa) -> ma * 10000 + mi * 100 + pa }

// Release signing: Android/keystore.properties (git-ignored: storeFile, storePassword, keyAlias, keyPassword)
// or the REYDESK_KEYSTORE / REYDESK_KEYSTORE_PASSWORD / REYDESK_KEY_ALIAS / REYDESK_KEY_PASSWORD env vars.
val keystoreProps = Properties().apply {
    val f = rootDir.resolve("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}
fun signingValue(prop: String, env: String): String? = keystoreProps.getProperty(prop) ?: System.getenv(env)

android {
    namespace = "ir.reyden.reydesk"
    compileSdk = 37

    defaultConfig {
        applicationId = "ir.reyden.reydesk"
        // Spec: "Android 10+" -> API 29.
        minSdk = 29
        // Deliberately NOT 37 (compileSdk stays 37): Android 17/API 37 introduces
        // ACCESS_LOCAL_NETWORK, which blocks LAN sockets/mDNS/WebRTC-on-LAN by default -
        // exactly what this app depends on. Revisit once that permission is added and
        // requested properly.
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersion
        buildConfigField("String", "APP_VERSION", "\"$appVersion\"")
    }

    signingConfigs {
        create("release") {
            signingValue("storeFile", "REYDESK_KEYSTORE")?.let { storeFile = file(it) }
            storePassword = signingValue("storePassword", "REYDESK_KEYSTORE_PASSWORD")
            keyAlias = signingValue("keyAlias", "REYDESK_KEY_ALIAS")
            keyPassword = signingValue("keyPassword", "REYDESK_KEY_PASSWORD")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (signingValue("storeFile", "REYDESK_KEYSTORE") != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // No `kotlinOptions { jvmTarget }` block: that DSL is contributed by the
    // org.jetbrains.kotlin.android plugin, which AGP 9's built-in Kotlin support
    // replaces (see plugins {} above). compileOptions above already pins JVM 17.

    // lintVital needs lint artifacts that are not cached offline on the dev machine; release lint is not part of the publish flow.
    lint { checkReleaseBuilds = false }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

protobuf {
    protoc {
        artifact = "com.google.protobuf:protoc:${libs.versions.protobufKotlinLite.get()}"
    }
    generateProtoTasks {
        all().forEach { task ->
            task.builtins {
                // "kotlin" alone only generates the DSL-builder wrappers - they need the
                // underlying Java message classes too. Both use the lite runtime to match
                // the `protobuf-kotlin-lite` dependency below.
                create("java") { option("lite") }
                create("kotlin") { option("lite") }
            }
        }
    }
}

// Pointing protobuf-gradle-plugin's Android sourceSet at an external directory
// (android.sourceSets.main.proto.srcDir) is fragile against AGP 9's new DSL from
// Kotlin build scripts. Instead, sync the canonical schema
// from Protocol/reydesk into this module's default `src/main/proto` convention,
// which the plugin picks up with zero custom sourceSet wiring. The synced copy is
// gitignored - Protocol/reydesk remains the only maintained source.
val syncProtoSchema = tasks.register<Sync>("syncProtoSchema") {
    from(rootDir.resolve("../Protocol/reydesk"))
    into(layout.projectDirectory.dir("src/main/proto/reydesk"))
}

tasks.named("preBuild") {
    dependsOn(syncProtoSchema)
}

// preBuild ordering alone isn't enough: Gradle's task validation correctly flags
// generate*Proto as reading src/main/proto without an explicit dependency on the
// task that populates it (implicit-dependency problem, not just ordering).
tasks.matching { it.name.startsWith("generate") && it.name.endsWith("Proto") }.configureEach {
    dependsOn(syncProtoSchema)
}

// The MIT licence and third-party notices live once in the repository root; copy them
// into the app's licence assets at build time (Copy, not Sync: the OFL files stay).
val copyLicenseAssets = tasks.register<Copy>("copyLicenseAssets") {
    from(rootDir.resolve("../LICENSE")) { rename { "ReyDesk-MIT.txt" } }
    from(rootDir.resolve("../THIRD_PARTY_NOTICES.md"))
    into(layout.projectDirectory.dir("src/main/assets/licenses"))
}

tasks.named("preBuild") {
    dependsOn(copyLicenseAssets)
}

tasks.matching { it.name.startsWith("merge") && it.name.endsWith("Assets") }.configureEach {
    dependsOn(copyLicenseAssets)
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.protobuf.kotlin.lite)
    implementation(libs.androidx.appcompat)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.zxing.android.embedded)
    implementation(libs.webrtc)
    implementation(libs.okhttp)
}

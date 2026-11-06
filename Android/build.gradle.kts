buildscript {
    repositories {
        google()
        mavenCentral()
    }
    dependencies {
        // AGP is put on the classpath here instead of applied via the plugins{}
        // DSL below: Google doesn't publish a plugin-marker artifact
        // (com.android.application:com.android.application.gradle.plugin) for
        // this AGP release even though the real gradle-<version>.jar exists,
        // so plugins{}'s marker-based resolution 404s.
        classpath("com.android.tools.build:gradle:${libs.versions.agp.get()}")
    }
}

plugins {
    // AGP 9+ has built-in Kotlin support and conflicts with the separate
    // org.jetbrains.kotlin.android plugin - do not re-add it.
    alias(libs.plugins.kotlin.compose) apply false
}

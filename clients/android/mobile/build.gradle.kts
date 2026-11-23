plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

// Version: version.properties beside the root build file is the single source for the phone and TV apps (spec native-apps WP12).
// versionCode = major * 1000000 + minor * 10000 + patch * 100 + versionBuild, so a higher version always has a higher code.
val versionProperties = java.util.Properties().apply {
    val text = providers.fileContents(rootProject.layout.projectDirectory.file("version.properties")).asText.get()
    load(java.io.StringReader(text))
}
val appVersionName: String = versionProperties.getProperty("versionName")
val appVersionBuild: Int = versionProperties.getProperty("versionBuild", "0").toInt()
val appVersionParts: List<Int> = appVersionName.split(".").map { it.toInt() }
require(appVersionParts.size == 3) { "version.properties versionName must be major.minor.patch, got '$appVersionName'." }
require(appVersionParts[1] in 0..99 && appVersionParts[2] in 0..99 && appVersionBuild in 0..99) {
    "version.properties: minor, patch and versionBuild must each be 0 to 99."
}
val appVersionCode: Int =
    appVersionParts[0] * 1_000_000 + appVersionParts[1] * 10_000 + appVersionParts[2] * 100 + appVersionBuild

// Release signing: the keystore and passwords come only from environment variables (CI secrets or your own shell), never from the
// repository. With any of them missing the release build is left unsigned, so pull-request builds keep working. The release
// workflow sets TUVIMA_ANDROID_SIGNING_REQUIRED=true so it can never publish an unsigned file.
val signingKeystoreFile: String? = providers.environmentVariable("TUVIMA_ANDROID_KEYSTORE_FILE").orNull
val signingKeystorePassword: String? = providers.environmentVariable("TUVIMA_ANDROID_KEYSTORE_PASSWORD").orNull
val signingKeyAlias: String? = providers.environmentVariable("TUVIMA_ANDROID_KEY_ALIAS").orNull
val signingKeyPassword: String? = providers.environmentVariable("TUVIMA_ANDROID_KEY_PASSWORD").orNull
val releaseSigningAvailable: Boolean =
    listOf(signingKeystoreFile, signingKeystorePassword, signingKeyAlias, signingKeyPassword).all { !it.isNullOrBlank() }
if (providers.environmentVariable("TUVIMA_ANDROID_SIGNING_REQUIRED").orNull == "true" && !releaseSigningAvailable) {
    throw GradleException(
        "Release signing is required but TUVIMA_ANDROID_KEYSTORE_FILE, TUVIMA_ANDROID_KEYSTORE_PASSWORD, " +
            "TUVIMA_ANDROID_KEY_ALIAS and TUVIMA_ANDROID_KEY_PASSWORD are not all set."
    )
}

android {
    namespace = "com.tuvima.library.mobile"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.tuvima.library"
        minSdk = 28
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersionName
    }

    signingConfigs {
        if (releaseSigningAvailable) {
            create("release") {
                storeFile = file(signingKeystoreFile!!)
                storePassword = signingKeystorePassword
                keyAlias = signingKeyAlias
                keyPassword = signingKeyPassword
            }
        }
    }
    buildTypes {
        getByName("release") {
            if (releaseSigningAvailable) signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { jvmToolchain(17) }

dependencies {
    implementation(project(":core"))
    val composeBom = platform("androidx.compose:compose-bom:2026.08.00")
    implementation(composeBom)
    androidTestImplementation(composeBom)
    implementation("androidx.activity:activity-compose:1.12.4")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.concurrent:concurrent-futures-ktx:1.3.0")
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.10.0")
    implementation("androidx.media3:media3-exoplayer:1.11.0")
    implementation("androidx.media3:media3-exoplayer-hls:1.11.0")
    implementation("androidx.media3:media3-session:1.11.0")
    implementation("androidx.media3:media3-ui:1.11.0")
    implementation("androidx.work:work-runtime-ktx:2.11.0")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
}

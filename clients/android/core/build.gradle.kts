plugins {
    id("com.android.library")
}

android {
    namespace = "com.tuvima.library.core"
    compileSdk = 37

    defaultConfig {
        minSdk = 28
        consumerProguardFiles("consumer-rules.pro")
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { jvmToolchain(17) }

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    testImplementation("junit:junit:4.13.2")
    // Android's own org.json (Apache-2.0) for JVM tests; the SDK jar only has stubs that throw "not mocked".
    testImplementation("com.vaadin.external.google:android-json:0.0.20131108.vaadin1")
}

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "com.megapdf.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "ca.electricrv.megapdf"
        minSdk = 26
        targetSdk = 36
        versionCode = 12
        versionName = "2.1.1"
        // The app's own instrumented tests (#346): Compose UI tests that drive the viewer on
        // an emulator, in android-ci.yml's instrumented-test job beside the engine's.
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        // Play upload key, injected by android-release.yml from repo secrets.
        // Absent locally and on PR builds — release then builds unsigned.
        val keystorePath = System.getenv("ANDROID_UPLOAD_KEYSTORE_PATH")
        if (keystorePath != null) {
            create("upload") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("ANDROID_UPLOAD_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_UPLOAD_KEY_ALIAS")
                keyPassword = System.getenv("ANDROID_UPLOAD_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("upload")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
        // #514: the reflow spike's prototype is gated on BuildConfig.DEBUG, which AGP 8 does not
        // generate unless asked. Nothing else in the app reads BuildConfig; this is here so a
        // prototype can exist in the tree without reaching a release build.
        buildConfig = true
    }
    testOptions {
        // System animation scales are set to 0 for a connected run, so a menu or a dialog
        // is on screen the moment it is asked for (#346).
        animationsDisabled = true
    }
    androidResources {
        // Emits the LocaleConfig from the values-* folders (#91) so Android 13+
        // lists MegaPDF under Settings > Languages > App languages. The default
        // locale comes from res/resources.properties.
        generateLocaleConfig = true
    }
}

dependencies {
    implementation(project(":engine"))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)

    // Instrumented UI tests (#346): the Compose semantics tree for finding and driving the
    // screen, and Espresso-Intents for answering the system pickers and the share sheet
    // without a second app.
    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.test.espresso.intents)
}

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
        versionCode = 16
        versionName = "2.4.0"
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
    // Find on scanned pages (#676): the bundled Latin model, so recognition needs no network and
    // no Play Services (Dave, 2026-10-17). About 13-16 MB per device.
    implementation(libs.mlkit.text.recognition)

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

// #817: the app declares no permissions, and the Play listing, the website and the privacy policy
// all say so. A library can add one without anyone editing our manifest (ML Kit text recognition,
// #676, brought INTERNET and ACCESS_NETWORK_STATE in 2.3.0 and nobody noticed for a release), so
// this reads the MERGED manifests, the ones that ship, and fails on any uses-permission in them.
//
// The one allowed entry is androidx.core's `<applicationId>.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`:
// a signature-level permission in our own namespace that no other app can hold, never shown to a
// person and not a capability (aapt lists it, Play's permission list does not). Anything else, even
// a harmless-looking one, has to be a decision: strip it with tools:node="remove" in the app manifest
// or change the copy, then add it here with the reason.
//
// Run by android-ci.yml and a dependency of assembleRelease / bundleRelease, so a signed bundle
// cannot be cut with one. `./gradlew :app:checkNoPermissions`.
val appIdForPermissionCheck = android.defaultConfig.applicationId ?: error("applicationId unset")
val mergedManifests = listOf("debug", "release").associateWith { variant ->
    layout.buildDirectory.file(
        "intermediates/merged_manifest/$variant/process${variant.replaceFirstChar { it.uppercase() }}MainManifest/AndroidManifest.xml"
    )
}
val checkNoPermissions by tasks.registering {
    group = "verification"
    description = "Fails if the merged debug or release manifest declares any uses-permission (#817)."
    dependsOn("processDebugMainManifest", "processReleaseMainManifest")
    inputs.files(mergedManifests.values)
    val allowed = setOf("$appIdForPermissionCheck.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION")
    val manifestFiles = mergedManifests.mapValues { it.value.get().asFile }
    doLast {
        val androidNs = "http://schemas.android.com/apk/res/android"
        val problems = ArrayList<String>()
        for ((variant, file) in manifestFiles) {
            check(file.isFile) { "merged $variant manifest not found at $file" }
            val factory = javax.xml.parsers.DocumentBuilderFactory.newInstance()
            factory.isNamespaceAware = true
            val root = factory.newDocumentBuilder().parse(file).documentElement
            val found = ArrayList<String>()
            for (tag in listOf("uses-permission", "uses-permission-sdk-23", "uses-permission-sdk-m")) {
                val nodes = root.getElementsByTagName(tag)
                for (i in 0 until nodes.length) {
                    found += (nodes.item(i) as org.w3c.dom.Element).getAttributeNS(androidNs, "name")
                }
            }
            logger.lifecycle("merged $variant manifest uses-permission: ${found.ifEmpty { listOf("(none)") }}")
            for (name in found) if (name !in allowed) problems += "$variant: $name"
        }
        if (problems.isNotEmpty()) {
            throw GradleException(
                "The merged manifest declares permissions, but the app promises none (#817): " +
                    problems.joinToString() +
                    ". Strip each with <uses-permission android:name=\"...\" tools:node=\"remove\"/> in " +
                    "app/src/main/AndroidManifest.xml, or change the Play listing, website and privacy policy."
            )
        }
    }
}
tasks.configureEach {
    if (name == "assembleRelease" || name == "bundleRelease") dependsOn(checkNoPermissions)
}

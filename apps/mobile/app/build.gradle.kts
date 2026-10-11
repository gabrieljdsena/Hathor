import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.devtools.ksp")
}

// Sync server config lives ONLY in local.properties (never hardcoded,
// never committed): API_BASE_URL (e.g. http://192.168.1.10:5051) +
// API_TOKEN (a per-device hth_ key with library:read + library:write).
// Empty values = sync disabled, like desktop.
val localProps = Properties()
val localPropsFile = rootProject.file("local.properties")
if (localPropsFile.exists()) {
    localPropsFile.inputStream().use { stream -> localProps.load(stream) }
}
fun apiProp(name: String, default: String = ""): String {
    val v = localProps.getProperty(name, default)
        .replace("\\", "\\\\")
        .replace("\"", "\\\"")
    return "\"$v\""
}

android {
    namespace = "com.musicplayer.android"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.musicplayer.android"
        minSdk = 24
        targetSdk = 34
        versionCode = 2
        versionName = "0.3.0-hathor"

        ndk {
            abiFilters += listOf("arm64-v8a", "x86_64")
        }

        buildConfigField("String", "API_BASE_URL", apiProp("API_BASE_URL"))
        buildConfigField("String", "API_TOKEN", apiProp("API_TOKEN"))
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        isCoreLibraryDesugaringEnabled = true
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    // Matches android:extractNativeLibs="true" required by youtubedl-android.
    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

dependencies {
    // Phase 1 engine: real yt-dlp + bundled ffmpeg, no Chaquopy setup in our code.
    implementation(libs.youtubedl.library)
    implementation(libs.youtubedl.ffmpeg)

    // Phase 2: local DB (same table/column names as desktop database.sql) +
    // sync over the Hathor Web API (data/api/SyncApi.kt, stdlib HTTP+JSON).
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.work.runtime)
    // Stream previews (any audio codec incl. opus/webm, which MediaPlayer can't do).
    implementation(libs.media3.exoplayer)

    // Phase 3: ID3 read/write (stock jaudiotagger + desugaring for API < 26 nio use).
    implementation(libs.jaudiotagger)
    coreLibraryDesugaring(libs.desugar.libs)

    implementation(libs.androidx.core.ktx)
    implementation(libs.lifecycle.runtime)
    implementation(libs.activity.compose)
    implementation(libs.coroutines.android)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.icons.core)
    implementation(libs.compose.icons.extended)
}

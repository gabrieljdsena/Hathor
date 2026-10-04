import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.devtools.ksp")
}

// Remote DB credentials live ONLY in local.properties (never hardcoded, never
// committed). Same names as the desktop .env: DB_HOST, DB_PORT, DB_USER,
// DB_PASSWORD, DB_NAME. Empty DB_HOST = sync disabled, like desktop.
val localProps = Properties()
val localPropsFile = rootProject.file("local.properties")
if (localPropsFile.exists()) {
    localPropsFile.inputStream().use { stream -> localProps.load(stream) }
}
fun dbProp(name: String, default: String = ""): String {
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

        buildConfigField("String", "DB_HOST", dbProp("DB_HOST"))
        buildConfigField("String", "DB_PORT", dbProp("DB_PORT", "4000"))
        buildConfigField("String", "DB_USER", dbProp("DB_USER"))
        buildConfigField("String", "DB_PASSWORD", dbProp("DB_PASSWORD"))
        buildConfigField("String", "DB_NAME", dbProp("DB_NAME"))
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
    // direct MySQL/TiDB access mirroring desktop sync.py (pymysql -> Connector/J).
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.mysql.connector)
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

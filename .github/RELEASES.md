# Publishing Hathor releases

Built binaries are published as **GitHub Releases**, not committed to git.
The workflows attach checksum files and keep the repository history free of
large APK/ZIP blobs.

## Desktop release (Windows + Linux)

The desktop release workflow builds both artifacts and publishes them to one
GitHub Release.

```bash
git tag desktop-v1.0.2
git push origin desktop-v1.0.2
```

Or run **Actions → Release desktop → Run workflow** and provide a tag such as
`desktop-v1.0.2`.

Artifacts:

- `hathor-desktop-<versionCode>-<versionName>.zip` — Windows sideload/updater
  package.
- `hathor-linux-<versionName>.zip` — portable Linux runtime bundle using the
  system Python/GTK/WebKitGTK stack; contains `run.sh`, a README and the Arch
  `packaging-arch/` directory.
- `SHA256SUMS.txt`.

The Linux zip is not a self-contained WebKit distribution. Arch remains the
native package path (`packaging/arch/PKGBUILD`); the zip is for machines that
already have the dependencies.

## Android release

```bash
git tag android-v0.4.24
git push origin android-v0.4.24
```

Or run **Actions → Release Android → Run workflow**.

Artifacts:

- `hathor-android-<versionCode>-<versionName>.apk`
- `SHA256SUMS.txt`

The Android job signs with the existing release key. Configure these repository
secrets before the first run:

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64 of `hathor-release.jks` |
| `ANDROID_STORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_ALIAS` | Release key alias |
| `ANDROID_KEY_PASSWORD` | Key password |
| `HATHOR_API_URL` | Build-time server URL |
| `HATHOR_API_TOKEN` | Build-time API key (legacy/fallback; login can be used in-app) |

Never commit the keystore, `keystore.properties`, `local.properties`, or the
base64 secret.

## Version bumping

- Desktop: bump `apps/desktop/version.json` (`versionCode` + `versionName`);
  keep `apps/desktop/packaging/arch/PKGBUILD` `pkgver` in sync.
- Android: bump `versionCode` / `versionName` in
  `apps/mobile/app/build.gradle.kts`.
- Push the matching `desktop-v*` or `android-v*` tag only after the build
  passes locally/CI.

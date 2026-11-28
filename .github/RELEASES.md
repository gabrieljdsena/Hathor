# Publishing Hathor releases

Built binaries are published as **GitHub Releases**, not committed to git.
The workflows attach checksum files and keep the repository history free of
large APK/ZIP blobs.

Tag pushes trigger from the tagged commit, so the `develop` line can publish
without merging. The **Run workflow** button only appears after the workflow
files reach the repository's default branch (`main`).

Current clients read these releases directly:

- Android `0.4.26+` polls `android-v*` releases.
- Desktop `1.0.3+` polls `desktop-v*` releases.
- Both use the GitHub asset `digest` (or `SHA256SUMS.txt`) and pick the
  highest `versionCode`, independent of Web API login.
- The channel is currently **dev** (prereleases included). Move to stable by
  filtering prereleases in the two updater clients once releases come from
  `main`.

## Desktop release (Windows + Linux)

The desktop release workflow builds both artifacts and publishes them to one
GitHub Release.

```bash
git tag desktop-v1.0.7
git push origin desktop-v1.0.7
```

Or run **Actions → Release desktop → Run workflow** and provide a tag such as
`desktop-v1.0.7`.

Artifacts:

- `hathor-desktop-<versionCode>-<versionName>.zip` — Windows sideload/updater
  package.
- `hathor-linux-<versionCode>-<versionName>.zip` — portable Linux runtime
  bundle using the system Python/GTK/WebKitGTK stack; contains `install-linux.sh`,
  `run.sh`, a README and the Arch `packaging-arch/` directory.
- `install-linux.sh` — standalone per-user installer/updater.
- `SHA256SUMS.txt`.

Recommended Linux install (per-user, no sudo for the app):

```bash
curl -fsSL https://raw.githubusercontent.com/gabrieljdsena/Hathor/develop/apps/desktop/scripts/install-linux.sh | bash
```

The one-liner resolves the newest `desktop-v*` release (including prereleases),
verifies `SHA256SUMS.txt`, and installs to `~/.local/opt/hathor`. It prefers
the system Python/GTK/WebKitGTK stack and creates a `--system-site-packages`
venv only when pure-Python modules are missing. Add `--install-deps` to let it
install distro packages.

Updates for script installs:

```bash
~/.local/opt/hathor/install-linux.sh --update
```

The Linux zip is not a self-contained WebKit distribution. Arch remains the
native package path (`packaging/arch/PKGBUILD`); the zip is for machines that
already have the dependencies or use `install-linux.sh --install-deps`.

## Android release

```bash
git tag android-v0.4.31
git push origin android-v0.4.31
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

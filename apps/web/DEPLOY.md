# Hathor web — Windows-service deploy

Same flow as myhomelab: build with a script, run as a Windows service.
The API self-hosts the React SPA (same origin, no vite/nginx needed).

## First time (elevated PowerShell)

```powershell
cd C:\Users\gabriel\code\Hathor\apps\web
.\scripts\build.ps1
.\scripts\install-service.ps1        # registers service 'Hathor' on :5051
```

`install-service.ps1` pins machine env vars from your current session when
present: `JWT_KEY` and `HATHOR_DB_PASSWORD`. If either is missing it warns —
set them first (`$env:JWT_KEY=...`, `$env:HATHOR_DB_PASSWORD=...`) or the
service will fail to start / mint tokens. HTTP only (LAN use).

## Every deploy after that

```powershell
cd C:\Users\gabriel\code\Hathor\apps\web
.\scripts\build.ps1
Restart-Service Hathor
```

Open `http://localhost:5051` (API + UI same origin).

## Notes

- YouTube bot-check protection: when downloads fail with "Sign in to
  confirm you're not a bot", open Settings → System → "YouTube bot-check
  protection" → Enable. One click downloads a proof-of-origin token
  provider (bgutil, pinned version via `YtDlp:PotProviderVersion`), builds
  it with Node.js, and starts it as a loopback-only child process of the
  API — no Google login, no manual cookies.txt. Requirements: Node.js 22+
  on the machine PATH (the service runs as LocalSystem, so install Node
  machine-wide) and outbound access to github.com (one-time download) plus
  youtube.com. The provider auto-resumes on service restarts; Disable stops
  it. Explicit `YtDlp:CookiesFile` / `YtDlp:CookiesFromBrowser` config still
  takes precedence when set. Upstream caveat: PO tokens make traffic look
  legitimate but don't guarantee every video passes — keep yt-dlp updated
  (Settings → yt-dlp → Update) when failures return.

- Lyrics romanization (fugashi + UniDic) is optional: `pip install fugashi
  unidic-lite` where the API runs (Windows service / dev machine) for
  full-quality kanji readings; without it the endpoint falls back to the
  built-in kana table. Docker images stay Python-free by design.

- The service runs as LocalSystem, whose `%APPDATA%` is the SYSTEM profile,
  not yours. The installer therefore pins `Library__SongsPath`,
  `Library__PodcastsPath` and `Database__StorageRoot` machine env vars to
  your real folders (same ones the desktop app uses). Override with
  `-SongsPath` / `-PodcastsPath` / `-StorageRoot` if your library lives
  elsewhere. Without these, All Songs comes back empty while the DB looks
  fine — the classic symptom this fixes.

- `artifacts/publish/` and the copied `src/Hathor.Api/wwwroot/` are
  gitignored build outputs — never committed.
- `appsettings.Secrets.json` is excluded from publish output by the csproj,
  so secrets can't leak via the publish folder. The service reads
  `HATHOR_DB_PASSWORD` / `JWT_KEY` from machine env (set by the installer).
- Dev (`dotnet run`, vite) and Docker (nginx frontend) are untouched: static
  hosting only activates when `wwwroot` exists.

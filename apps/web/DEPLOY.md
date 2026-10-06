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

# Hathor web — Windows-service deploy

Same flow as myhomelab: build with a script, run as a Windows service.
The API self-hosts the React SPA (same origin, no vite/nginx needed).

## First time (elevated PowerShell)

```powershell
cd C:\Users\gabriel\code\Hathor\apps\web
.\scripts\build.ps1
.\scripts\install-service.ps1        # registers service 'Hathor' on :5050
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

Open `http://localhost:5050` (API + UI same origin).

## Notes

- `artifacts/publish/` and the copied `src/Hathor.Api/wwwroot/` are
  gitignored build outputs — never committed.
- `appsettings.Secrets.json` is excluded from publish output by the csproj,
  so secrets can't leak via the publish folder. The service reads
  `HATHOR_DB_PASSWORD` / `JWT_KEY` from machine env (set by the installer).
- Dev (`dotnet run`, vite) and Docker (nginx frontend) are untouched: static
  hosting only activates when `wwwroot` exists.

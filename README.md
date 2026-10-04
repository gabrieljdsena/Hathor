# Hathor (monorepo)

One repo for the whole Hathor music system (formerly `Music_app`).

```text
apps/
  desktop/  Windows desktop app — Python + pywebview + pygame (MP3 library, yt-dlp, iTunes, lyrics, SMTC)
  web/      Web app — ASP.NET Core 8 API + Worker + React/Vite frontend (see apps/web/IMPLEMENTATION_PLAN.md)
  mobile/   Android app (Kotlin/Compose) — placeholder, push existing AndroidPlayer code here
packages/
  contracts/  Sync contract shared by all 3 apps (schema, export/import, tombstones)
```

## Quick links

- Desktop: `apps/desktop/README.md` — `pip install -r requirements.txt`, `npm install`, `python main.py`
- Web: `apps/web/` — `Hathor.slnx`, `docker-compose.yml`, `frontend/hathor-web/`
- Mobile: `apps/mobile/README.md`

## Dev

Each app builds/runs independently from its own folder. Root `docker-compose.yml`
lives in `apps/web/docker-compose.yml` (API + Worker + Postgres + MySQL-compat + web).

CI is per-app with path filters (`apps/desktop/**`, `apps/web/**`, `apps/mobile/**`)
so a web-only change doesn't rebuild desktop.

## Rename note

GitHub repo was `gabrieljdsena/Music_app`, renamed to `gabrieljdsena/Hathor`.
GitHub keeps a redirect, so old clones keep working after:

```powershell
git remote set-url origin https://github.com/gabrieljdsena/Hathor.git
```

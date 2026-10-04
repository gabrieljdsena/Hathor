# Hathor Mobile (Android)

Placeholder for the existing Android player (`AndroidPlayer/`: Compose screens,
`AppDatabase.kt` Room, `PlayerService`/`PlayerManager`, `SyncWorker`/`PullWorker`,
`RemoteConn`/`RemoteSync`/`RemoteUpload`).

## How to push your existing code here

From a checkout of this repo on `chore/monorepo` (or `main` after merge):

```powershell
# 1. Copy your AndroidPlayer project into apps/mobile, excluding build dirs:
robocopy C:\path\to\AndroidPlayer C:\Users\gabriel\code\Hathor\apps\mobile /E `
  /XD .gradle .idea build app\build node_modules `
  /XF local.properties *.jks *.keystore

# 2. Commit + push:
git -C C:\Users\gabriel\code\Hathor status --short
git -C C:\Users\gabriel\code\Hathor add apps/mobile
git -C C:\Users\gabriel\code\Hathor commit -m "feat(mobile): add Android app"
git -C C:\Users\gabriel\code\Hathor push -u origin chore/monorepo
```

If mobile is currently its own git repo and you want to keep history:

```powershell
git -C C:\Users\gabriel\code\Hathor subtree add --prefix=apps/mobile C:\path\to\AndroidPlayer main
git -C C:\Users\gabriel\code\Hathor push -u origin chore/monorepo
```

## Contract

Mobile stays sync-compatible with desktop + web via `packages/contracts/`
(`export`/`import`, tombstone deletions, `MAX(id)` incremental history).

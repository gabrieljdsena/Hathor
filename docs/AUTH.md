# Web account authentication

Hathor clients can sign in with the same account as the web UI instead of
minting a scoped API key. The existing server auth system is reused; there is
no second credential store.

```text
username + password
        |
        v  POST /api/v1/auth/login
access JWT (15 min) + rotating refresh token (24 h, or 30 d with rememberMe)
        |
        v  Authorization: Bearer <access>
        |
        v  on 401 / within 60 s of expiry
POST /api/v1/auth/refresh  -> new access + rotated refresh
```

## Client behaviour

| Item | Desktop | Android |
|---|---|---|
| Access token | memory only | memory only |
| Refresh token | Windows Credential Manager / Linux Secret Service via `keyring` | AES/GCM key in Android Keystore; ciphertext in prefs |
| Password | sent once to `/auth/login`, never stored | same |
| API-key fallback | existing saved keys continue; no new key entry in the login UI | existing saved keys continue; build-time `API_TOKEN` still works |
| Plain HTTP | allowed; HTTPS/VPN recommended | allowed; HTTPS/VPN recommended |

- Refresh is single-flight; a failed token is passed into the refresh call so
  concurrent requests reuse a token another request just rotated.
- JSON requests, sync file uploads/downloads, update checks/APK downloads and
  AI DJ clip URLs retry once after a 401.
- A refresh rejected with 400/401/403 clears the session and asks for sign-in.
  Offline/5xx failures keep the session and surface an offline message.
- API keys remain fully functional for automation, old builds and plain-HTTP
  LAN installs.
- Password login over plain LAN HTTP is allowed. Prefer HTTPS or a trusted VPN;
  a scoped API key is the safer fallback on plain HTTP.

## Phases

| Phase | Status |
|---|---|
| 1. Server hardening | **Done.** Optional `deviceLabel`, password change revokes other sessions while keeping the current one, refresh rotation with reuse detection/grace, session list/revoke/logout-all endpoints, tests. |
| 2. Desktop auth core | **Done.** `services/auth_session.py`, keyring refresh token, token-aware `HathorApi`, single-flight refresh and 401 retry, every service client, Settings sign-in/out and API-key fallback. |
| 3. Mobile auth core | **Done.** `AuthSession`, Android Keystore token store, session-aware `SyncApi`/`HathorApi`, Settings sign-in/out and API-key fallback. |
| 4. Background and edge cases | **Done.** Sync file transport, app-update metadata/APK downloads, AI DJ clip media, failed-token sharing, and offline-vs-rejected session handling. Active-session management on clients remains optional; the web UI already has it. |
| 5. Rollout | **In progress.** Both clients support login and API keys; docs and releases are updated. A successful live password sign-in still needs a test account over the LAN, and HTTPS/VPN validation is pending. |

## Verification

Server tests (`apps/web/tests/Hathor.Api.Tests`):

- `AuthSessionTests` — password change/revocation, device labels, remember-me
  lifetimes, refresh rotation, replay detection, logout, logout-all, session
  revoke.
- `AuthRefreshGraceTests` — duplicate refresh inside the grace window rotates
  instead of burning the session.

Live LAN probes confirm `/auth/status` is public and `/auth/login`,
`/auth/refresh`, `/auth/me` and `/auth/sessions` reject unauthenticated or
invalid credentials with 401.

Client tests:

- Desktop `tests/test_auth_session.py`, `tests/test_api_config.py`,
  `tests/test_hathor_api.py`, `tests/test_app_update.py`.
- Android `AuthSessionTest`, `UpdateHttpApiTest`, `ApiContractTest`.

## Releases

- Desktop `desktop-v1.0.7` — build 8, `1.0.7-hathor`; dedicated login screen and
  GitHub Releases update checks.
- Android `android-v0.4.30` — code 33, `0.4.30-hathor`; dedicated login screen,
  show/hide password, and GitHub Releases updates.
  Older clients keep the server/SMB channel.

## Remaining

1. Perform a successful live sign-in with a test Web account (desktop +
   Android), including refresh after 15 minutes and sign-out/revocation.
2. Repeat over HTTPS or a trusted VPN; decide whether plain-HTTP password login
   should eventually be disabled in favour of API keys.
3. Optional: add active-session list/revoke to desktop/mobile Settings, or use
   the web UI for lost-device logout.
4. Optional long-term: device pairing/QR approval so clients never receive the
   password.

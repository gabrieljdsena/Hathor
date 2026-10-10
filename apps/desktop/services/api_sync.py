"""Sync the local SQLite library with the Hathor C# Web API.

Syncs the local SQLite library with the Hathor server (the remote
MySQL/TiDB leg is fully removed). Scan, push and pull keep the same
logic — only the transport changed:

- pull: ``GET /api/v1/sync/delta?cursor=`` merges rows with the same
  upsert/link-reconcile/daily-mix semantics the old pull had; files missing on
  disk stream down via ``GET /api/v1/sync/files/{file}`` (byte-identical,
  exact remote filename) instead of being re-downloaded from YouTube.
- push: local rows + ``Sync_Deletions`` tombstones go up via
  ``POST /api/v1/sync/import`` (server applies scoped replaces and
  idempotent history inserts); files the server lacks come back as
  ``missing_files`` and are uploaded via ``PUT /api/v1/sync/files/{file}``.

Delta contract (server must speak this; see
``docs/SYNC_API_MIGRATION.md``)::

    GET delta?cursor=... ->
      { "cursor": str,
        "songs": [{file, downloaded_link, title, date_download, artist, loudness_db?}],
        "podcasts": [...same...],
        "playlists": [{id, title, description, thumbnail}],
        "song_playlist": [{id, song_file, playlist_id, date_added}],
        "lyrics": [{id, song_file, lyrics, offset_ms?}],
        "music_history": [{id, song_file, date_played}],
        "playlist_history": [{id, playlist_id, date_played}],
        "podcast_tags": [{id, name}],
        "podcast_tag_links": [{id, podcast_file, tag_id}],
        "podcast_chapters": [{id, podcast_file, name, start_secs, end_secs}],
        "daily_mix": [{mix_date, song_files}],
        "deletions": [{table_name, row_key}] }

Stdlib only (urllib) — no new pip dependency.

Wire notes: the server speaks camelCase JSON (ASP.NET Core default) while
this module works in snake_case internally. Responses are normalized on
the way in (_norm) and push payloads are emitted in PascalCase (which the
server binds case-insensitively) with ISO-8601 UTC datetimes.
"""

import datetime
import json
import os
import sqlite3
import urllib.error
import urllib.parse
import urllib.request

API_URL_ENV = "HATHOR_API_URL"
API_KEY_ENV = "HATHOR_API_KEY"

NOT_CONFIGURED = (
    "No sync server configured. "
    f"Set {API_URL_ENV} (e.g. http://<server>:5051) and {API_KEY_ENV} in .env."
)
SERVER_MISSING = (
    "Sync server does not speak the sync-file endpoints yet "
    "(need GET/PUT /api/v1/sync/files/{file} + delta). Pull the latest server build."
)


class ApiSyncError(Exception):
    """User-facing sync failure (message is shown verbatim in the UI)."""


def sync_backend():
    """Return 'api' | 'unconfigured' (the remote-DB leg is gone)."""
    url, key = get_api_config()
    if url and key:
        return "api"
    return "unconfigured"


def get_api_config():
    url = (os.getenv(API_URL_ENV) or "").strip().rstrip("/")
    key = (os.getenv(API_KEY_ENV) or "").strip()
    return url, key


# server-field -> local-field per delta section (server sends camelCase;
# local merge code reads snake_case). Unknown shapes pass through untouched.
FIELD_MAPS = {
    "songs": {"file": "file", "downloadedLink": "downloaded_link",
              "title": "title", "dateDownloadUtc": "date_download",
              "artist": "artist", "loudnessDb": "loudness_db"},
    "podcasts": {"file": "file", "downloadedLink": "downloaded_link",
                 "title": "title", "dateDownloadUtc": "date_download",
                 "artist": "artist"},
    "playlists": {"id": "id", "title": "title", "description": "description",
                  "thumbnail": "thumbnail"},
    "song_playlist": {"id": "id", "songFile": "song_file",
                      "playlistId": "playlist_id", "dateAddedUtc": "date_added"},
    "lyrics": {"id": "id", "songFile": "song_file", "lyricsJson": "lyrics",
               "offsetMs": "offset_ms"},
    "music_history": {"id": "id", "songFile": "song_file",
                      "datePlayedUtc": "date_played"},
    "playlist_history": {"id": "id", "playlistId": "playlist_id",
                         "datePlayedUtc": "date_played"},
    "podcast_tags": {"id": "id", "name": "name"},
    "podcast_tag_links": {"id": "id", "podcastFile": "podcast_file",
                          "tagId": "tag_id"},
    "daily_mix": {"mixDate": "mix_date", "songFilesJson": "song_files"},
    "podcast_chapters": {"id": "id", "podcastFile": "podcast_file",
                         "name": "name", "startSecs": "start_secs",
                         "endSecs": "end_secs"},
    "deletions": {"tableName": "table_name", "rowKey": "row_key"},
}

# server section name -> local section name.
SECTION_MAP = {
    "songs": "songs", "podcasts": "podcasts", "playlists": "playlists",
    "songLinks": "song_playlist", "podcastTags": "podcast_tags",
    "podcastTagLinks": "podcast_tag_links", "lyrics": "lyrics",
    "musicHistory": "music_history", "playlistHistory": "playlist_history",
    "dailyMix": "daily_mix", "podcastChapters": "podcast_chapters",
    "deletions": "deletions",
}


def _norm_section(rows, mapping):
    """Map one delta section's rows to local snake_case field names."""
    normed = []
    for row in rows or []:
        if not isinstance(row, dict):
            continue
        out = {}
        for server_key, local_key in mapping.items():
            for candidate in (local_key, server_key,
                              server_key[0].upper() + server_key[1:]):
                if candidate in row and row[candidate] is not None:
                    out[local_key] = row[candidate]
                    break
        normed.append(out)
    return normed


def _norm_snapshot(snap):
    """Normalize a delta/export payload to local section + field names."""
    normed = {}
    for server_section, local_section in SECTION_MAP.items():
        for candidate in (local_section, server_section,
                          server_section[0].upper() + server_section[1:]):
            if candidate in snap and isinstance(snap[candidate], list):
                normed[local_section] = _norm_section(
                    snap[candidate], FIELD_MAPS[local_section])
                break
    for key in ("cursor", "Cursor"):
        if snap.get(key):
            normed["cursor"] = snap[key]
            break
    return normed


def _iso(dt):
    """'YYYY-MM-DD HH:MM:SS' (UTC, local DB shape) -> ISO-8601 UTC."""
    if not dt:
        return None
    s = str(dt).strip()
    if " " in s and "T" not in s:
        s = s.replace(" ", "T", 1)
    if not s.endswith("Z") and "+" not in s[10:]:
        s += "Z"
    return s


def cursor_path(db_path):
    return os.path.join(os.path.dirname(os.path.abspath(db_path)), "sync_cursor.txt")


def read_cursor(db_path):
    try:
        with open(cursor_path(db_path), "r", encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def write_cursor(db_path, cursor):
    if not cursor:
        return
    with open(cursor_path(db_path), "w", encoding="utf-8") as f:
        f.write(cursor)


class ApiSyncClient:
    def __init__(self, db_path, timeout=30):
        self.db_path = db_path
        self.timeout = timeout
        base_url, api_key = get_api_config()
        if not base_url or not api_key:
            raise ApiSyncError(NOT_CONFIGURED)
        self.base = base_url + "/api/v1/sync"
        self.key = api_key

    # -- transport ------------------------------------------------------
    def _request(self, method, path, payload=None, query=None):
        url = self.base + path
        if query:
            url += "?" + urllib.parse.urlencode(query)
        data = None
        headers = {
            "Authorization": f"Bearer {self.key}",
            "User-Agent": "hathor-desktop/1",
            "Accept": "application/json",
        }
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                body = resp.read()
        except urllib.error.HTTPError as e:
            if e.code == 401:
                raise ApiSyncError(
                    "Sync server rejected the API key (401). Check "
                    f"{API_KEY_ENV} — it needs library:read + library:write scope."
                )
            if e.code == 404:
                raise ApiSyncError(SERVER_MISSING)
            try:
                detail = json.loads(e.read().decode("utf-8")).get("message")
            except Exception:
                detail = None
            raise ApiSyncError(f"Sync server error ({e.code}): {detail or e.reason}")
        except urllib.error.URLError as e:
            raise ApiSyncError(
                f"Cannot reach sync server at {self.base}. Is the server running? ({e.reason})"
            )
        try:
            return json.loads(body.decode("utf-8")) if body else {}
        except ValueError:
            raise ApiSyncError("Sync server returned a non-JSON response.")

    def get_delta(self, cursor):
        return self._request("GET", "/delta", query={"cursor": cursor} if cursor else {})

    # -- pull -----------------------------------------------------------
    def pull_once(self):
        import settings

        cursor = read_cursor(self.db_path)
        try:
            snap = _norm_snapshot(self.get_delta(cursor))
        except ApiSyncError:
            raise
        except Exception as e:
            return f"Error contacting sync server: {e}"

        added = {"songs": 0, "podcasts": 0}
        downloaded = {"songs": 0, "podcasts": 0}
        try:
            with sqlite3.connect(self.db_path) as conn:
                self._ensure_tables(conn)
                self._apply_deletions(conn, snap.get("deletions") or [])
                added["songs"] = self._merge_songs(conn, snap.get("songs") or [])
                added["podcasts"] = self._merge_podcasts(conn, snap.get("podcasts") or [])
                self._merge_rest(conn, snap)
        except Exception as e:
            return f"Error syncing to local DB: {e}"

        # Fetch missing bytes straight from the server (exact filenames).
        for row in snap.get("songs") or []:
            if self._fetch_missing(settings.path, row.get("file"), "songs"):
                downloaded["songs"] += 1
        for row in snap.get("podcasts") or []:
            if self._fetch_missing(settings.podcasts_path, row.get("file"), "podcasts"):
                downloaded["podcasts"] += 1

        write_cursor(self.db_path, snap.get("cursor", ""))
        msg = (
            f"Synced {added['songs']} new songs from server. "
            f"Downloaded {downloaded['songs']} files."
        )
        if added["podcasts"] or downloaded["podcasts"]:
            msg += (
                f" Podcasts: {added['podcasts']} new entries, "
                f"{downloaded['podcasts']} files downloaded."
            )
        return msg

    def _fetch_missing(self, folder, filename, library="songs"):
        """Download one file if absent on disk. Returns True when fetched."""
        if not filename or "/" in filename or "\\" in filename:
            return False
        dest = os.path.join(folder, filename)
        if os.path.exists(dest):
            return False
        os.makedirs(folder, exist_ok=True)
        tmp = dest + ".hathor-part"
        req = urllib.request.Request(
            self.base + "/files/" + urllib.parse.quote(filename)
            + "?" + urllib.parse.urlencode({"library": library}),
            headers={
                "Authorization": f"Bearer {self.key}",
                "User-Agent": "hathor-desktop/1",
            },
            method="GET",
        )
        try:
            with urllib.request.urlopen(req, timeout=120) as resp, open(tmp, "wb") as f:
                while True:
                    chunk = resp.read(256 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise ApiSyncError(SERVER_MISSING)
            raise ApiSyncError(f"File download failed ({e.code}): {filename}")
        except urllib.error.URLError as e:
            try:
                os.remove(tmp)
            except OSError:
                pass
            raise ApiSyncError(f"File download failed: {e.reason}")
        os.replace(tmp, dest)
        return True

    # -- push -----------------------------------------------------------
    def push_once(self):
        try:
            payload = self._collect_local()
        except Exception as e:
            return f"Error reading local DB: {e}"
        try:
            resp = self._request("POST", "/import", payload=payload)
        except ApiSyncError:
            raise
        except Exception as e:
            return f"Error contacting sync server: {e}"

        uploaded = 0
        try:
            missing = (resp.get("missing_files") or resp.get("missingFiles")
                       or resp.get("MissingFiles") or [])
            for filename in missing:
                if self._upload_local_file(filename):
                    uploaded += 1
        except ApiSyncError:
            raise
        except Exception as e:
            return f"Error uploading files: {e}"

        # Tombstones only clear once the server accepted them.
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("DELETE FROM Sync_Deletions")
        except Exception as e:
            return f"Push accepted but could not clear tombstones: {e}"
        msg = "Push complete: library rows sent to server."
        if uploaded:
            msg += f" Uploaded {uploaded} new file(s)."
        return msg

    def _collect_local(self):
        """Local rows shaped as the server's SyncSnapshot (PascalCase keys,
        ISO-8601 UTC datetimes). Missing tables read as [] (old installs)."""
        payload = {}
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = sqlite3.Row

            def rows(sql):
                try:
                    return [dict(r) for r in conn.execute(sql).fetchall()]
                except sqlite3.OperationalError:
                    return []

            try:
                song_rows = [dict(r) for r in conn.execute(
                    "SELECT file, downloaded_link, title, date_download, artist, loudness_db FROM Songs").fetchall()]
            except sqlite3.OperationalError:
                # loudness_db postdates this install; play-time fills it.
                song_rows = rows("SELECT file, downloaded_link, title, date_download, artist FROM Songs")
            payload["Songs"] = [
                {"File": r.get("file"), "DownloadedLink": r.get("downloaded_link"),
                 "Title": r.get("title"), "DateDownloadUtc": _iso(r.get("date_download")),
                 "Artist": r.get("artist"), "LoudnessDb": r.get("loudness_db")}
                for r in song_rows
            ]
            payload["Podcasts"] = [
                {"File": r.get("file"), "DownloadedLink": r.get("downloaded_link"),
                 "Title": r.get("title"), "DateDownloadUtc": _iso(r.get("date_download")),
                 "Artist": r.get("artist")}
                for r in rows("SELECT file, downloaded_link, title, date_download, artist FROM Podcasts")
            ]
            payload["Playlists"] = [
                {"Id": r.get("id"), "Title": r.get("title"),
                 "Description": r.get("description"), "Thumbnail": r.get("thumbnail")}
                for r in rows("SELECT id, title, description, thumbnail FROM Playlists")
            ]
            payload["SongLinks"] = [
                {"Id": r.get("id"), "SongFile": r.get("song_file"),
                 "PlaylistId": r.get("playlist_id"), "DateAddedUtc": _iso(r.get("date_added"))}
                for r in rows("SELECT id, song_file, playlist_id, date_added FROM Song_Playlist")
            ]
            payload["Lyrics"] = [
                {"Id": r.get("id"), "SongFile": r.get("song_file"),
                 "LyricsJson": r.get("lyrics"), "OffsetMs": r.get("offset_ms") or 0}
                for r in rows("SELECT id, song_file, lyrics, offset_ms FROM Lyrics")
            ] or [
                {"Id": r.get("id"), "SongFile": r.get("song_file"),
                 "LyricsJson": r.get("lyrics"), "OffsetMs": 0}
                for r in rows("SELECT id, song_file, lyrics FROM Lyrics")
            ]
            payload["MusicHistory"] = [
                {"Id": r.get("id"), "SongFile": r.get("song_file"),
                 "DatePlayedUtc": _iso(r.get("date_played"))}
                for r in rows("SELECT id, song_file, date_played FROM Music_History")
            ]
            payload["PlaylistHistory"] = [
                {"Id": r.get("id"), "PlaylistId": r.get("playlist_id"),
                 "DatePlayedUtc": _iso(r.get("date_played"))}
                for r in rows("SELECT id, playlist_id, date_played FROM Playlist_History")
            ]
            payload["PodcastTags"] = [
                {"Id": r.get("id"), "Name": r.get("name")}
                for r in rows("SELECT id, name FROM Podcast_Tags")
            ]
            payload["PodcastTagLinks"] = [
                {"Id": r.get("id"), "PodcastFile": r.get("podcast_file"),
                 "TagId": r.get("tag_id")}
                for r in rows("SELECT id, podcast_file, tag_id FROM Podcast_Tag_Links")
            ]
            payload["PodcastChapters"] = [
                {"Id": r.get("id"), "PodcastFile": r.get("podcast_file"),
                 "Name": r.get("name"), "StartSecs": r.get("start_secs"),
                 "EndSecs": r.get("end_secs")}
                for r in rows("SELECT id, podcast_file, name, start_secs, end_secs FROM Podcast_Chapters")
            ]
            payload["DailyMix"] = [
                {"MixDate": r.get("mix_date"), "SongFilesJson": r.get("song_files")}
                for r in rows("SELECT mix_date, song_files FROM Daily_Mix")
            ]
            payload["Deletions"] = [
                {"TableName": r.get("table_name"), "RowKey": r.get("row_key")}
                for r in rows("SELECT table_name, row_key FROM Sync_Deletions")
            ]
        return payload

    def _upload_local_file(self, filename):
        """PUT one local MP3 the server lacks. Returns True when uploaded."""
        import settings

        if not filename or "/" in filename or "\\" in filename:
            return False
        library = "songs"
        for folder in (settings.path, settings.podcasts_path):
            src = os.path.join(folder, filename)
            if os.path.exists(src):
                library = "podcasts" if folder == settings.podcasts_path else "songs"
                break
        else:
            return False
        with open(src, "rb") as f:
            data = f.read()
        req = urllib.request.Request(
            self.base + "/files/" + urllib.parse.quote(filename)
            + "?" + urllib.parse.urlencode({"library": library}),
            data=data,
            headers={
                "Authorization": f"Bearer {self.key}",
                "User-Agent": "hathor-desktop/1",
                "Content-Type": "audio/mpeg",
            },
            method="PUT",
        )
        try:
            with urllib.request.urlopen(req, timeout=300):
                pass
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise ApiSyncError(SERVER_MISSING)
            raise ApiSyncError(f"File upload failed ({e.code}): {filename}")
        except urllib.error.URLError as e:
            raise ApiSyncError(f"File upload failed: {e.reason}")
        return True

    # -- merge (same semantics as the retired JDBC pull) ------------------
    @staticmethod
    def _ensure_tables(conn):
        conn.execute(
            "CREATE TABLE IF NOT EXISTS Podcasts ("
            "file varchar(255) PRIMARY KEY, downloaded_link varchar(255), "
            "title varchar(255) NOT NULL, date_download DATETIME DEFAULT CURRENT_TIMESTAMP, "
            "artist varchar(255))"
        )
        try:
            conn.execute("ALTER TABLE Songs ADD COLUMN loudness_db DOUBLE")
        except sqlite3.OperationalError:
            pass
        for ddl in (
            "CREATE TABLE IF NOT EXISTS Podcast_Tags (id INTEGER PRIMARY KEY AUTOINCREMENT, name varchar(255) NOT NULL UNIQUE)",
            "CREATE TABLE IF NOT EXISTS Podcast_Tag_Links (id INTEGER PRIMARY KEY AUTOINCREMENT, podcast_file varchar(255) NOT NULL, tag_id bigint NOT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_podcast_tag_links_pair ON Podcast_Tag_Links(podcast_file, tag_id)",
            "CREATE TABLE IF NOT EXISTS Podcast_Chapters (id INTEGER PRIMARY KEY AUTOINCREMENT, podcast_file varchar(255) NOT NULL, name varchar(255) NOT NULL, start_secs real NOT NULL, end_secs real NULL)",
            "CREATE INDEX IF NOT EXISTS idx_podcast_chapters_file ON Podcast_Chapters(podcast_file, start_secs)",
            "CREATE TABLE IF NOT EXISTS Daily_Mix (mix_date varchar(10) PRIMARY KEY, song_files text not null, created_at datetime default CURRENT_TIMESTAMP)",
        ):
            conn.execute(ddl)

    @staticmethod
    def _merge_songs(conn, songs):
        added = 0
        for s in songs:
            file = s.get("file")
            if not file:
                continue
            if s.get("loudness_db") is not None:
                conn.execute(
                    "UPDATE Songs SET loudness_db = ? WHERE file = ? AND loudness_db IS NULL",
                    (s["loudness_db"], file),
                )
            if not conn.execute("SELECT file FROM Songs WHERE file = ?", (file,)).fetchone():
                try:
                    conn.execute(
                        "INSERT INTO Songs (file, downloaded_link, title, date_download, artist, loudness_db)"
                        " VALUES (?, ?, ?, ?, ?, ?)",
                        (file, s.get("downloaded_link"), s.get("title"),
                         s.get("date_download"), s.get("artist"), s.get("loudness_db")),
                    )
                except Exception:
                    conn.execute(
                        "INSERT INTO Songs (file, downloaded_link, title, date_download, artist)"
                        " VALUES (?, ?, ?, ?, ?)",
                        (file, s.get("downloaded_link"), s.get("title"),
                         s.get("date_download"), s.get("artist")),
                    )
                added += 1
        return added

    @staticmethod
    def _merge_podcasts(conn, podcasts):
        added = 0
        for p in podcasts:
            file = p.get("file")
            if not file:
                continue
            if not conn.execute("SELECT file FROM Podcasts WHERE file = ?", (file,)).fetchone():
                conn.execute(
                    "INSERT INTO Podcasts (file, downloaded_link, title, date_download, artist)"
                    " VALUES (?, ?, ?, ?, ?)",
                    (file, p.get("downloaded_link"), p.get("title"),
                     p.get("date_download"), p.get("artist")),
                )
                added += 1
        return added

    @staticmethod
    def _merge_rest(conn, snap):
        for pl in snap.get("playlists") or []:
            conn.execute(
                "INSERT INTO Playlists (id, title, description, thumbnail) VALUES (?, ?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET title = excluded.title,"
                " description = excluded.description, thumbnail = excluded.thumbnail",
                (pl.get("id"), pl.get("title"), pl.get("description"), pl.get("thumbnail")),
            )
        links = snap.get("song_playlist") or []
        for l in links:
            conn.execute(
                "INSERT INTO Song_Playlist (id, song_file, playlist_id, date_added) VALUES (?, ?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET song_file = excluded.song_file,"
                " playlist_id = excluded.playlist_id, date_added = excluded.date_added",
                (l.get("id"), l.get("song_file"), l.get("playlist_id"), l.get("date_added")),
            )
        # Links removed elsewhere stay missing server-side: drop local copies
        # absent from the delta set. Guarded on non-empty (a failed delta must
        # never wipe local links) — same rule as the retired JDBC pull.
        if links:
            keep = {(l.get("playlist_id"), l.get("song_file")) for l in links}
            stale = [
                r for r in conn.execute("SELECT playlist_id, song_file FROM Song_Playlist").fetchall()
                if (r[0], r[1]) not in keep
            ]
            if stale:
                conn.executemany(
                    "DELETE FROM Song_Playlist WHERE playlist_id = ? AND song_file = ?", stale
                )
        for ly in snap.get("lyrics") or []:
            if "::chapter:" in (ly.get("song_file") or ""):
                continue
            conn.execute(
                "INSERT INTO Lyrics (id, song_file, lyrics) VALUES (?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET song_file = excluded.song_file,"
                " lyrics = excluded.lyrics",
                (ly.get("id"), ly.get("song_file"), ly.get("lyrics")),
            )
            # Server embeds the offset in each lyric row (older shapes
            # carried a separate lyrics_offsets section — still honored).
            if ly.get("offset_ms"):
                try:
                    conn.execute(
                        "UPDATE Lyrics SET offset_ms = ? WHERE song_file = ?",
                        (ly["offset_ms"], ly.get("song_file")),
                    )
                except sqlite3.OperationalError:
                    pass
        for off in snap.get("lyrics_offsets") or []:
            if "::chapter:" in (off.get("song_file") or ""):
                continue
            try:
                conn.execute(
                    "UPDATE Lyrics SET offset_ms = ? WHERE song_file = ?",
                    (off.get("offset_ms"), off.get("song_file")),
                )
            except sqlite3.OperationalError:
                pass
        for h in snap.get("music_history") or []:
            conn.execute(
                "INSERT INTO Music_History (id, song_file, date_played) VALUES (?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET song_file = excluded.song_file,"
                " date_played = excluded.date_played",
                (h.get("id"), h.get("song_file"), h.get("date_played")),
            )
        for h in snap.get("playlist_history") or []:
            conn.execute(
                "INSERT INTO Playlist_History (id, playlist_id, date_played) VALUES (?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET playlist_id = excluded.playlist_id,"
                " date_played = excluded.date_played",
                (h.get("id"), h.get("playlist_id"), h.get("date_played")),
            )
        for t in snap.get("podcast_tags") or []:
            conn.execute(
                "INSERT INTO Podcast_Tags (id, name) VALUES (?, ?)"
                " ON CONFLICT(id) DO UPDATE SET name = excluded.name",
                (t.get("id"), t.get("name")),
            )
        tag_links = snap.get("podcast_tag_links") or []
        for l in tag_links:
            conn.execute(
                "INSERT INTO Podcast_Tag_Links (id, podcast_file, tag_id) VALUES (?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET podcast_file = excluded.podcast_file,"
                " tag_id = excluded.tag_id",
                (l.get("id"), l.get("podcast_file"), l.get("tag_id")),
            )
        if tag_links:
            keep = {(l.get("tag_id"), l.get("podcast_file")) for l in tag_links}
            stale = [
                r for r in conn.execute("SELECT tag_id, podcast_file FROM Podcast_Tag_Links").fetchall()
                if (r[0], r[1]) not in keep
            ]
            if stale:
                conn.executemany(
                    "DELETE FROM Podcast_Tag_Links WHERE tag_id = ? AND podcast_file = ?", stale
                )
        for c in snap.get("podcast_chapters") or []:
            if not c.get("podcast_file") or not c.get("name"):
                continue
            conn.execute(
                "INSERT INTO Podcast_Chapters (id, podcast_file, name, start_secs, end_secs)"
                " VALUES (?, ?, ?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET podcast_file = excluded.podcast_file,"
                " name = excluded.name, start_secs = excluded.start_secs, end_secs = excluded.end_secs",
                (c.get("id"), c.get("podcast_file"), c.get("name"),
                 c.get("start_secs"), c.get("end_secs")),
            )
        if snap.get("daily_mix"):
            for m in snap["daily_mix"]:
                try:
                    files = json.loads(m.get("song_files") or "null")
                except Exception:
                    continue
                if not isinstance(files, list):
                    continue
                conn.execute(
                    "INSERT INTO Daily_Mix (mix_date, song_files) VALUES (?, ?)"
                    " ON CONFLICT(mix_date) DO UPDATE SET song_files = excluded.song_files",
                    (m.get("mix_date"), json.dumps(files)),
                )
            conn.execute(
                "DELETE FROM Daily_Mix WHERE mix_date < ?",
                (datetime.date.today().isoformat(),),
            )

    @staticmethod
    def _apply_deletions(conn, deletions):
        """Apply server-authoritative tombstones (delta only lists them)."""
        import settings as _settings

        for d in deletions:
            table = (d.get("table_name") or "").lower()
            key = d.get("row_key")
            if not key:
                continue
            try:
                if table == "songs":
                    conn.execute("DELETE FROM Songs WHERE file = ?", (key,))
                    conn.execute("DELETE FROM Song_Playlist WHERE song_file = ?", (key,))
                    conn.execute("DELETE FROM Lyrics WHERE song_file = ?", (key,))
                    conn.execute("DELETE FROM Music_History WHERE song_file = ?", (key,))
                    _remove_file(_settings.path, key)
                elif table == "podcasts":
                    conn.execute("DELETE FROM Podcasts WHERE file = ?", (key,))
                    conn.execute("DELETE FROM Podcast_Tag_Links WHERE podcast_file = ?", (key,))
                    conn.execute("DELETE FROM Podcast_Chapters WHERE podcast_file = ?", (key,))
                    _remove_file(_settings.podcasts_path, key)
                elif table == "playlists":
                    conn.execute("DELETE FROM Song_Playlist WHERE playlist_id = ?", (key,))
                    conn.execute("DELETE FROM Playlist_History WHERE playlist_id = ?", (key,))
                    conn.execute("DELETE FROM Playlists WHERE id = ?", (key,))
                elif table == "lyrics":
                    conn.execute("DELETE FROM Lyrics WHERE song_file = ?", (key,))
                elif table == "music_history":
                    conn.execute("DELETE FROM Music_History WHERE song_file = ?", (key,))
                elif table == "playlist_history":
                    conn.execute("DELETE FROM Playlist_History WHERE playlist_id = ?", (key,))
                elif table == "podcast_tags":
                    conn.execute("DELETE FROM Podcast_Tag_Links WHERE tag_id = ?", (key,))
                    conn.execute("DELETE FROM Podcast_Tags WHERE id = ?", (key,))
                elif table == "podcast_chapters":
                    conn.execute("DELETE FROM Podcast_Chapters WHERE podcast_file = ?", (key,))
            except Exception as e:
                print(f" [Sync] Could not apply deletion {table}/{key}: {e}")


def _remove_file(folder, filename):
    if not filename or "/" in filename or "\\" in filename:
        return
    try:
        path = os.path.join(folder, filename)
        if os.path.exists(path):
            os.remove(path)
    except OSError as e:
        print(f" [Sync] Could not remove deleted file {filename}: {e}")

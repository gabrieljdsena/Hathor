import sqlite3
import os
import datetime
import json
from pathlib import Path
import settings

class DatabaseManager:
    def __init__(self, api, db_path):
        self.api = api
        self.db_path = db_path

    def load_playlists(self):
        try:
            with sqlite3.connect(self.db_path) as conn:
                query = "SELECT id, title, description, thumbnail FROM Playlists"
                cursor = conn.execute(query)
                return [{"id": row[0], "title": row[1], "description": row[2], "thumbnail": row[3]} for row in cursor.fetchall()]
        except Exception as e:
            print(f" [Python] Error loading playlists: {str(e)}")
            return []

    def new_playlist(self, data):
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("INSERT INTO Playlists (title, description, thumbnail) VALUES (?, ?, ?)", (data.get("Title"), data.get("Description"), data.get("Cover")))
        except Exception as e:
            print(f" [Python] Error creating playlist: {str(e)}")

    def getImage(self, id):
        try:
            with sqlite3.connect(self.db_path) as conn:
                query = "SELECT thumbnail FROM Playlists WHERE id = ?"
                cursor = conn.execute(query, (id,))
                row = cursor.fetchone()
                if row:
                    return row[0]
        except Exception as e:
            print(f" [Python] Error loading playlist image: {str(e)}")
            return None

    def update_playlist(self, playlist_id, data):
        try:
            with sqlite3.connect(self.db_path) as conn:
                fields = []
                values = []
                if "title" in data:
                    fields.append("title = ?")
                    values.append(data["title"])
                if "description" in data:
                    fields.append("description = ?")
                    values.append(data["description"])
                if "thumbnail" in data:
                    fields.append("thumbnail = ?")
                    values.append(data["thumbnail"])
                if fields:
                    values.append(playlist_id)
                    conn.execute(f"UPDATE Playlists SET {', '.join(fields)} WHERE id = ?", values)
            return True
        except Exception as e:
            print(f" [Python] Error updating playlist: {str(e)}")
            return False

    def delete_playlist(self, playlist_id):
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("DELETE FROM Playlist_History WHERE playlist_id = ?", (playlist_id,))
                conn.execute("DELETE FROM Song_Playlist WHERE playlist_id = ?", (playlist_id,))
                conn.execute("DELETE FROM Playlists WHERE id = ?", (playlist_id,))
            self.record_deletion("playlists", playlist_id)
            self.record_deletion("playlist_history", playlist_id)
            return True
        except Exception as e:
            print(f" [Python] Error deleting playlist: {str(e)}")
            return False

    def record_deletion(self, table_name, row_key):
        """Record a deletion so the next manual push can remove the row on the remote DB."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("INSERT INTO Sync_Deletions (table_name, row_key) VALUES (?, ?)", (table_name, str(row_key)))
        except Exception as e:
            print(f" [Python] Error recording deletion: {str(e)}")

    def get_song_playlists(self, song_file):
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT playlist_id FROM Song_Playlist WHERE song_file = ?", (song_file,))
                return [row[0] for row in cursor.fetchall()]
        except Exception as e:
            print(f" [Python] Error getting song playlists: {str(e)}")
            return []

    def update_song_playlists(self, song_file, song_title, playlist_ids):
        try:
            with sqlite3.connect(self.db_path) as conn:
                # First ensure song exists in Songs table to satisfy foreign key constraint
                conn.execute("INSERT OR IGNORE INTO Songs (file, title) VALUES (?, ?)", (song_file, song_title or song_file))
                
                # Delete existing associations
                conn.execute("DELETE FROM Song_Playlist WHERE song_file = ?", (song_file,))
                
                # Insert new associations
                for pid in playlist_ids:
                    conn.execute("INSERT INTO Song_Playlist (song_file, playlist_id) VALUES (?, ?)", (song_file, pid))
            return True
        except Exception as e:
            print(f" [Python] Error updating song playlists: {str(e)}")
            return False

    # ---- Podcast tags (same shape as Playlists + Song_Playlist) ----
    def get_podcast_tags(self):
        """All tags with live episode counts (files currently on disk)."""
        try:
            path = Path(settings.podcasts_path)
            live = {f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3'} if path.exists() else set()
            with sqlite3.connect(self.db_path) as conn:
                tags = [
                    {"id": row[0], "name": row[1]}
                    for row in conn.execute("SELECT id, name FROM Podcast_Tags ORDER BY name").fetchall()
                ]
                counts = {}
                for file, tag_id in conn.execute("SELECT podcast_file, tag_id FROM Podcast_Tag_Links").fetchall():
                    if file in live:
                        counts[tag_id] = counts.get(tag_id, 0) + 1
            for tag in tags:
                tag["episode_count"] = counts.get(tag["id"], 0)
            return tags
        except Exception as e:
            print(f" [Python] Error loading podcast tags: {str(e)}")
            return []

    def get_podcast_tag_map(self):
        """Episode file -> [tag ids] for filtering + row display."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                out = {}
                for file, tag_id in conn.execute("SELECT podcast_file, tag_id FROM Podcast_Tag_Links").fetchall():
                    out.setdefault(file, []).append(tag_id)
                return out
        except Exception as e:
            print(f" [Python] Error loading podcast tag map: {str(e)}")
            return {}

    def new_podcast_tag(self, name):
        """Create a tag. Returns the id (existing id on duplicates, -1 on blank/failure)."""
        clean = (name or "").strip()
        if not clean:
            return -1
        try:
            with sqlite3.connect(self.db_path) as conn:
                row = conn.execute("SELECT id FROM Podcast_Tags WHERE name = ?", (clean,)).fetchone()
                if row:
                    return row[0]
                cur = conn.execute("INSERT INTO Podcast_Tags (name) VALUES (?)", (clean,))
                return cur.lastrowid
        except Exception as e:
            print(f" [Python] Error creating podcast tag: {str(e)}")
            return -1

    def rename_podcast_tag(self, tag_id, name):
        """Rename a tag. False on blank/duplicate names or failure."""
        clean = (name or "").strip()
        if not clean:
            return False
        try:
            with sqlite3.connect(self.db_path) as conn:
                row = conn.execute("SELECT id FROM Podcast_Tags WHERE name = ?", (clean,)).fetchone()
                if row and row[0] != int(tag_id):
                    return False
                conn.execute("UPDATE Podcast_Tags SET name = ? WHERE id = ?", (clean, tag_id))
                return True
        except Exception as e:
            print(f" [Python] Error renaming podcast tag: {str(e)}")
            return False

    def delete_podcast_tag(self, tag_id):
        """Delete a tag + its links (delete_playlist() equivalent)."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("DELETE FROM Podcast_Tag_Links WHERE tag_id = ?", (tag_id,))
                conn.execute("DELETE FROM Podcast_Tags WHERE id = ?", (tag_id,))
            self.record_deletion("podcast_tags", tag_id)
            return True
        except Exception as e:
            print(f" [Python] Error deleting podcast tag: {str(e)}")
            return False

    def assign_podcast_tag(self, file, tag_id):
        """Assign a tag to one episode (INSERT OR IGNORE)."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "INSERT OR IGNORE INTO Podcast_Tag_Links (podcast_file, tag_id) VALUES (?, ?)",
                    (file, tag_id),
                )
            return True
        except Exception as e:
            print(f" [Python] Error assigning podcast tag: {str(e)}")
            return False

    def unassign_podcast_tag(self, file, tag_id):
        """Remove one episode's tag assignment."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "DELETE FROM Podcast_Tag_Links WHERE podcast_file = ? AND tag_id = ?",
                    (file, tag_id),
                )
            return True
        except Exception as e:
            print(f" [Python] Error unassigning podcast tag: {str(e)}")
            return False

    # ==========================
    # Podcast chapters (web parity)
    # ==========================
    @staticmethod
    def _validate_chapter(name, start_secs, end_secs):
        """Mirror web validation (PodcastTimestamps Validate). None = valid."""
        import math
        clean = (name or "").strip()
        if not clean:
            return "Timestamp name can't be blank."
        if len(clean) > 255:
            return "Timestamp name is too long (max 255)."
        try:
            start = float(start_secs)
        except (TypeError, ValueError):
            return "Start time must be zero or later."
        if math.isnan(start) or math.isinf(start) or start < 0:
            return "Start time must be zero or later."
        if end_secs is not None:
            try:
                end = float(end_secs)
            except (TypeError, ValueError):
                return "End time must be later than the start time."
            if math.isnan(end) or math.isinf(end) or end <= start:
                return "End time must be later than the start time."
        return None

    def _ensure_chapter_schema(self, conn):
        conn.execute(
            "CREATE TABLE IF NOT EXISTS Podcast_Chapters ("
            "id INTEGER PRIMARY KEY AUTOINCREMENT, "
            "podcast_file varchar(255) NOT NULL, "
            "name varchar(255) NOT NULL, "
            "start_secs real NOT NULL, "
            "end_secs real NULL)"
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_podcast_chapters_file "
            "ON Podcast_Chapters(podcast_file, start_secs)"
        )

    def get_podcast_chapters(self, file):
        """Chapter marks for one episode, ordered by start. Empty list if none."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                self._ensure_chapter_schema(conn)
                rows = conn.execute(
                    "SELECT id, podcast_file, name, start_secs, end_secs "
                    "FROM Podcast_Chapters WHERE podcast_file = ? ORDER BY start_secs",
                    (file,),
                ).fetchall()
                return [
                    {'id': r[0], 'file': r[1], 'name': r[2],
                     'startSecs': r[3], 'endSecs': r[4]}
                    for r in rows
                ]
        except Exception as e:
            print(f" [Python] Error loading podcast chapters: {str(e)}")
            return []

    def add_podcast_chapter(self, file, name, start_secs, end_secs=None):
        """Add a chapter. Returns {'ok': True, 'chapter': {...}} or {'ok': False, 'message': ...}."""
        if not os.path.exists(os.path.join(settings.podcasts_path, file or "")):
            return {'ok': False, 'message': 'Episode not found.'}
        error = self._validate_chapter(name, start_secs, end_secs)
        if error:
            return {'ok': False, 'message': error}
        try:
            with sqlite3.connect(self.db_path) as conn:
                self._ensure_chapter_schema(conn)
                cur = conn.execute(
                    "INSERT INTO Podcast_Chapters (podcast_file, name, start_secs, end_secs) "
                    "VALUES (?, ?, ?, ?)",
                    (file, (name or "").strip(), float(start_secs),
                     None if end_secs is None else float(end_secs)),
                )
                chapter_id = cur.lastrowid
            chapters = self.get_podcast_chapters(file)
            chapter = next((c for c in chapters if c['id'] == chapter_id), None)
            return {'ok': True, 'chapter': chapter}
        except Exception as e:
            print(f" [Python] Error adding podcast chapter: {str(e)}")
            return {'ok': False, 'message': 'Could not save the chapter.'}

    def update_podcast_chapter(self, chapter_id, name, start_secs, end_secs=None):
        """Edit a chapter. Same result shape as add_podcast_chapter."""
        error = self._validate_chapter(name, start_secs, end_secs)
        if error:
            return {'ok': False, 'message': error}
        try:
            with sqlite3.connect(self.db_path) as conn:
                self._ensure_chapter_schema(conn)
                row = conn.execute(
                    "SELECT podcast_file FROM Podcast_Chapters WHERE id = ?",
                    (chapter_id,),
                ).fetchone()
                if not row:
                    return {'ok': False, 'message': 'Chapter not found.'}
                conn.execute(
                    "UPDATE Podcast_Chapters SET name = ?, start_secs = ?, end_secs = ? "
                    "WHERE id = ?",
                    ((name or "").strip(), float(start_secs),
                     None if end_secs is None else float(end_secs), chapter_id),
                )
            chapters = self.get_podcast_chapters(row[0])
            chapter = next((c for c in chapters if c['id'] == int(chapter_id)), None)
            return {'ok': True, 'chapter': chapter}
        except Exception as e:
            print(f" [Python] Error updating podcast chapter: {str(e)}")
            return {'ok': False, 'message': 'Could not save the chapter.'}

    def delete_podcast_chapter(self, chapter_id):
        """Delete one chapter mark."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                self._ensure_chapter_schema(conn)
                cur = conn.execute("DELETE FROM Podcast_Chapters WHERE id = ?", (chapter_id,))
                return cur.rowcount > 0
        except Exception as e:
            print(f" [Python] Error deleting podcast chapter: {str(e)}")
            return False

    # ==========================
    # Lyrics highlight offset (web parity: ±20000 ms, sparse)
    # ==========================
    def get_lyrics_offset(self, song_file):
        """Persisted highlight-timing correction in ms. 0 when unset."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                try:
                    row = conn.execute(
                        "SELECT offset_ms FROM Lyrics WHERE song_file = ?", (song_file,)
                    ).fetchone()
                except sqlite3.OperationalError:
                    return 0
                if not row or row[0] is None:
                    return 0
                return max(-20000, min(20000, int(row[0])))
        except Exception as e:
            print(f" [Python] Error loading lyrics offset: {str(e)}")
            return 0

    def set_lyrics_offset(self, song_file, offset_ms):
        """Persist the correction (clamped). Works even with no lyrics row yet."""
        try:
            offset = max(-20000, min(20000, int(offset_ms or 0)))
        except (TypeError, ValueError):
            offset = 0
        try:
            with sqlite3.connect(self.db_path) as conn:
                try:
                    row = conn.execute(
                        "SELECT id FROM Lyrics WHERE song_file = ?", (song_file,)
                    ).fetchone()
                except sqlite3.OperationalError:
                    return offset
                if row:
                    conn.execute(
                        "UPDATE Lyrics SET offset_ms = ? WHERE song_file = ?",
                        (offset, song_file),
                    )
                else:
                    conn.execute(
                        "INSERT INTO Lyrics (song_file, lyrics, offset_ms) VALUES (?, ?, ?)",
                        (song_file, None, offset),
                    )
            return offset
        except Exception as e:
            print(f" [Python] Error saving lyrics offset: {str(e)}")
            return 0

    def get_playlist_songs(self, playlist_id):
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT song_file, date_added FROM Song_Playlist WHERE playlist_id = ?", (playlist_id,))
                rows = cursor.fetchall()
                
                playlist_songs = []
                for row in rows:
                    file_name = row[0]
                    date_added = row[1]
                    file_path = os.path.join(settings.path, file_name)
                    if os.path.exists(file_path):
                        song_data = self.api.metadata.get_song_metadata(file_path, file_name)
                        if date_added:
                            song_data['DateAdded'] = date_added.replace(' ', 'T') + 'Z'
                        else:
                            song_data['DateAdded'] = None
                        playlist_songs.append(song_data)
                return playlist_songs
        except Exception as e:
            print(f" [Python] Error loading playlist songs: {str(e)}")
            return []

    def get_songs_by_artist(self, artist):
        """Return all songs whose metadata Artist matches the given artist (metadata-only)."""
        path = Path(settings.path)
        if not path.exists() or not artist:
            return []
        artist = str(artist).strip()

        date_lookup = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT file, date_download FROM Songs")
                for row in cursor.fetchall():
                    if row[1]:
                        date_lookup[row[0]] = row[1].replace(' ', 'T') + 'Z' if 'T' not in str(row[1]) else str(row[1])
                    else:
                        date_lookup[row[0]] = None
        except Exception as e:
            print(f" [Python] Error fetching date_download for artist: {e}")

        songs = []
        for f in path.iterdir():
            if f.is_file() and f.suffix.lower() == '.mp3':
                song_data = self.api.metadata.get_song_metadata(str(f), f.name)
                if (song_data.get('Artist') or '') == artist:
                    song_data['DateDownload'] = date_lookup.get(f.name, None)
                    songs.append(song_data)
        songs.sort(key=lambda s: ((s.get('Album') or '').lower(), (s.get('Title') or '').lower()))
        return songs

    def get_songs_by_album(self, album):
        """Return all songs whose metadata Album matches the given album (metadata-only)."""
        path = Path(settings.path)
        if not path.exists() or not album:
            return []
        album = str(album).strip()

        date_lookup = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT file, date_download FROM Songs")
                for row in cursor.fetchall():
                    if row[1]:
                        date_lookup[row[0]] = row[1].replace(' ', 'T') + 'Z' if 'T' not in str(row[1]) else str(row[1])
                    else:
                        date_lookup[row[0]] = None
        except Exception as e:
            print(f" [Python] Error fetching date_download for album: {e}")

        songs = []
        for f in path.iterdir():
            if f.is_file() and f.suffix.lower() == '.mp3':
                song_data = self.api.metadata.get_song_metadata(str(f), f.name)
                if (song_data.get('Album') or '') == album:
                    song_data['DateDownload'] = date_lookup.get(f.name, None)
                    songs.append(song_data)
        songs.sort(key=lambda s: ((s.get('Artist') or '').lower(), (s.get('Title') or '').lower()))
        return songs

    def get_download_history(self, page=1, limit=10):
        try:
            import math
            page = max(int(page), 1)
            limit = max(int(limit), 1)
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    "SELECT file, date_download, downloaded_link FROM Songs "
                    "WHERE downloaded_link IS NOT NULL AND downloaded_link != '' "
                    "ORDER BY date_download DESC"
                )
                rows = cursor.fetchall()

            existing = [
                (file_name, date_download)
                for file_name, date_download, _link in rows
                if os.path.exists(os.path.join(settings.path, file_name))
            ]
            link_lookup = {file_name: link for file_name, _date, link in rows}
            total_count = len(existing)
            total_pages = math.ceil(total_count / limit) if total_count > 0 else 1
            if page > total_pages:
                page = total_pages
            offset = (page - 1) * limit
            page_rows = existing[offset:offset + limit]

            history_songs = []
            for file_name, date_download in page_rows:
                file_path = os.path.join(settings.path, file_name)
                song_data = self.api.metadata.get_song_metadata(file_path, file_name, include_cover=True)
                song_data['DateDownload'] = date_download.replace(' ', 'T') + 'Z' if date_download else None
                song_data['DownloadedLink'] = link_lookup.get(file_name) or ''
                history_songs.append(song_data)
            return {"items": history_songs, "total_pages": total_pages, "current_page": page}
        except Exception as e:
            print(f" [Python] Error loading download history: {str(e)}")
            return {"items": [], "total_pages": 1, "current_page": 1}

    def get_played_history(self, page=1, limit=10):
        try:
            import math
            page = max(int(page), 1)
            limit = max(int(limit), 1)
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT song_file, date_played FROM Music_History ORDER BY date_played DESC")
                rows = cursor.fetchall()

            existing = [
                (file_name, date_played)
                for file_name, date_played in rows
                if os.path.exists(os.path.join(settings.path, file_name))
            ]
            total_count = len(existing)
            total_pages = math.ceil(total_count / limit) if total_count > 0 else 1
            if page > total_pages:
                page = total_pages
            offset = (page - 1) * limit
            page_rows = existing[offset:offset + limit]

            history_songs = []
            for file_name, date_played in page_rows:
                file_path = os.path.join(settings.path, file_name)
                song_data = self.api.metadata.get_song_metadata(file_path, file_name, include_cover=True)
                song_data['DatePlayed'] = date_played.replace(' ', 'T') + 'Z' if date_played else None
                history_songs.append(song_data)
            return {"items": history_songs, "total_pages": total_pages, "current_page": page}
        except Exception as e:
            print(f" [Python] Error loading played history: {str(e)}")
            return {"items": [], "total_pages": 1, "current_page": 1}

    def get_all_songs(self):
        """Return full metadata for every local mp3 (used by the All Songs view)."""
        path = Path(settings.path)
        if not path.exists():
            return []
        date_lookup = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT file, date_download FROM Songs")
                for row in cursor.fetchall():
                    if row[1]:
                        date_lookup[row[0]] = row[1].replace(' ', 'T') + 'Z' if 'T' not in str(row[1]) else str(row[1])
                    else:
                        date_lookup[row[0]] = None
        except Exception as e:
            print(f" [Python] Error fetching date_download: {e}")

        songs = []
        for f in path.iterdir():
            if f.is_file() and f.suffix.lower() == '.mp3':
                song_data = self.api.metadata.get_song_metadata(str(f), f.name)
                song_data['DateDownload'] = date_lookup.get(f.name, None)
                songs.append(song_data)
        return songs

    def get_library_count(self):
        try:
            path = Path(settings.path)
            if not path.exists():
                return 0
            return sum(1 for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3')
        except Exception:
            return 0

    def get_podcasts(self):
        """Lightweight listing: DB rows + files on disk, newest first.

        No tag parsing here by design; full metadata (duration, cover) is
        fetched per-episode on demand (bento menu) or at play time.
        """
        path = Path(settings.podcasts_path)
        if not path.exists():
            return []
        rows = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                for file, title, artist, date_download in conn.execute(
                    "SELECT file, title, artist, date_download FROM Podcasts"
                ):
                    rows[file] = (title, artist, date_download)
        except Exception as e:
            print(f" [Python] Error fetching podcasts: {e}")

        songs = []
        for f in path.iterdir():
            if not (f.is_file() and f.suffix.lower() == '.mp3'):
                continue
            title, artist, date_download = rows.get(f.name, (None, None, None))
            if date_download:
                date_str = date_download.replace(' ', 'T') + 'Z' if 'T' not in str(date_download) else str(date_download)
            else:
                try:
                    mtime = os.path.getmtime(str(f))
                    date_str = datetime.datetime.utcfromtimestamp(mtime).strftime('%Y-%m-%d %H:%M:%S').replace(' ', 'T') + 'Z'
                except Exception:
                    date_str = None
            songs.append({
                'File': f.name,
                'Title': title or f.name.replace('.mp3', ''),
                'Artist': artist or '',
                'Album': '',
                'Year': '',
                'Duration': 0,
                'DateDownload': date_str,
                'CoverArt': None,
                'IsPodcast': True,
            })
        songs.sort(key=lambda s: (s.get('DateDownload') or ''), reverse=True)
        return songs

    def get_podcast_details(self, file):
        """Full metadata for one episode (cover included). Empty dict if gone."""
        try:
            file_path = os.path.join(settings.podcasts_path, file)
            if not os.path.exists(file_path):
                return {}
            song_data = self.api.metadata.get_song_metadata(file_path, file, include_cover=True)
            try:
                with sqlite3.connect(self.db_path) as conn:
                    row = conn.execute(
                        "SELECT date_download FROM Podcasts WHERE file = ?", (file,)
                    ).fetchone()
                if row and row[0]:
                    song_data['DateDownload'] = row[0].replace(' ', 'T') + 'Z' if 'T' not in str(row[0]) else str(row[0])
            except Exception:
                pass
            song_data['IsPodcast'] = True
            return song_data
        except Exception as e:
            print(f" [Python] Error loading podcast details: {e}")
            return {}

    def sync_local_podcasts_to_db(self):
        """Ensure every local podcast file exists in the Podcasts table."""
        path = Path(settings.podcasts_path)
        if not path.exists():
            return "Folder does not exist."

        files = [f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3']
        added_count = 0
        updated_count = 0

        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "CREATE TABLE IF NOT EXISTS Podcasts ("
                    "file varchar(255) PRIMARY KEY, downloaded_link varchar(255), "
                    "title varchar(255) NOT NULL, date_download DATETIME DEFAULT CURRENT_TIMESTAMP, "
                    "artist varchar(255))"
                )
                for file in files:
                    file_path = str(path / file)
                    song_data = self.api.metadata.get_song_metadata(file_path, file)

                    title = song_data.get('Title', file.replace('.mp3', ''))
                    artist = song_data.get('Artist', '')
                    if artist == 'Unknown':
                        artist = ''

                    cursor = conn.execute("SELECT title, artist FROM Podcasts WHERE file = ?", (file,))
                    row = cursor.fetchone()

                    if row:
                        if row[0] != title or row[1] != artist:
                            conn.execute("UPDATE Podcasts SET title = ?, artist = ? WHERE file = ?", (title, artist, file))
                            updated_count += 1
                    else:
                        try:
                            mtime = os.path.getmtime(file_path)
                            dt = datetime.datetime.utcfromtimestamp(mtime).strftime('%Y-%m-%d %H:%M:%S')
                            conn.execute("INSERT INTO Podcasts (file, title, artist, date_download) VALUES (?, ?, ?, ?)", (file, title, artist, dt))
                        except Exception:
                            conn.execute("INSERT INTO Podcasts (file, title, artist) VALUES (?, ?, ?)", (file, title, artist))
                        added_count += 1

            return f"Sync complete: {added_count} added, {updated_count} updated."
        except Exception as e:
            print(f" [Python] Podcast sync error: {e}")
            return f"Error: {str(e)}"

    def _get_ranked_files(self, existing_set):
        """Files ordered by total play count (Music_History), most played first."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    "SELECT song_file, COUNT(*) as cnt, MAX(date_played) as last_played "
                    "FROM Music_History GROUP BY song_file ORDER BY cnt DESC, last_played DESC"
                )
                ranked = [row[0] for row in cursor.fetchall() if row[0] in existing_set]
                return ranked
        except Exception as e:
            print(f" [Python] Error ranking songs: {e}")
            return []

    def _build_daily_mix_files(self, all_files, ranked):
        """2 from top 10, 5 from 11-25, 13 from 26-70, rest fully random (total 50).

        The 'rest' prefers songs outside the top 70 (discovery/long-tail) so
        the tier quotas stay exact whenever the library is large enough;
        leftovers from the top tiers are only used as a fallback for small
        libraries.
        """
        import random as _random
        target = min(50, len(all_files))
        if target == 0:
            return []

        top10 = [f for f in ranked[0:10] if f in all_files]
        top11_25 = [f for f in ranked[10:25] if f in all_files]
        top26_70 = [f for f in ranked[25:70] if f in all_files]
        top70set = set(ranked[0:70]) & set(all_files)

        picked = []
        quotas = [(top10, 2), (top11_25, 5), (top26_70, 13)]
        for pool, n in quotas:
            if not pool:
                continue
            picked.extend(_random.sample(pool, min(n, len(pool), target - len(picked))))
            if len(picked) >= target:
                break

        remaining_needed = target - len(picked)
        if remaining_needed > 0:
            # Prefer songs outside the top 70, then fall back to any leftover.
            primary = [f for f in all_files if f not in picked and f not in top70set]
            take = min(remaining_needed, len(primary))
            if take > 0:
                picked.extend(_random.sample(primary, take))
                remaining_needed -= take
            if remaining_needed > 0:
                pool = [f for f in all_files if f not in picked]
                picked.extend(_random.sample(pool, min(remaining_needed, len(pool))))

        _random.shuffle(picked)
        return picked

    def _resolve_files_to_songs(self, files_in_order):
        date_lookup = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT file, date_download FROM Songs")
                for row in cursor.fetchall():
                    if row[1]:
                        date_lookup[row[0]] = row[1].replace(' ', 'T') + 'Z' if 'T' not in str(row[1]) else str(row[1])
                    else:
                        date_lookup[row[0]] = None
        except Exception:
            pass
        songs = []
        order = {f: i for i, f in enumerate(files_in_order)}
        for file_name in files_in_order:
            file_path = os.path.join(settings.path, file_name)
            if os.path.exists(file_path):
                song_data = self.api.metadata.get_song_metadata(file_path, file_name)
                song_data['DateDownload'] = date_lookup.get(file_name, None)
                songs.append(song_data)
        # keep stored order (already shuffled at generation time)
        _ = order
        return songs

    def get_daily_mix(self):
        """Return today's 50-song mix, generating + persisting it once per day.

        Distribution: 2 from top 10 most-played, 5 from 11-25,
        13 from 26-70 (Music_History counts), rest fully random.
        """
        import datetime as _dt
        today = _dt.date.today().isoformat()
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "CREATE TABLE IF NOT EXISTS Daily_Mix ("
                    "mix_date varchar(10) PRIMARY KEY, song_files text not null, "
                    "created_at datetime default CURRENT_TIMESTAMP)"
                )
                row = conn.execute(
                    "SELECT song_files FROM Daily_Mix WHERE mix_date = ?", (today,)
                ).fetchone()
                if row and row[0]:
                    try:
                        stored = json.loads(row[0])
                    except Exception:
                        stored = []
                    existing = [f for f in stored if os.path.exists(os.path.join(settings.path, f))]
                    if existing:
                        return {"date": today, "songs": self._resolve_files_to_songs(existing), "cached": True}
                    # stored mix no longer valid (files deleted) -> fall through to regenerate
        except Exception as e:
            print(f" [Python] Error reading daily mix: {e}")

        # Generate a fresh mix
        try:
            path = Path(settings.path)
            all_files = [f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3'] if path.exists() else []
            ranked = self._get_ranked_files(set(all_files))
            picked = self._build_daily_mix_files(all_files, ranked)
            try:
                with sqlite3.connect(self.db_path) as conn:
                    conn.execute(
                        "CREATE TABLE IF NOT EXISTS Daily_Mix ("
                        "mix_date varchar(10) PRIMARY KEY, song_files text not null, "
                        "created_at datetime default CURRENT_TIMESTAMP)"
                    )
                    # daily reset: keep only today's mix
                    conn.execute("DELETE FROM Daily_Mix WHERE mix_date != ?", (today,))
                    conn.execute(
                        "INSERT OR REPLACE INTO Daily_Mix (mix_date, song_files) VALUES (?, ?)",
                        (today, json.dumps(picked)),
                    )
            except Exception as e:
                print(f" [Python] Error saving daily mix: {e}")
            return {"date": today, "songs": self._resolve_files_to_songs(picked), "cached": False}
        except Exception as e:
            print(f" [Python] Error generating daily mix: {e}")
            return {"date": today, "songs": [], "cached": False}

    def get_recently_played(self, limit=15):
        """Most recently played unique songs, newest first (home strip)."""
        try:
            limit = max(int(limit), 1)
        except Exception:
            limit = 15
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    "SELECT song_file FROM Music_History ORDER BY date_played DESC, id DESC"
                )
                seen = set()
                files = []
                for (file_name,) in cursor.fetchall():
                    if file_name in seen:
                        continue
                    seen.add(file_name)
                    if os.path.exists(os.path.join(settings.path, file_name)):
                        files.append(file_name)
                    if len(files) >= limit:
                        break
            return self._resolve_files_to_songs(files)
        except Exception as e:
            print(f" [Python] Error loading recently played: {e}")
            return []

    def get_recently_downloaded(self, limit=15):
        """Most recently added songs, newest first (home strip)."""
        try:
            limit = max(int(limit), 1)
        except Exception:
            limit = 15
        try:
            path = Path(settings.path)
            if not path.exists():
                return []
            date_lookup = {}
            try:
                with sqlite3.connect(self.db_path) as conn:
                    for file_name, date_download in conn.execute("SELECT file, date_download FROM Songs"):
                        date_lookup[file_name] = date_download
            except Exception:
                pass

            def _sort_key(f):
                raw = date_lookup.get(f.name)
                if raw:
                    try:
                        import datetime as _dt
                        s = str(raw).replace('T', ' ').rstrip('Z')
                        return _dt.datetime.fromisoformat(s).timestamp()
                    except Exception:
                        pass
                try:
                    return os.path.getmtime(str(f))
                except Exception:
                    return 0

            files = [f for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3']
            files.sort(key=_sort_key, reverse=True)
            return self._resolve_files_to_songs([f.name for f in files[:limit]])
        except Exception as e:
            print(f" [Python] Error loading recently downloaded: {e}")
            return []

    def sync_local_songs_to_db(self):
        """Iterate over the local music folder and ensure all songs exist in the database."""
        path = Path(settings.path)
        if not path.exists():
            return "Folder does not exist."
            
        files = [f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3']
        added_count = 0
        updated_count = 0
        
        try:
            with sqlite3.connect(self.db_path) as conn:
                for file in files:
                    file_path = str(path / file)
                    song_data = self.api.metadata.get_song_metadata(file_path, file)
                    
                    title = song_data.get('Title', file.replace('.mp3', ''))
                    artist = song_data.get('Artist', '')
                    if artist == 'Unknown':
                        artist = ''
                        
                    # Check if exists
                    cursor = conn.execute("SELECT title, artist FROM Songs WHERE file = ?", (file,))
                    row = cursor.fetchone()
                    
                    if row:
                        if row[0] != title or row[1] != artist:
                            conn.execute("UPDATE Songs SET title = ?, artist = ? WHERE file = ?", (title, artist, file))
                            updated_count += 1
                    else:
                        try:
                            mtime = os.path.getmtime(file_path)
                            dt = datetime.datetime.utcfromtimestamp(mtime).strftime('%Y-%m-%d %H:%M:%S')
                            conn.execute("INSERT INTO Songs (file, title, artist, date_download) VALUES (?, ?, ?, ?)", (file, title, artist, dt))
                        except Exception:
                            conn.execute("INSERT INTO Songs (file, title, artist) VALUES (?, ?, ?)", (file, title, artist))
                        added_count += 1
                        
            return f"Sync complete: {added_count} added, {updated_count} updated."
        except Exception as e:
            print(f" [Python] Sync error: {e}")
            return f"Error: {str(e)}"

    def sync_remote_to_local_and_download(self):
        """Fetch missing songs from remote database, save to local, and trigger downloads."""
        if not os.getenv("DB_HOST"):
            return "No remote DB configured. Remote sync unavailable."
        
        try:
            from sync import get_mysql_connection
            conn = get_mysql_connection()
            with conn.cursor() as cur:
                # Add check for songs table existence
                cur.execute(
                    "SELECT COUNT(*) FROM information_schema.tables "
                    "WHERE table_schema = DATABASE() AND table_name = 'songs'"
                )
                if not cur.fetchone()[0]:
                    return "Remote database not initialized yet."
                cur.execute("SELECT file, downloaded_link, title, date_download, artist FROM songs")
                remote_songs = cur.fetchall()
                
                cur.execute("SELECT id, title, description, thumbnail FROM playlists")
                remote_playlists = cur.fetchall()
                
                cur.execute("SELECT id, song_file, playlist_id, date_added FROM song_playlist")
                remote_song_playlist = cur.fetchall()
                
                cur.execute("SELECT id, song_file, lyrics FROM lyrics")
                remote_lyrics = cur.fetchall()
                # offset_ms is newer (same guard pattern as tags below)
                try:
                    cur.execute("SELECT song_file, offset_ms FROM lyrics WHERE offset_ms <> 0")
                    remote_lyrics_offsets = cur.fetchall()
                except Exception:
                    remote_lyrics_offsets = []
                
                cur.execute("SELECT id, song_file, date_played FROM music_history")
                remote_music_history = cur.fetchall()
                
                cur.execute("SELECT id, playlist_id, date_played FROM playlist_history")
                remote_playlist_history = cur.fetchall()

                # daily_mix may not exist on remote DBs created before this feature
                try:
                    cur.execute("SELECT mix_date, song_files FROM daily_mix")
                    remote_daily_mix = cur.fetchall()
                except Exception:
                    remote_daily_mix = []

                # podcasts may not exist on remote DBs created before this feature
                try:
                    cur.execute("SELECT file, downloaded_link, title, date_download, artist FROM podcasts")
                    remote_podcasts = cur.fetchall()
                except Exception:
                    remote_podcasts = []

                # podcast_tags / podcast_tag_links are newer still (same guard)
                try:
                    cur.execute("SELECT id, name FROM podcast_tags")
                    remote_podcast_tags = cur.fetchall()
                except Exception:
                    remote_podcast_tags = []
                try:
                    cur.execute("SELECT id, podcast_file, tag_id FROM podcast_tag_links")
                    remote_podcast_tag_links = cur.fetchall()
                except Exception:
                    remote_podcast_tag_links = []
                # podcast_chapters is newest (same guard)
                try:
                    cur.execute("SELECT id, podcast_file, name, start_secs, end_secs FROM podcast_chapters")
                    remote_podcast_chapters = cur.fetchall()
                except Exception:
                    remote_podcast_chapters = []
            conn.close()
        except Exception as e:
            return f"Error connecting to remote DB: {e}"
        
        songs_to_download = []
        added_count = 0
        podcasts_added = 0
        podcasts_to_download = []
        try:
            with sqlite3.connect(self.db_path) as local_conn:
                local_conn.execute(
                    "CREATE TABLE IF NOT EXISTS Podcasts ("
                    "file varchar(255) PRIMARY KEY, downloaded_link varchar(255), "
                    "title varchar(255) NOT NULL, date_download DATETIME DEFAULT CURRENT_TIMESTAMP, "
                    "artist varchar(255))"
                )
                for file, downloaded_link, title, date_download, artist in remote_songs:
                    # Check if exists in local db
                    cursor = local_conn.execute("SELECT file FROM Songs WHERE file = ?", (file,))
                    if not cursor.fetchone():
                        # Insert into local
                        local_conn.execute(
                            "INSERT INTO Songs (file, downloaded_link, title, date_download, artist) VALUES (?, ?, ?, ?, ?)",
                            (file, downloaded_link, title, date_download, artist)
                        )
                        added_count += 1
                        
                        file_path = os.path.join(settings.path, file)
                        if not os.path.exists(file_path):
                            if downloaded_link:
                                vid_id = downloaded_link.split("v=")[-1] if "v=" in downloaded_link else downloaded_link.split("/")[-1]
                                url = downloaded_link
                            else:
                                vid_id = ""
                                url = f"{title} {artist if artist else ''} audio".strip()
                                
                            songs_to_download.append({
                                "id": vid_id,
                                "url": url,
                                "title": title,
                                "artist": artist if artist else "Unknown",
                                # Pin the exact remote filename on disk so the
                                # merged row resolves instead of dangling.
                                "target_file": file,
                            })
                
                # Sync other tables
                for row in remote_playlists:
                    local_conn.execute("""
                        INSERT INTO Playlists (id, title, description, thumbnail) VALUES (?, ?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            title = excluded.title,
                            description = excluded.description,
                            thumbnail = excluded.thumbnail
                    """, row)
                for row in remote_song_playlist:
                    local_conn.execute("""
                        INSERT INTO Song_Playlist (id, song_file, playlist_id, date_added) VALUES (?, ?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            song_file = excluded.song_file,
                            playlist_id = excluded.playlist_id,
                            date_added = excluded.date_added
                    """, row)
                for row in remote_lyrics:
                    # Web chapter-cache rows ("{file}::chapter:{id}") are never
                    # read back (chapters sync via podcast_chapters).
                    if "::chapter:" in (row[1] or ""):
                        continue
                    local_conn.execute("""
                        INSERT INTO Lyrics (id, song_file, lyrics) VALUES (?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            song_file = excluded.song_file,
                            lyrics = excluded.lyrics
                    """, row)
                for song_file, offset_ms in remote_lyrics_offsets:
                    if "::chapter:" in (song_file or ""):
                        continue
                    try:
                        local_conn.execute(
                            "UPDATE Lyrics SET offset_ms = ? WHERE song_file = ?",
                            (offset_ms, song_file),
                        )
                    except sqlite3.OperationalError:
                        pass
                for row in remote_music_history:
                    local_conn.execute("""
                        INSERT INTO Music_History (id, song_file, date_played) VALUES (?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            song_file = excluded.song_file,
                            date_played = excluded.date_played
                    """, row)
                for row in remote_playlist_history:
                    local_conn.execute("""
                        INSERT INTO Playlist_History (id, playlist_id, date_played) VALUES (?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            playlist_id = excluded.playlist_id,
                            date_played = excluded.date_played
                    """, row)
                # Podcast tags + links (upsert by id, like song_playlist: an
                # empty snapshot never wipes local tags)
                local_conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Tags (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name varchar(255) NOT NULL UNIQUE
                    )
                """)
                local_conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Tag_Links (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        podcast_file varchar(255) NOT NULL,
                        tag_id bigint NOT NULL
                    )
                """)
                local_conn.execute("""
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_podcast_tag_links_pair
                    ON Podcast_Tag_Links(podcast_file, tag_id)
                """)
                for row in remote_podcast_tags:
                    local_conn.execute("""
                        INSERT INTO Podcast_Tags (id, name) VALUES (?, ?)
                        ON CONFLICT(id) DO UPDATE SET name = excluded.name
                    """, row)
                for row in remote_podcast_tag_links:
                    local_conn.execute("""
                        INSERT INTO Podcast_Tag_Links (id, podcast_file, tag_id) VALUES (?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            podcast_file = excluded.podcast_file,
                            tag_id = excluded.tag_id
                    """, row)
                # Episode chapters (upsert by id; empty snapshot never wipes local)
                local_conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Chapters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        podcast_file varchar(255) NOT NULL,
                        name varchar(255) NOT NULL,
                        start_secs real NOT NULL,
                        end_secs real NULL
                    )
                """)
                local_conn.execute("""
                    CREATE INDEX IF NOT EXISTS idx_podcast_chapters_file
                    ON Podcast_Chapters(podcast_file, start_secs)
                """)
                for row in remote_podcast_chapters:
                    local_conn.execute("""
                        INSERT INTO Podcast_Chapters (id, podcast_file, name, start_secs, end_secs)
                        VALUES (?, ?, ?, ?, ?)
                        ON CONFLICT(id) DO UPDATE SET
                            podcast_file = excluded.podcast_file,
                            name = excluded.name,
                            start_secs = excluded.start_secs,
                            end_secs = excluded.end_secs
                    """, row)
                # Adopt the remote daily mix (same mix of the day on every device)
                if remote_daily_mix:
                    local_conn.execute(
                        "CREATE TABLE IF NOT EXISTS Daily_Mix ("
                        "mix_date varchar(10) PRIMARY KEY, song_files text not null, "
                        "created_at datetime default CURRENT_TIMESTAMP)"
                    )
                    for mix_date, song_files in remote_daily_mix:
                        try:
                            files = json.loads(song_files)
                        except Exception:
                            continue
                        if not isinstance(files, list):
                            continue
                        local_conn.execute(
                            "INSERT INTO Daily_Mix (mix_date, song_files) VALUES (?, ?) "
                            "ON CONFLICT(mix_date) DO UPDATE SET song_files = excluded.song_files",
                            (mix_date, json.dumps(files)),
                        )
                    local_conn.execute(
                        "DELETE FROM Daily_Mix WHERE mix_date < ?",
                        (datetime.date.today().isoformat(),),
                    )
                # Sync podcasts: rows + missing-file downloads into the podcasts folder
                for file, downloaded_link, title, date_download, artist in remote_podcasts:
                    cursor = local_conn.execute("SELECT file FROM Podcasts WHERE file = ?", (file,))
                    if not cursor.fetchone():
                        local_conn.execute(
                            "INSERT INTO Podcasts (file, downloaded_link, title, date_download, artist) VALUES (?, ?, ?, ?, ?)",
                            (file, downloaded_link, title, date_download, artist)
                        )
                        podcasts_added += 1

                        file_path = os.path.join(settings.podcasts_path, file)
                        if not os.path.exists(file_path):
                            if downloaded_link:
                                vid_id = downloaded_link.split("v=")[-1] if "v=" in downloaded_link else downloaded_link.split("/")[-1]
                                url = downloaded_link
                            else:
                                vid_id = ""
                                url = f"{title} {artist if artist else ''} audio".strip()

                            podcasts_to_download.append({
                                "id": vid_id,
                                "url": url,
                                "title": title,
                                "artist": artist if artist else "Unknown",
                                "is_podcast": True,
                                "target_file": file,
                            })
                    
        except Exception as e:
            return f"Error syncing to local DB: {e}"
        
        if songs_to_download or podcasts_to_download:
            for song_data in songs_to_download + podcasts_to_download:
                try:
                    self.api.download_manager.submit(song_data, block=False)
                except Exception as e:
                    print(f" [Python] Could not enqueue download for {song_data.get('title')}: {e}")

            def _complete():
                if getattr(self.api, '_window', None):
                    self.api._window.evaluate_js("if (typeof window.pywebview !== 'undefined' && typeof window.pywebview.api !== 'undefined') window.pywebview.api.send_song_list();")

            self.api.download_manager.set_idle_callback(_complete)

        msg = f"Synced {added_count} new entries from remote DB. Started downloading {len(songs_to_download)} songs."
        if podcasts_added or podcasts_to_download:
            msg += f" Podcasts: {podcasts_added} new entries, {len(podcasts_to_download)} downloads started."
        return msg

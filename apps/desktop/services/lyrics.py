import json
import urllib.request
import urllib.parse
import re
import sqlite3
import unicodedata

try:
    import pykakasi
    kks = pykakasi.kakasi()
except ImportError:
    kks = None

class LyricsService:
    CHAPTER_KEY_MARKER = "::chapter:"
    _BRACKET_QUALIFIER = re.compile(
        r'\s*[\(\[].*?(remaster|mix|version|edit|live|feat\.|ft\.).*?[\)\]]',
        re.IGNORECASE)

    def __init__(self, api):
        self.api = api

    # ==========================
    # Podcast chapter lyrics (web parity)
    # ==========================
    @classmethod
    def chapter_key(cls, file, chapter_id):
        """Cache key for one chapter's lyrics ("{file}::chapter:{id}")."""
        return f"{file}{cls.CHAPTER_KEY_MARKER}{int(chapter_id)}"

    @classmethod
    def _clean_chapter_text(cls, text):
        return cls._BRACKET_QUALIFIER.sub('', text or '').strip()

    @classmethod
    def _split_chapter_name(cls, name):
        """Split "Title - Artist" (DJ-mix style); artist may be empty."""
        title = cls._clean_chapter_text(name)
        if ' - ' not in title:
            return title, ''
        head, tail = title.split(' - ', 1)
        return head.strip(), tail.strip()

    @classmethod
    def _normalize_title(cls, value):
        """Folded comparison form: case/punctuation/diacritics/brackets."""
        s = cls._clean_chapter_text(value).lower()
        s = unicodedata.normalize('NFD', s)
        s = ''.join(ch for ch in s if unicodedata.category(ch) != 'Mn')
        s = ''.join(ch if ch.isalnum() else ' ' for ch in s)
        return re.sub(r'\s+', ' ', s).strip()

    def _song_is_current_podcast(self, song_file):
        song = getattr(self.api, 'last_song', None) or {}
        return bool(song.get('IsPodcast')) and song.get('File') == song_file

    def _active_chapter(self, song_file=None):
        """Last named chapter starting at/before the current playback time."""
        try:
            file = song_file or self.api.current_filename
            if not file or not self._song_is_current_podcast(file):
                return None
            db = getattr(self.api, 'db', None)
            if db is None:
                return None
            chapters = db.get_podcast_chapters(file) or []
            if not chapters:
                return None
            try:
                pos = float(self.api.playback.get_current_pos() or 0)
            except Exception:
                pos = 0.0
            active = None
            active_name = ''
            for chapter in chapters:
                name = (chapter.get('name') or '').strip()
                if not name:
                    continue
                if float(chapter.get('startSecs') or 0) <= pos + 0.05:
                    active = chapter
                    active_name = name
                else:
                    break
            if not active:
                return None
            return {
                'id': int(active.get('id')),
                'name': active_name,
                'start_secs': float(active.get('startSecs') or 0),
                'end_secs': (None if active.get('endSecs') is None
                             else float(active.get('endSecs'))),
            }
        except Exception as e:
            print(f" [Python] Chapter lyrics context error: {e}")
            return None

    def _chapter_by_id(self, chapter_id):
        """Resolve an explicit chapter id against the current episode."""
        try:
            file = self.api.current_filename
            if not file or chapter_id is None:
                return None
            db = getattr(self.api, 'db', None)
            if db is None:
                return None
            for chapter in db.get_podcast_chapters(file) or []:
                if int(chapter.get('id') or 0) != int(chapter_id):
                    continue
                name = (chapter.get('name') or '').strip()
                if not name:
                    return None
                return {
                    'id': int(chapter.get('id')),
                    'name': name,
                    'start_secs': float(chapter.get('startSecs') or 0),
                    'end_secs': (None if chapter.get('endSecs') is None
                                 else float(chapter.get('endSecs'))),
                }
        except Exception as e:
            print(f" [Python] Chapter lyrics lookup error: {e}")
        return None

    def _lyrics_payload(self, synced, plain, chapter=None):
        if kks:
            synced = self.romanize_text(synced, is_lrc=True) if synced else None
            plain = self.romanize_text(plain, is_lrc=False) if plain else None
        payload = {"synced": synced, "plain": plain}
        if chapter:
            payload.update({
                "chapter_id": chapter.get('id'),
                "chapter_name": chapter.get('name'),
                "chapter_start_secs": chapter.get('start_secs') or 0,
            })
        return payload

    def romanize_text(self, text, is_lrc=False):
        if not kks or not text:
            return text
            
        lines = text.split('\n')
        romanized_lines = []
        for line in lines:
            if is_lrc:
                match = re.match(r'^(\[\d+:\d+\.\d+\])(.*)$', line)
                if match:
                    timestamp = match.group(1)
                    content = match.group(2)
                    
                    converted = kks.convert(content)
                    romaji = " ".join([item['hepburn'] for item in converted])
                    romaji = re.sub(r'\s+', ' ', romaji).strip() # Clean up double spaces
                    
                    romanized_lines.append(f"{timestamp} {romaji}")
                else:
                    romanized_lines.append(line)
            else:
                converted = kks.convert(line)
                romaji = " ".join([item['hepburn'] for item in converted])
                romaji = re.sub(r'\s+', ' ', romaji).strip()
                romanized_lines.append(romaji)
                
        return '\n'.join(romanized_lines)

    def _clean_track_artist(self, track_name, artist_name):
        clean_track = track_name.replace('？', '?').replace('！', '!')
        clean_track = re.sub(r'\s*[\(\[].*?(remaster|mix|version|edit|live|feat\.|ft\.).*?[\)\]]', '', clean_track, flags=re.IGNORECASE).strip()
        clean_artist = artist_name or ""

        if clean_artist.lower() in ["unknown", "unknown artist", ""] and " - " in clean_track:
            parts = clean_track.split(" - ", 1)
            clean_artist = parts[0].strip()
            clean_track = parts[1].strip()

        return clean_track, clean_artist

    @staticmethod
    def _artist_usable(artist_name):
        """Whether the artist is real enough for an exact (/api/get) lookup."""
        if not artist_name:
            return False
        return artist_name.strip().lower() not in ("unknown", "unknown artist")

    def _lyrics_track_only_search(self, clean_track, duration_seconds=None):
        """Fallback for songs with no known artist: track-only /api/search.

        Accepts only a case-insensitive exact track-name match (skipping
        instrumentals), so it can never attach the wrong song's lyrics.
        Returns None quietly when nothing unambiguous is found.
        """
        if not clean_track:
            return None
        try:
            params = {"track_name": clean_track}
            if duration_seconds:
                params["duration"] = int(duration_seconds)
            url = "https://lrclib.net/api/search?" + urllib.parse.urlencode(params)
            req = urllib.request.Request(url, headers={'User-Agent': 'MyMusicPlayer/1.0'})
            with urllib.request.urlopen(req) as response:
                if response.status != 200:
                    return None
                results = json.loads(response.read().decode()) or []
            norm = re.sub(r'\s+', ' ', clean_track).strip().lower()
            for item in results:
                if not isinstance(item, dict) or item.get("instrumental"):
                    continue
                cand = re.sub(r'\s+', ' ', str(item.get("trackName") or "")).strip().lower()
                if not cand or cand != norm:
                    continue
                synced = item.get("syncedLyrics")
                plain = item.get("plainLyrics")
                if not synced and not plain:
                    return None
                self.save_lyrics_for_current(synced, plain)
                if kks:
                    synced = self.romanize_text(synced, is_lrc=True) if synced else None
                    plain = self.romanize_text(plain, is_lrc=False) if plain else None
                return {"synced": synced, "plain": plain}
            return None
        except urllib.error.HTTPError as e:
            print(f" [Python] Lyrics track search failed (HTTP {e.code}) for '{clean_track}'")
            return None
        except Exception as e:
            print(f" [Python] Failed to fetch lyrics: {e}")
            return None

    def _lrclib_search(self, track, artist, album_name=None, duration_seconds=None):
        """Raw lrclib /api/search rows (empty list on any failure)."""
        if not track:
            return []
        params = {"track_name": track}
        if artist:
            params["artist_name"] = artist
        if album_name:
            params["album_name"] = album_name
        if duration_seconds:
            params["duration"] = int(duration_seconds)
        url = "https://lrclib.net/api/search?" + urllib.parse.urlencode(params)
        req = urllib.request.Request(url, headers={'User-Agent': 'MyMusicPlayer/1.0'})
        try:
            with urllib.request.urlopen(req) as response:
                if response.status != 200:
                    return []
                results = json.loads(response.read().decode()) or []
                return [item for item in results if isinstance(item, dict)]
        except Exception as e:
            print(f" [Python] Lyrics search failed: {e}")
            return []

    def _pick_chapter_candidate(self, title, artist, results):
        """First candidate whose normalized title/artist match the chapter."""
        want_title = self._normalize_title(title)
        if not want_title:
            return None
        want_artist = self._normalize_title(artist)
        for item in results or []:
            if item.get("instrumental"):
                continue
            if not (item.get("syncedLyrics") or item.get("plainLyrics")):
                continue
            if self._normalize_title(item.get("trackName")) != want_title:
                continue
            if want_artist:
                have_artist = self._normalize_title(item.get("artistName"))
                if not have_artist:
                    continue
                if want_artist not in have_artist and have_artist not in want_artist:
                    continue
            return item
        return None

    def _fetch_chapter_lyrics(self, chapter):
        """Search by the chapter's title/artist and cache under its key."""
        title, artist = self._split_chapter_name(chapter.get('name'))
        if not title:
            return None
        results = self._lrclib_search(title, artist)
        best = self._pick_chapter_candidate(title, artist, results)
        if not best:
            return None
        synced = best.get("syncedLyrics")
        plain = best.get("plainLyrics")
        if not synced and not plain:
            return None
        self._save_lyrics_row(
            self.chapter_key(self.api.current_filename, chapter['id']),
            synced, plain)
        return {"synced": synced, "plain": plain}

    def _save_lyrics_row(self, cache_key, synced, plain):
        """Upsert one lyrics row, keeping the first row's offset and pruning duplicates."""
        if not cache_key or (not synced and not plain):
            return False
        try:
            lyrics_json = json.dumps({"synced": synced, "plain": plain})
            with sqlite3.connect(self.api.db_path) as conn:
                rows = conn.execute(
                    "SELECT id FROM Lyrics WHERE song_file = ? ORDER BY id",
                    (cache_key,)
                ).fetchall()
                if rows:
                    conn.execute(
                        "UPDATE Lyrics SET lyrics = ? WHERE id = ?",
                        (lyrics_json, rows[0][0])
                    )
                    for duplicate in rows[1:]:
                        conn.execute("DELETE FROM Lyrics WHERE id = ?", (duplicate[0],))
                else:
                    conn.execute(
                        "INSERT INTO Lyrics (song_file, lyrics, offset_ms) VALUES (?, ?, 0)",
                        (cache_key, lyrics_json)
                    )
            return True
        except Exception as e:
            print(f" [Python] Lyrics cache write error: {e}")
            return False

    def save_lyrics_for_current(self, synced, plain, chapter_id=None):
        """Cache lyrics for the current song or one of its podcast chapters."""
        song_file = self.api.current_filename
        if not song_file or (not synced and not plain):
            return False
        cache_key = song_file
        if chapter_id is not None:
            chapter = self._chapter_by_id(chapter_id)
            if chapter:
                cache_key = self.chapter_key(song_file, chapter['id'])
        return self._save_lyrics_row(cache_key, synced, plain)

    def delete_lyrics_for_current(self, chapter_id=None):
        """Drop saved lyrics for the current song/chapter (cache miss refetches)."""
        song_file = self.api.current_filename
        if not song_file:
            return False
        cache_key = song_file
        if chapter_id is not None:
            chapter = self._chapter_by_id(chapter_id)
            if chapter:
                cache_key = self.chapter_key(song_file, chapter['id'])
        try:
            with sqlite3.connect(self.api.db_path) as conn:
                cur = conn.execute("DELETE FROM Lyrics WHERE song_file = ?", (cache_key,))
                return cur.rowcount > 0
        except Exception as e:
            print(f" [Python] Lyrics delete error: {e}")
            return False

    def get_lyrics(self, track_name, artist_name, album_name=None, duration_seconds=None):
        song_file = self.api.current_filename
        chapter = self._active_chapter(song_file)
        cache_key = self.chapter_key(song_file, chapter['id']) if (song_file and chapter) else song_file

        # 1. Check local cache first (enables offline lyrics)
        if cache_key:
            try:
                with sqlite3.connect(self.api.db_path) as conn:
                    cursor = conn.execute(
                        "SELECT lyrics FROM Lyrics WHERE song_file = ? ORDER BY id LIMIT 1",
                        (cache_key,)
                    )
                    row = cursor.fetchone()
                    if row and row[0]:
                        cached = json.loads(row[0])
                        return self._lyrics_payload(
                            cached.get("synced"), cached.get("plain"), chapter)
            except Exception as e:
                print(f" [Python] Lyrics cache read error: {e}")

        # 1b. Chapter context: search by the chapter's "Title - Artist"
        # and cache under the chapter key (never the episode row).
        if chapter:
            fetched = self._fetch_chapter_lyrics(chapter)
            if not fetched:
                return None
            return self._lyrics_payload(
                fetched.get("synced"), fetched.get("plain"), chapter)

        # 2. Fetch from lrclib.net on cache miss
        clean_track, clean_artist = self._clean_track_artist(track_name, artist_name)
        if not clean_track or not self._artist_usable(clean_artist):
            # /api/get requires both fields (else HTTP 400): fall back to a
            # track-only search and accept only an unambiguous match.
            return self._lyrics_track_only_search(clean_track, duration_seconds)

        base_url = "https://lrclib.net/api/get"

        params = {
            "track_name": clean_track,
            "artist_name": clean_artist
        }
        if duration_seconds:
            params["duration"] = int(duration_seconds)

        query_string = urllib.parse.urlencode(params)
        url = f"{base_url}?{query_string}"
        
        headers = {'User-Agent': 'MyMusicPlayer/1.0'}
        req = urllib.request.Request(url, headers=headers)
        
        try:
            with urllib.request.urlopen(req) as response:
                if response.status == 200:
                    data = json.loads(response.read().decode())
                    
                    synced = data.get("syncedLyrics")
                    plain = data.get("plainLyrics")

                    if not synced and not plain:
                        return None

                    # 3. Cache the raw (un-romanized) lyrics for future offline use
                    self.save_lyrics_for_current(synced, plain)

                    return self._lyrics_payload(synced, plain)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                print(f" [Python] Lyrics not found for {track_name} by {artist_name}")
            else:
                print(f" [Python] Lyrics lookup failed (HTTP {e.code}) for '{track_name}' by '{artist_name}'")
        except Exception as e:
            print(f" [Python] Failed to fetch lyrics: {e}")

        return None

    def search_lyrics(self, track_name, artist_name, album_name=None, duration_seconds=None):
        """Search lrclib for candidate lyrics when an exact match isn't found."""
        chapter = self._active_chapter(self.api.current_filename)
        if chapter:
            # Chapter search is strict: normalized title must match and, when
            # the chapter names an artist, the candidate artist must too.
            title, artist = self._split_chapter_name(chapter.get('name'))
            if not title:
                return []
            results = self._lrclib_search(title, artist)
            suggestions = []
            for item in results:
                if self._pick_chapter_candidate(title, artist, [item]) is None:
                    continue
                suggestions.append({
                    "id": item.get("id"),
                    "trackName": item.get("trackName") or "",
                    "artistName": item.get("artistName") or "",
                    "albumName": item.get("albumName"),
                    "duration": item.get("duration"),
                    "syncedLyrics": item.get("syncedLyrics"),
                    "plainLyrics": item.get("plainLyrics"),
                    "chapterId": chapter['id'],
                    "chapterStartSecs": chapter.get('start_secs') or 0,
                })
                if len(suggestions) >= 10:
                    break
            return suggestions

        if not track_name:
            return []

        clean_track, clean_artist = self._clean_track_artist(track_name, artist_name)
        results = self._lrclib_search(
            clean_track, clean_artist, album_name, duration_seconds)

        suggestions = []
        seen = set()
        for item in results:
            if item.get("instrumental"):
                continue
            if not (item.get("syncedLyrics") or item.get("plainLyrics")):
                continue
            track = item.get("trackName") or ""
            artist = item.get("artistName") or ""
            key = (track.lower(), artist.lower())
            if key in seen:
                continue
            seen.add(key)
            suggestions.append({
                "id": item.get("id"),
                "trackName": track,
                "artistName": artist,
                "albumName": item.get("albumName"),
                "duration": item.get("duration"),
                "syncedLyrics": item.get("syncedLyrics"),
                "plainLyrics": item.get("plainLyrics"),
            })
            if len(suggestions) >= 10:
                break
        return suggestions

import os
import time
import re
import json
import base64
import sqlite3
import threading
import pygame
from mutagen.mp3 import MP3
from mutagen.id3 import ID3, TIT2, TPE1, TALB, TDRC, APIC
import settings

from .library_moves import LibraryMoveError, move_between_libraries


class MetadataManager:
    def __init__(self, api):
        self.api = api
        self._pending_lock = threading.RLock()
        self._pending_edits = {}
        try:
            playback = getattr(api, 'playback', None)
            if playback is not None:
                playback.add_track_changed_listener(self._on_track_changed)
                playback.add_queue_exhausted_listener(self._on_queue_exhausted)
        except Exception as exc:
            print(f" [Python] Metadata pending-edit hook error: {exc}")

    def get_song_metadata(self, file_path, file_name, include_cover=False):
        song_data = {
            'File': file_name,
            'Artist': 'Unknown',
            'Title': file_name.replace('.mp3', ''),
            'Album': 'Unknown',
            'Year': 'Unknown',
            'Duration': 0,
            'CoverArt': None,
        }
        if os.path.exists(file_path):
            try:
                song = ID3(file_path)
                song_mp3 = MP3(file_path)
                song_data['Artist'] = str(song.get('TPE1')) if 'TPE1' in song else 'Unknown'
                song_data['Title'] = str(song.get('TIT2')) if 'TIT2' in song else file_name.replace('.mp3', '')
                song_data['Album'] = str(song.get('TALB')) if 'TALB' in song else 'Unknown'
                song_data['Year'] = str(song.get('TDRC')) if 'TDRC' in song else 'Unknown'
                song_data['Duration'] = song_mp3.info.length
                
                if include_cover:
                    for key in song.keys():
                        if key.startswith('APIC'):
                            apic = song[key]
                            img_data = base64.b64encode(apic.data).decode('utf-8')
                            song_data['CoverArt'] = f"data:{apic.mime};base64,{img_data}"
                            break
            except Exception as e:
                print(f" [Python] Failed to load metadata for {file_name}: {e}")
        return song_data

    def get_cover_art_base64(self, file_name, base_path=None):
        file_path = os.path.join(base_path or settings.path, file_name)
        if os.path.exists(file_path):
            try:
                song_file = ID3(file_path)
                for key in song_file.keys():
                    if key.startswith('APIC'):
                        apic = song_file[key]
                        img_data = base64.b64encode(apic.data).decode('utf-8')
                        return f"data:{apic.mime};base64,{img_data}"
            except Exception:
                pass
        return None

    def get_local_image_base64(self, file_path):
        import mimetypes
        if file_path and os.path.exists(file_path):
            try:
                with open(file_path, "rb") as image_file:
                    encoded_string = base64.b64encode(image_file.read()).decode('utf-8')
                    mime_type, _ = mimetypes.guess_type(file_path)
                    if not mime_type:
                        mime_type = "image/jpeg"
                    return f"data:{mime_type};base64,{encoded_string}"
            except Exception as e:
                print(f" [Python] Error loading local image: {e}")
        return None

    def _advance_after_delete(self, was_current):
        if not was_current:
            return
        self.api.current_filename = None
        self.api.last_song = {}
        try:
            with sqlite3.connect(self.api.db_path) as conn:
                conn.execute("UPDATE Settings SET current_song = NULL WHERE id = 1")
        except Exception:
            pass
        playback = getattr(self.api, 'playback', None)
        if playback is None:
            return
        try:
            playback._notify_queue_changed(None)
        except Exception:
            pass
        try:
            if playback.next_songs:
                playback.play_next(auto=True)
        except Exception as exc:
            print(f" [Python] Could not advance after deleting current track: {exc}")

    def delete_song(self, song_data):
        filename = song_data.get('File')
        self.cancel_pending_edit(filename)
        total_path = os.path.join(settings.path, filename)
        
        # We need to access api.playback, but playback isn't created yet maybe?
        # Let's rely on api properties or method calls
        last_filename = self.api.last_song.get('File')
        was_current = str(last_filename) == str(filename)
        
        if was_current:
            pygame.mixer.music.stop()
            if hasattr(pygame.mixer.music, 'unload'):
                pygame.mixer.music.unload()
            self.api.playing = False
            self.api.first_play = True
            
            if hasattr(self.api, 'media_controls'):
                self.api.media_controls.set_stopped()
                
            if getattr(self.api, '_window', None):
                try:
                    self.api._window.evaluate_js("if (typeof window.update_play_button_ui === 'function') { window.update_play_button_ui(false); }")
                except Exception as e:
                    print(f" [Python] update_play_button_ui JS error: {e}")

        if os.path.exists(total_path):
            try:
                os.remove(total_path)
                self.api.song_list = [s for s in getattr(self.api, 'song_list', []) if s.get('File') != filename]
                
                # Remove the local DB rows so the sync doesn't re-upload the deleted song
                try:
                    with sqlite3.connect(self.api.db_path) as conn:
                        conn.execute("DELETE FROM Song_Playlist WHERE song_file = ?", (filename,))
                        conn.execute("DELETE FROM Lyrics WHERE song_file = ?", (filename,))
                        conn.execute("DELETE FROM Music_History WHERE song_file = ?", (filename,))
                        conn.execute("DELETE FROM Songs WHERE file = ?", (filename,))
                    if getattr(self.api, 'db', None):
                        self.api.db.record_deletion("songs", filename)
                        self.api.db.record_deletion("lyrics", filename)
                        self.api.db.record_deletion("music_history", filename)
                except Exception as db_err:
                    print(f" [Python] Error cleaning up song from database: {db_err}")
                
                # Check playback queue
                if hasattr(self.api, 'playback'):
                    self.api.playback.next_songs = [s for s in getattr(self.api.playback, 'next_songs', []) if s.get('File') != filename]
                    self.api.playback.prev_songs = [s for s in getattr(self.api.playback, 'prev_songs', []) if s.get('File') != filename]
                    queue_json = json.dumps(self.api.playback.next_songs)
                else:
                    self.api.next_songs = [s for s in getattr(self.api, 'next_songs', []) if s.get('File') != filename]
                    self.api.prev_songs = [s for s in getattr(self.api, 'prev_songs', []) if s.get('File') != filename]
                    queue_json = json.dumps(self.api.next_songs)
                    
                if getattr(self.api, '_window', None):
                    self.api._window.evaluate_js(f"window.queue_songs = {queue_json}; if (typeof window.update_queue_ui === 'function') window.update_queue_ui();")
            except Exception as e:
                print(f" [Python] Error deleting file: {e}")
        self._advance_after_delete(was_current)

    def delete_podcast(self, song_data):
        """Delete a podcast episode: file + Podcasts row (+ remote tombstone)."""
        filename = song_data.get('File')
        self.cancel_pending_edit(filename)
        total_path = os.path.join(settings.podcasts_path, filename)

        last_filename = self.api.last_song.get('File')
        was_current = str(last_filename) == str(filename)

        if was_current:
            pygame.mixer.music.stop()
            if hasattr(pygame.mixer.music, 'unload'):
                pygame.mixer.music.unload()
            self.api.playing = False
            self.api.first_play = True

            if hasattr(self.api, 'media_controls'):
                self.api.media_controls.set_stopped()

            if getattr(self.api, '_window', None):
                try:
                    self.api._window.evaluate_js("if (typeof window.update_play_button_ui === 'function') { window.update_play_button_ui(false); }")
                except Exception as e:
                    print(f" [Python] update_play_button_ui JS error: {e}")

        if os.path.exists(total_path):
            try:
                os.remove(total_path)
                try:
                    with sqlite3.connect(self.api.db_path) as conn:
                        conn.execute("DELETE FROM Podcast_Tag_Links WHERE podcast_file = ?", (filename,))
                        conn.execute("DELETE FROM Podcast_Chapters WHERE podcast_file = ?", (filename,))
                        conn.execute("DELETE FROM Podcasts WHERE file = ?", (filename,))
                    if getattr(self.api, 'db', None):
                        self.api.db.record_deletion("podcasts", filename)
                        self.api.db.record_deletion("podcast_chapters", filename)
                except Exception as db_err:
                    print(f" [Python] Error cleaning up podcast from database: {db_err}")

                if hasattr(self.api, 'playback'):
                    self.api.playback.next_songs = [s for s in getattr(self.api.playback, 'next_songs', []) if s.get('File') != filename]
                    self.api.playback.prev_songs = [s for s in getattr(self.api.playback, 'prev_songs', []) if s.get('File') != filename]
                    queue_json = json.dumps(self.api.playback.next_songs)
                else:
                    self.api.next_songs = [s for s in getattr(self.api, 'next_songs', []) if s.get('File') != filename]
                    self.api.prev_songs = [s for s in getattr(self.api, 'prev_songs', []) if s.get('File') != filename]
                    queue_json = json.dumps(self.api.next_songs)

                if getattr(self.api, '_window', None):
                    self.api._window.evaluate_js(f"window.queue_songs = {queue_json}; if (typeof window.update_queue_ui === 'function') window.update_queue_ui();")
            except Exception as e:
                print(f" [Python] Error deleting file: {e}")
        self._advance_after_delete(was_current)

    def move_song_to_podcast(self, song_data):
        """Move a song into the Podcasts library (file kept, tags/history dropped)."""
        return self._move_library_file(song_data, to_podcast=True)

    def move_podcast_to_song(self, song_data):
        """Move an episode into the Songs library (file kept, chapters/tags dropped)."""
        return self._move_library_file(song_data, to_podcast=False)

    def _stop_for_library_move(self, filename):
        """The source file is about to leave this folder: release it first."""
        current = str((getattr(self.api, 'last_song', None) or {}).get('File') or '')
        if not current:
            current = str(getattr(self.api, 'current_filename', '') or '')
        if current != filename:
            return False
        try:
            pygame.mixer.music.stop()
            if hasattr(pygame.mixer.music, 'unload'):
                pygame.mixer.music.unload()
        except Exception as exc:
            print(f" [Python] Move-stop playback error: {exc}")
        self.api.playing = False
        self.api.first_play = True
        self.api.current_filename = None
        self.api.last_song = {}
        try:
            conn = sqlite3.connect(self.api.db_path)
            try:
                conn.execute("UPDATE Settings SET current_song = NULL WHERE id = 1")
                conn.commit()
            finally:
                conn.close()
        except Exception as exc:
            print(f" [Python] Could not clear current_song after move: {exc}")
        if hasattr(self.api, 'media_controls'):
            try:
                self.api.media_controls.set_stopped()
            except Exception:
                pass
        if getattr(self.api, '_window', None):
            try:
                self.api._window.evaluate_js(
                    "if (typeof window.update_play_button_ui === 'function') "
                    "{ window.update_play_button_ui(false); }"
                )
            except Exception:
                pass
        return True

    def _drop_from_queues(self, filename):
        playback = getattr(self.api, 'playback', None)
        removed = False
        for owner, attr in (
            (playback, 'next_songs'),
            (playback, 'prev_songs'),
            (playback, 'unshuffled_song_list'),
            (self.api, 'next_songs'),
            (self.api, 'prev_songs'),
            (self.api, 'unshuffled_song_list'),
        ):
            if owner is None:
                continue
            songs = getattr(owner, attr, None)
            if not isinstance(songs, list):
                continue
            kept = [s for s in songs if (s or {}).get('File') != filename]
            if len(kept) != len(songs):
                removed = True
                setattr(owner, attr, kept)
        if removed and playback is not None:
            try:
                playback._persist_queue()
            except Exception:
                pass
            try:
                playback._notify_queue_changed(None)
            except Exception:
                pass
        if getattr(self.api, '_window', None):
            try:
                queue = getattr(playback, 'next_songs', []) if playback is not None else []
                self.api._window.evaluate_js(
                    f"window.queue_songs = {json.dumps(queue)}; "
                    "if (typeof window.update_queue_ui === 'function') window.update_queue_ui();"
                )
            except Exception:
                pass
        return removed

    def _move_library_file(self, song_data, to_podcast):
        filename = str((song_data or {}).get('File') or '').strip()
        if not filename:
            return {"ok": False, "message": "No audio file was selected."}
        self.cancel_pending_edit(filename)
        self._stop_for_library_move(filename)
        try:
            result = move_between_libraries(
                self.api.db_path,
                settings.path,
                settings.podcasts_path,
                filename,
                to_podcast=to_podcast,
            )
        except LibraryMoveError as exc:
            return {"ok": False, "message": str(exc)}
        except Exception as exc:
            print(f" [Python] Library move failed: {exc}")
            return {"ok": False, "message": "The file could not be moved."}
        self._drop_from_queues(filename)
        try:
            self.api.song_list = self.api.db.get_all_songs()
        except Exception as exc:
            print(f" [Python] Could not refresh song list after move: {exc}")
        return result

    def _on_track_changed(self, song):
        new_file = str((song or {}).get('File') or '')
        try:
            threading.Thread(
                target=self.apply_pending_edits,
                kwargs={'exclude_file': new_file},
                daemon=True,
            ).start()
        except Exception as exc:
            print(f" [Python] Pending metadata apply error: {exc}")

    def _on_queue_exhausted(self):
        # Nothing is loaded now; clear the current pointer so applying a
        # pending edit cannot reload/restart the last track.
        try:
            if not getattr(self.api, 'playing', False):
                self.api.current_filename = None
                self.api.last_song = {}
        except Exception:
            pass
        return self.apply_pending_edits()

    def update_song_metadata(self, song_data):
        """Apply immediately, or hold a playing file's edit until it changes."""
        filename = str((song_data or {}).get('File') or '')
        current = str((getattr(self.api, 'last_song', None) or {}).get('File') or '')
        if not current:
            current = str(getattr(self.api, 'current_filename', '') or '')
        if filename and current == filename:
            with self._pending_lock:
                self._pending_edits[filename] = dict(song_data or {})
            return {
                "ok": True,
                "pending": True,
                "file": filename,
                "message": "Pending edit - applies when the track changes.",
            }
        return self._write_song_metadata(song_data)

    def cancel_pending_edit(self, filename):
        with self._pending_lock:
            return self._pending_edits.pop(str(filename), None) is not None

    def apply_pending_edits(self, exclude_file=None):
        """Write queued edits whose files are no longer loaded by pygame."""
        with self._pending_lock:
            pending = [
                (name, data) for name, data in self._pending_edits.items()
                if name != exclude_file
            ]
        applied = []
        for filename, data in pending:
            try:
                ok = self._write_song_metadata(data)
            except Exception as exc:
                print(f" [Python] Pending metadata write failed for {filename}: {exc}")
                ok = False
            if ok:
                with self._pending_lock:
                    self._pending_edits.pop(filename, None)
                applied.append(filename)
        return applied

    def _write_song_metadata(self, song_data):
        filename = song_data.get('File')
        print(f" [Python] Updating metadata for: {filename}")
        try:
            is_podcast = bool(song_data.get('IsPodcast'))
            base_path = settings.podcasts_path if is_podcast else settings.path
            total_path = os.path.join(base_path, filename)
            if not os.path.exists(total_path):
                return False

            last_filename = self.api.last_song.get('File')
            is_current_song = (str(last_filename) == str(filename))
            was_playing = False
            resume_pos = 0

            if is_current_song:
                was_playing = getattr(self.api, 'playing', False)
                if hasattr(self.api, 'playback'):
                    resume_pos = self.api.playback.get_current_pos()
                else:
                    resume_pos = self.api.get_current_pos()
                    
                pygame.mixer.music.stop()
                if hasattr(pygame.mixer.music, 'unload'):
                    pygame.mixer.music.unload()

            audio = MP3(total_path)
            if audio.tags is None:
                audio.add_tags()
            
            if 'Title' in song_data:
                audio.tags['TIT2'] = TIT2(encoding=3, text=[song_data['Title']])
            if 'Artist' in song_data:
                audio.tags['TPE1'] = TPE1(encoding=3, text=[song_data['Artist']])
            if 'Album' in song_data:
                audio.tags['TALB'] = TALB(encoding=3, text=[song_data['Album']])
            if 'Year' in song_data:
                audio.tags['TDRC'] = TDRC(encoding=3, text=[str(song_data['Year'])])
            
            if song_data.get('CoverArt'):
                cover_art = song_data['CoverArt']
                if cover_art == 'REMOVE':
                    keys_to_remove = [k for k in audio.tags.keys() if k.startswith('APIC')]
                    for k in keys_to_remove:
                        audio.tags.pop(k)
                else:
                    img_data = None
                    mime = 'image/jpeg'
                
                    if cover_art.startswith('data:'):
                        header, encoded = cover_art.split(',', 1)
                        mime = header.split(';')[0].split(':')[1]
                        img_data = base64.b64decode(encoded)
                    elif cover_art.startswith('http://') or cover_art.startswith('https://'):
                        try:
                            import urllib.request
                            headers = {'User-Agent': 'MyMusicPlayer/1.0'}
                            req = urllib.request.Request(cover_art, headers=headers)
                            with urllib.request.urlopen(req) as response:
                                img_data = response.read()
                                if '.png' in cover_art.lower():
                                    mime = 'image/png'
                                elif '.gif' in cover_art.lower():
                                    mime = 'image/gif'
                        except Exception as e:
                            print(f" [Python] Failed to download artwork from URL {cover_art}: {e}")
                    
                    if img_data:
                        keys_to_remove = [k for k in audio.tags.keys() if k.startswith('APIC')]
                        for k in keys_to_remove:
                            audio.tags.pop(k)

                        audio.tags.add(
                            APIC(
                                encoding=3, mime=mime, type=3, desc=u'Cover', data=img_data
                            )
                        )
            
            audio.save(v2_version=3)
            
            if is_current_song:
                self.api.last_song['Title'] = song_data.get('Title', self.api.last_song.get('Title', 'Unknown'))
                self.api.last_song['Artist'] = song_data.get('Artist', self.api.last_song.get('Artist', 'Unknown'))
                self.api.last_song['Album'] = song_data.get('Album', self.api.last_song.get('Album', 'Unknown'))
                self.api.last_song['Year'] = song_data.get('Year', self.api.last_song.get('Year', 'Unknown'))
                if song_data.get('CoverArt') == 'REMOVE':
                    self.api.last_song['CoverArt'] = None
                elif song_data.get('CoverArt'):
                    self.api.last_song['CoverArt'] = song_data['CoverArt']
                
                safe_song = json.dumps(self.api.last_song)
                if getattr(self.api, '_window', None):
                    self.api._window.evaluate_js(f"""
                        window.current_playing_song = {safe_song}; 
                        if (typeof window.set_active_song_ui === 'function') window.set_active_song_ui(window.current_playing_song); 
                        if (typeof window.playing_view === 'function') window.playing_view(window.current_playing_song);
                        if (typeof window.fetchLyricsForCurrentSong === 'function') window.fetchLyricsForCurrentSong(window.current_playing_song);
                    """)
                
                pygame.mixer.music.load(os.path.join(base_path, filename))
                pygame.mixer.music.play(0, resume_pos)
                
                if hasattr(self.api, 'playback'):
                    self.api.playback.current_time_offset = resume_pos
                    self.api.playback.last_play_time = time.time()
                else:
                    self.api.current_time_offset = resume_pos
                    self.api.last_play_time = time.time()
                
                if not was_playing:
                    pygame.mixer.music.pause()
                else:
                    self.api.playing = True
                    
            if hasattr(self.api, 'media_controls'):
                # The OS overlay (winsdk) is flaky and must never fail a save
                # whose tags already hit the disk.
                try:
                    self.api.media_controls.update_overlay(
                        self.api.last_song.get('Title', 'Unknown'),
                        self.api.last_song.get('Artist', 'Unknown'),
                        self.api.last_song.get('CoverArt')
                    )
                except Exception as e:
                    print(f" [Python] Media overlay error (save unaffected): {e}")

            # Update backend lists
            lists_to_update = [getattr(self.api, 'song_list', [])]
            if hasattr(self.api, 'playback'):
                lists_to_update.extend([
                    getattr(self.api.playback, 'next_songs', []),
                    getattr(self.api.playback, 'prev_songs', [])
                ])
                if hasattr(self.api.playback, 'unshuffled_song_list'):
                    lists_to_update.append(self.api.playback.unshuffled_song_list)
            else:
                lists_to_update.extend([
                    getattr(self.api, 'next_songs', []),
                    getattr(self.api, 'prev_songs', [])
                ])
                if hasattr(self.api, 'unshuffled_song_list'):
                    lists_to_update.append(self.api.unshuffled_song_list)
            
            for lst in lists_to_update:
                for s in lst:
                    if s.get('File') == filename:
                        if 'Title' in song_data: s['Title'] = song_data['Title']
                        if 'Artist' in song_data: s['Artist'] = song_data['Artist']
                        if 'Album' in song_data: s['Album'] = song_data['Album']
                        if 'Year' in song_data: s['Year'] = song_data['Year']
                        if song_data.get('CoverArt') == 'REMOVE': s['CoverArt'] = None
                        elif song_data.get('CoverArt'): s['CoverArt'] = song_data['CoverArt']

            if getattr(self.api, '_window', None):
                updated_song = next((s for s in getattr(self.api, 'song_list', []) if s.get('File') == filename), song_data)
                # Ensure cover art is included in the update for the UI
                if song_data.get('CoverArt') and song_data['CoverArt'] != 'REMOVE':
                    updated_song['CoverArt'] = song_data['CoverArt']
                elif song_data.get('CoverArt') == 'REMOVE':
                    updated_song['CoverArt'] = None
                safe_updated = json.dumps(updated_song)
                self.api._window.evaluate_js(f"if (typeof window.update_song_row_ui === 'function') window.update_song_row_ui({json.dumps(filename)}, {safe_updated});")
                
                next_queue = getattr(self.api.playback, 'next_songs', []) if hasattr(self.api, 'playback') else getattr(self.api, 'next_songs', [])
                self.api._window.evaluate_js(f"window.queue_songs = {json.dumps(next_queue)}; if (typeof window.update_queue_ui === 'function') window.update_queue_ui();")

            # Database update
            try:
                with sqlite3.connect(self.api.db_path) as conn:
                    table = "Podcasts" if is_podcast else "Songs"
                    conn.execute(f"UPDATE {table} SET title = ?, artist = ? WHERE file = ?",
                        (song_data.get('Title', ''), song_data.get('Artist', ''), filename))
            except Exception as e:
                print(f" [Python] DB error updating song: {e}")
            return True
        except Exception as e:
            print(f" [Python] Error updating metadata: {str(e)}")
            return False

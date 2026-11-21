"""Move audio files between the Songs and Podcasts libraries.

The move is a library operation, not a copy: the file changes folders and the
source catalog rows are removed with the same tombstones a delete would leave,
so a later sync cannot resurrect a song as both a song and an episode. Tag
links, chapters, lyrics and play history belong to one library only and are
dropped from the source side by design.
"""

from __future__ import annotations

import os
import shutil
import sqlite3


class LibraryMoveError(Exception):
    """User-facing move failure."""


def _basename(filename):
    text = str(filename or "").strip().replace("\\", "/")
    name = text.rsplit("/", 1)[-1]
    if not name or name in (".", ".."):
        raise LibraryMoveError("A valid audio filename is required.")
    return name


def _unique_name(directory, filename):
    """Return a collision-safe filename inside *directory*."""
    stem, ext = os.path.splitext(filename)
    candidate = filename
    counter = 1
    while os.path.exists(os.path.join(directory, candidate)):
        counter += 1
        candidate = f"{stem} (moved {counter}){ext}"
    return candidate


def _source_row(conn, table, filename):
    if table == "Songs":
        row = conn.execute(
            "SELECT downloaded_link, title, date_download, artist, loudness_db "
            "FROM Songs WHERE file = ?",
            (filename,),
        ).fetchone()
        if not row:
            return {}
        return {
            "downloaded_link": row[0],
            "title": row[1],
            "date_download": row[2],
            "artist": row[3],
            "loudness_db": row[4],
        }
    row = conn.execute(
        "SELECT downloaded_link, title, date_download, artist "
        "FROM Podcasts WHERE file = ?",
        (filename,),
    ).fetchone()
    if not row:
        return {}
    return {
        "downloaded_link": row[0],
        "title": row[1],
        "date_download": row[2],
        "artist": row[3],
        "loudness_db": None,
    }


def _insert_destination(conn, table, filename, row, metadata):
    title = (metadata or {}).get("Title") or row.get("title") or os.path.splitext(filename)[0]
    artist = (metadata or {}).get("Artist") or row.get("artist") or ""
    if str(artist).strip().lower() == "unknown":
        artist = ""
    values = [filename, row.get("downloaded_link"), title, row.get("date_download"), artist]
    columns = "file, downloaded_link, title, date_download, artist"
    placeholders = "?, ?, ?, COALESCE(?, CURRENT_TIMESTAMP), ?"
    if table == "Songs":
        columns += ", loudness_db"
        placeholders += ", ?"
        values.append(row.get("loudness_db"))
    conn.execute(
        f"INSERT OR REPLACE INTO {table} ({columns}) VALUES ({placeholders})",
        values,
    )


def _delete_source(conn, filename, to_podcast):
    tombstones = []
    if to_podcast:
        conn.execute("DELETE FROM Song_Playlist WHERE song_file = ?", (filename,))
        conn.execute("DELETE FROM Lyrics WHERE song_file = ?", (filename,))
        conn.execute("DELETE FROM Music_History WHERE song_file = ?", (filename,))
        conn.execute("DELETE FROM Songs WHERE file = ?", (filename,))
        tombstones.extend(
            (table, filename) for table in ("songs", "lyrics", "music_history")
        )
        return tombstones
    chapter_prefix = filename + "::chapter:"
    chapter_keys = [
        row[0] for row in conn.execute(
            "SELECT song_file FROM Lyrics WHERE song_file LIKE ?",
            (chapter_prefix + "%",),
        ).fetchall()
    ]
    conn.execute("DELETE FROM Lyrics WHERE song_file = ? OR song_file LIKE ?",
                 (filename, chapter_prefix + "%"))
    conn.execute("DELETE FROM Podcast_Tag_Links WHERE podcast_file = ?", (filename,))
    conn.execute("DELETE FROM Podcast_Chapters WHERE podcast_file = ?", (filename,))
    conn.execute("DELETE FROM Podcasts WHERE file = ?", (filename,))
    tombstones.extend((table, filename) for table in ("podcasts", "podcast_chapters"))
    tombstones.extend(("lyrics", key) for key in chapter_keys)
    return tombstones


def move_between_libraries(
    db_path,
    source_dir,
    dest_dir,
    filename,
    to_podcast,
    read_metadata=None,
):
    """Move one file and migrate its catalog rows between libraries.

    ``to_podcast`` selects the destination. ``read_metadata`` may be a callable
    ``(path, filename) -> dict`` used to ingest the destination title/artist;
    when it is absent the source row is carried over.
    """
    filename = _basename(filename)
    source_dir = os.path.abspath(source_dir)
    dest_dir = os.path.abspath(dest_dir)
    if os.path.realpath(source_dir) == os.path.realpath(dest_dir):
        raise LibraryMoveError("Songs and Podcasts use the same folder.")
    source_path = os.path.join(source_dir, filename)
    if not os.path.isfile(source_path):
        raise LibraryMoveError(f"{filename} is not in the source library.")

    try:
        os.makedirs(dest_dir, exist_ok=True)
    except OSError as exc:
        raise LibraryMoveError(f"Could not create the destination folder: {exc}")

    target_name = _unique_name(dest_dir, filename)
    target_path = os.path.join(dest_dir, target_name)
    moved = False
    source_table = "Songs" if to_podcast else "Podcasts"
    dest_table = "Podcasts" if to_podcast else "Songs"
    try:
        shutil.move(source_path, target_path)
        moved = True

        metadata = {}
        if read_metadata:
            try:
                metadata = read_metadata(target_path, target_name) or {}
            except Exception:
                metadata = {}

        conn = sqlite3.connect(db_path)
        try:
            row = _source_row(conn, source_table, filename)
            tombstones = _delete_source(conn, filename, to_podcast)
            _insert_destination(conn, dest_table, target_name, row, metadata)
            for table, key in tombstones:
                conn.execute(
                    "INSERT INTO Sync_Deletions (table_name, row_key) VALUES (?, ?)",
                    (table, key),
                )
            conn.commit()
        finally:
            conn.close()
        return {
            "ok": True,
            "oldFile": filename,
            "newFile": target_name,
            "sourceDir": source_dir,
            "destDir": dest_dir,
            "isPodcast": bool(to_podcast),
        }
    except Exception as exc:
        if moved:
            try:
                shutil.move(target_path, source_path)
            except Exception:
                pass
        if isinstance(exc, LibraryMoveError):
            raise
        raise LibraryMoveError(str(exc)) from exc

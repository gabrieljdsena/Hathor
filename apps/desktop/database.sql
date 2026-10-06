create table if not exists Songs(
    file varchar(255) PRIMARY KEY,
    downloaded_link varchar(255) null,
    title varchar(255) not null,
    date_download datetime default CURRENT_TIMESTAMP,
    artist varchar(255) null
);

create table if not exists Podcasts(
    file varchar(255) PRIMARY KEY,
    downloaded_link varchar(255) null,
    title varchar(255) not null,
    date_download datetime default CURRENT_TIMESTAMP,
    artist varchar(255) null
);

create table if not exists Playlists(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title varchar(255) not null,
    description text,
    thumbnail BLOB null
);

create table if not exists Song_Playlist(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    song_file varchar(255) not null,
    playlist_id bigint not null,
    date_added datetime default CURRENT_TIMESTAMP,
    FOREIGN KEY (song_file) REFERENCES Songs(file),
    FOREIGN KEY (playlist_id) REFERENCES Playlists(id)
);

create table if not exists Settings(
    id int not null PRIMARY KEY,
    current_song varchar(255),
    current_playlist INTEGER NULL,
    current_volume float,
    limit_downloads int,
    standardize_volume boolean,
    current_tab varchar(255),
    window_width int,
    window_height int,
    background_path varchar(255),
    songs_path varchar(255),
    podcasts_path varchar(255),
    browser varchar(50),
    queue_songs text,
    custom_queue integer DEFAULT 0,
    queue_source text,
    crossfade_enabled integer DEFAULT 0,
    crossfade_seconds real DEFAULT 5,
    chapter_skip integer DEFAULT 0,
    FOREIGN KEY (current_song) REFERENCES Songs(file)
);

create table if not exists Download_Queue(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    qid uuid not null unique,
    url varchar(1000),
    title varchar(255),
    artist varchar(255),
    status varchar(50) DEFAULT 'queued',
    progress real DEFAULT 0,
    error text,
    filename varchar(255),
    is_podcast integer DEFAULT 0,
    created_at datetime default CURRENT_TIMESTAMP,
    updated_at datetime default CURRENT_TIMESTAMP
);

create table if not exists Lyrics(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    song_file varchar(255) not null,
    lyrics text,
    offset_ms integer not null default 0,
    FOREIGN KEY (song_file) REFERENCES Songs(file)
);

create table if not exists Music_History(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    song_file varchar(255) not null,
    date_played datetime default CURRENT_TIMESTAMP,
    FOREIGN KEY (song_file) REFERENCES Songs(file)
);

create table if not exists Playlist_History(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    playlist_id bigint not null,
    date_played datetime default CURRENT_TIMESTAMP,
    FOREIGN KEY (playlist_id) REFERENCES Playlists(id)
);

create table if not exists Sync_Deletions(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    table_name varchar(255) not null,
    row_key varchar(255) not null,
    deleted_at datetime default CURRENT_TIMESTAMP
);

create table if not exists Daily_Mix(
    mix_date varchar(10) PRIMARY KEY,
    song_files text not null,
    created_at datetime default CURRENT_TIMESTAMP
);

-- Podcast tags (same shape as Playlists + Song_Playlist).
create table if not exists Podcast_Tags(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name varchar(255) not null unique
);

create table if not exists Podcast_Tag_Links(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    podcast_file varchar(255) not null,
    tag_id bigint not null,
    FOREIGN KEY (podcast_file) REFERENCES Podcasts(file),
    FOREIGN KEY (tag_id) REFERENCES Podcast_Tags(id)
);

create unique index if not exists idx_podcast_tag_links_pair
    on Podcast_Tag_Links(podcast_file, tag_id);

-- Chapter marks inside a podcast episode (web parity): start offset in
-- seconds, display name, optional end offset (null = open-ended).
create table if not exists Podcast_Chapters(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    podcast_file varchar(255) not null,
    name varchar(255) not null,
    start_secs real not null,
    end_secs real null,
    FOREIGN KEY (podcast_file) REFERENCES Podcasts(file)
);

create index if not exists idx_podcast_chapters_file
    on Podcast_Chapters(podcast_file, start_secs);


INSERT OR IGNORE INTO Settings(id,current_song, limit_downloads, current_playlist, current_volume, standardize_volume, current_tab, window_width, window_height, background_path, songs_path, browser)
values (1,null,null,null,null,null,null,null,null,null,null,null)
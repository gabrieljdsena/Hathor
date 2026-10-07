import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, type Song } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable from '../components/LibraryTable'
import { ControlButton, PlayCircleButton } from '../components/ui/buttons'
import ViewHeader from '../components/ui/ViewHeader'
import SearchInput, { useDebouncedValue } from '../components/ui/SearchInput'
import { fuzzyMatch } from '../utils/fuzzy'
import { EmptyState, LoadingState } from '../components/ui/states'

function shuffleList(list: Song[]): Song[] {
  const out = [...list]
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[out[i], out[j]] = [out[j], out[i]]
  }
  return out
}

// Typo-tolerant client filter for the artists/albums name lists (same
// fuzzy match as the song tables, debounced like every search box).
function useNameFilter(names: readonly string[] | undefined) {
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const filtering = debounced.trim().length > 0
  const visible = useMemo(
    () => (names ?? []).filter((n) => !filtering || fuzzyMatch(n, debounced)),
    [names, debounced, filtering],
  )
  return { search, setSearch, visible, filtering }
}

// Detail-page hero artwork (iTunes 600x600 via the API, exact-match
// preference server-side): large squared tile, music-note fallback while
// loading or when iTunes has nothing.
function HeroArt({ url, alt }: { url: string | null | undefined; alt: string }) {
  if (!url) {
    return (
      <div className="w-28 h-28 sm:w-36 sm:h-36 flex-shrink-0 flex items-center justify-center bg-white/5 border border-white/10 rounded-2xl">
        <svg viewBox="0 0 24 24" fill="currentColor" className="w-12 h-12 text-zinc-600">
          <path d="M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3" />
        </svg>
      </div>
    )
  }
  return (
    <img
      src={url}
      alt={alt}
      loading="lazy"
      className="w-28 h-28 sm:w-36 sm:h-36 flex-shrink-0 object-cover border border-white/10 rounded-2xl"
    />
  )
}

export function Artists() {
  const { data: artists, isLoading } = useQuery({ queryKey: ['artists'], queryFn: api.artists })
  const { search, setSearch, visible, filtering } = useNameFilter(artists)
  return (
    <div className="max-w-5xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24 flex flex-col gap-6">
      <ViewHeader
        icon="users"
        title="Artists"
        subtitle={`${artists?.length ?? 0} artists`}
        actions={
          <SearchInput value={search} onChange={setSearch} placeholder="Search artists..." id="artists_txt_search" />
        }
      />
      {isLoading ? (
        <LoadingState label="Loading artists…" />
      ) : (artists ?? []).length === 0 ? (
        <EmptyState title="No artists yet" hint="Artists appear once your library has tagged music." />
      ) : filtering && visible.length === 0 ? (
        <EmptyState title="No artists match" hint="Try different spelling." />
      ) : (
        <div className="flex flex-col gap-1">
          {visible.map((name) => (
            <Link
              key={name}
              to={`/artists/${encodeURIComponent(name)}`}
              className="p-3 px-4 rounded-xl hover:bg-white/5 transition-all text-sm font-medium text-zinc-100 hover:text-orange-400 truncate"
            >
              {name}
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}

export function ArtistDetail() {
  const { name } = useParams()
  // useParams already decodes percent-encoding; a second
  // decodeURIComponent would corrupt names and throw on literals like '%'.
  const artist = name ?? ''
  const playSong = usePlayer((s) => s.playSong)
  const { data: songs, isLoading } = useQuery({
    queryKey: ['artist-songs', artist],
    queryFn: () => api.artistSongs(artist),
    enabled: artist.length > 0,
  })
  const playAll = (shuffled: boolean) => {
    const list = [...(songs ?? [])]
    if (list.length === 0) return
    const ordered = shuffled ? shuffleList(list) : list
    void playSong(ordered[0], ordered, { type: 'artist', id: artist })
  }
  const { data: art } = useQuery({
    queryKey: ['artist-image', artist],
    queryFn: () => api.artistImage(artist),
    enabled: artist.length > 0,
    staleTime: 1000 * 60 * 60,
  })
  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6 flex items-center gap-4">
        <HeroArt url={art?.url} alt={artist} />
        <ViewHeader icon="users" compact title={artist} subtitle={`${songs?.length ?? 0} songs`} />
        {(songs ?? []).length > 0 && (
          <div className="ml-auto flex items-center gap-2 flex-shrink-0">
            <ControlButton icon="shuffle" title="Shuffle play" onClick={() => playAll(true)} />
            <PlayCircleButton title="Play all" onClick={() => playAll(false)} />
          </div>
        )}
      </div>
      <LibraryTable
        songs={songs}
        isLoading={isLoading}
        loadingLabel="Loading artist…"
        emptyTitle="No songs by this artist"
        source={{ type: 'artist', id: artist }}
        searchPlaceholder="Search artist..."
      />
    </div>
  )
}

export function Albums() {
  const { data: albums, isLoading } = useQuery({ queryKey: ['albums'], queryFn: api.albums })
  const { search, setSearch, visible, filtering } = useNameFilter(albums)
  return (
    <div className="max-w-5xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24 flex flex-col gap-6">
      <ViewHeader
        icon="musicNote"
        title="Albums"
        subtitle={`${albums?.length ?? 0} albums`}
        actions={
          <SearchInput value={search} onChange={setSearch} placeholder="Search albums..." id="albums_txt_search" />
        }
      />
      {isLoading ? (
        <LoadingState label="Loading albums…" />
      ) : (albums ?? []).length === 0 ? (
        <EmptyState title="No albums yet" hint="Albums appear once your library has tagged music." />
      ) : filtering && visible.length === 0 ? (
        <EmptyState title="No albums match" hint="Try different spelling." />
      ) : (
        <div className="flex flex-col gap-1">
          {visible.map((title) => (
            <Link
              key={title}
              to={`/albums/${encodeURIComponent(title)}`}
              className="p-3 px-4 rounded-xl hover:bg-white/5 transition-all text-sm font-medium text-zinc-100 hover:text-orange-400 truncate"
            >
              {title}
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}

export function AlbumDetail() {
  const { title } = useParams()
  // useParams already decodes percent-encoding (see ArtistDetail).
  const album = title ?? ''
  const playSong = usePlayer((s) => s.playSong)
  const { data: songs, isLoading } = useQuery({
    queryKey: ['album-songs', album],
    queryFn: () => api.albumSongs(album),
    enabled: album.length > 0,
  })
  const playAll = (shuffled: boolean) => {
    const list = [...(songs ?? [])]
    if (list.length === 0) return
    const ordered = shuffled ? shuffleList(list) : list
    void playSong(ordered[0], ordered, { type: 'album', id: album })
  }
  // Disambiguate same-name albums when every track agrees on the artist.
  const artists = [...new Set((songs ?? []).map((s) => s.artist).filter((a) => a && a !== 'Unknown'))]
  const albumArtist = artists.length === 1 ? artists[0] : undefined
  const { data: art } = useQuery({
    queryKey: ['album-image', album, albumArtist ?? ''],
    queryFn: () => api.albumImage(album, albumArtist),
    enabled: album.length > 0,
    staleTime: 1000 * 60 * 60,
  })
  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6 flex items-center gap-4">
        <HeroArt url={art?.url} alt={album} />
        <ViewHeader icon="musicNote" compact title={album} subtitle={`${songs?.length ?? 0} songs`} />
        {(songs ?? []).length > 0 && (
          <div className="ml-auto flex items-center gap-2 flex-shrink-0">
            <ControlButton icon="shuffle" title="Shuffle play" onClick={() => playAll(true)} />
            <PlayCircleButton title="Play all" onClick={() => playAll(false)} />
          </div>
        )}
      </div>
      <LibraryTable
        songs={songs}
        isLoading={isLoading}
        loadingLabel="Loading album…"
        emptyTitle="No songs on this album"
        source={{ type: 'album', id: album }}
        searchPlaceholder="Search album..."
      />
    </div>
  )
}

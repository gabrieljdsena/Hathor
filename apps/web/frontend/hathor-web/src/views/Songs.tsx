import { useQuery } from '@tanstack/react-query'
import { api, type Song } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable from '../components/LibraryTable'
import { PlayCircleButton } from '../components/ui/buttons'
import ViewHeader from '../components/ui/ViewHeader'

// Library table cloned from Music Player/ui/views/all_songs.html.
export default function Songs() {
  const playSong = usePlayer((s) => s.playSong)

  // Whole-library fetch (desktop get_all_songs has no paging): size the
  // page from the count so libraries bigger than any fixed pageSize work.
  const { data: total } = useQuery({ queryKey: ['songs', 'count'], queryFn: () => api.songCount() })
  const {
    data: songs,
    isLoading,
    isError,
  } = useQuery({
    queryKey: ['songs', 'all', total],
    queryFn: () => api.songs({ pageSize: Math.max(total ?? 100, 1) }),
    enabled: total !== undefined,
  })

  const playAll = (list: Song[]) => {
    const first = list[0]
    if (first) void playSong(first, list, { type: 'all_songs', id: null })
  }

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        <ViewHeader
          icon="musicNote"
          compact
          title="All Songs"
          subtitle={isLoading ? 'Loading…' : `${songs?.length ?? 0} songs`}
          actions={<PlayCircleButton title="Play all" onClick={() => songs && playAll(songs)} />}
        />
      </div>

      <LibraryTable
        songs={songs}
        isLoading={isLoading}
        loadingLabel="Loading your library…"
        emptyTitle={isError ? 'Could not load songs' : 'No songs yet'}
        emptyHint={isError ? 'Check your connection and try again.' : 'Download some music to build your library.'}
        source={{ type: 'all_songs', id: null }}
        searchId="all_txt_search"
      />
    </div>
  )
}

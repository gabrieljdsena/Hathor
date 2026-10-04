import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable from '../components/LibraryTable'
import { ControlButton, PlayCircleButton } from '../components/ui/buttons'
import { EmptyState, LoadingState } from '../components/ui/states'
import ViewHeader from '../components/ui/ViewHeader'

// Daily Mix view cloned from ui/views/daily_mix.html:
// date badge + play/shuffle + searchable table. Mix id = mix date
// (queue_source {type:daily_mix, id:date}).
export default function Mix() {
  const playSong = usePlayer((s) => s.playSong)
  const queryClient = useQueryClient()
  const { data: mix, isLoading } = useQuery({ queryKey: ['daily-mix'], queryFn: api.dailyMix })

  const dateLabel = (date: string) => {
    try {
      return new Date(`${date}T00:00:00`).toLocaleDateString(undefined, {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
      })
    } catch {
      return date
    }
  }

  const playAll = (shuffled: boolean) => {
    const list = [...(mix?.songs ?? [])]
    if (list.length === 0) return
    if (shuffled) {
      for (let i = list.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1))
        ;[list[i], list[j]] = [list[j], list[i]]
      }
    }
    const first = list[0]
    void playSong(first, list, { type: 'daily_mix', id: mix?.date ?? null })
  }

  const regenerate = () =>
    void api.regenerateMix().then((m) => {
      queryClient.setQueryData(['daily-mix'], m)
    })

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        <div className="flex items-center gap-4">
          <ViewHeader
            icon="sparkles"
            compact
            title="Daily Mix"
            subtitle={
              isLoading
                ? 'Generating…'
                : `${mix?.songs.length ?? 0} songs${mix?.date ? ` · ${dateLabel(mix.date)}` : ''}${
                    mix && !mix.cached ? ' · fresh' : ''
                  }`
            }
          />
          <div className="ml-auto flex items-center gap-2">
            {mix && mix.songs.length > 0 && (
              <button
                onClick={regenerate}
                title="Regenerate mix"
                className="text-xs text-zinc-500 hover:text-orange-400 uppercase tracking-widest transition-colors cursor-pointer"
              >
                Regenerate
              </button>
            )}
            <ControlButton icon="shuffle" title="Shuffle play" onClick={() => playAll(true)} />
            <PlayCircleButton title="Play mix" onClick={() => playAll(false)} />
          </div>
        </div>
      </div>

      {isLoading ? (
        <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pt-6">
          <LoadingState label="Mixing today's songs…" />
        </div>
      ) : (mix?.songs ?? []).length === 0 ? (
        <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pt-6">
          <EmptyState
            title="Nothing mixed yet"
            hint="Play some songs and download music — your mix needs listening history and library songs to work with."
          />
        </div>
      ) : (
        <LibraryTable
          songs={mix?.songs}
          isLoading={false}
          source={{ type: 'daily_mix', id: mix?.date ?? null }}
          searchPlaceholder="Search mix..."
          searchId="daily_txt_search"
        />
      )}
    </div>
  )
}

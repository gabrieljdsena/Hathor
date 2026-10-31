import { useQuery } from '@tanstack/react-query'
import { api, deviceId, type PlaybackSpot } from '../../api/client'

// Resume across devices: another browser's paused spot, polled lightly
// (60s). The server excludes this browser's own device id, so a pause here
// never echoes back. Rendered via ResumeBento (dark + orange); tapping
// restores the foreign queue at its position — explicit only, nothing
// auto-plays.
export function useResumeSpot(): PlaybackSpot | null {
  let id: string | null = null
  try {
    id = deviceId()
  } catch {
    id = null
  }
  const { data } = useQuery({
    queryKey: ['resume-state', id],
    queryFn: () => api.latestPlayback(id),
    refetchInterval: 60_000,
    staleTime: 30_000,
    retry: false,
  })
  return data ?? null
}

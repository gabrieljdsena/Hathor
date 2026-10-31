import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type Song } from '../api/client'
import {
  recordEndedPlayback,
  resetPlaybackTelemetryForTests,
  startPlaybackTelemetry,
} from './telemetry'

function song(file: string): Song {
  return {
    file,
    title: file,
    artist: 'Artist',
    album: 'Album',
    year: '2020',
    duration: 200,
    coverArt: null,
    dateDownload: null,
  }
}

describe('playback telemetry', () => {
  let state: { currentSong: Song | null; isPlaying: boolean }
  let position: { position: number; duration: number }
  let report: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    vi.useFakeTimers()
    resetPlaybackTelemetryForTests()
    state = { currentSong: song('a.mp3'), isPlaying: true }
    position = { position: 10, duration: 200 }
    report = vi
      .spyOn(api, 'recordPlaybackEvent')
      .mockResolvedValue(undefined as never)
  })

  afterEach(() => {
    resetPlaybackTelemetryForTests()
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('reports a partial track change (not skipped past halfway)', () => {
    const stop = startPlaybackTelemetry(() => state, () => position, 1000)
    position.position = 120
    vi.advanceTimersByTime(1000)

    state = { currentSong: song('b.mp3'), isPlaying: true }
    vi.advanceTimersByTime(1000)

    expect(report).toHaveBeenCalledWith(
      expect.objectContaining({
        file: 'a.mp3',
        completed: false,
        skipped: false,
        positionSec: 120,
      }),
      false,
    )
    stop()
  })

  it('reports a skip when the next track starts under halfway', () => {
    const stop = startPlaybackTelemetry(() => state, () => position, 1000)
    position.position = 20
    vi.advanceTimersByTime(1000)

    state = { currentSong: song('b.mp3'), isPlaying: true }
    vi.advanceTimersByTime(1000)

    expect(report).toHaveBeenCalledWith(
      expect.objectContaining({
        file: 'a.mp3',
        completed: false,
        skipped: true,
      }),
      false,
    )
    stop()
  })

  it('reports an explicit ended as completed', () => {
    const stop = startPlaybackTelemetry(() => state, () => position, 1000)
    recordEndedPlayback(200, 200)

    expect(report).toHaveBeenCalledWith(
      expect.objectContaining({
        file: 'a.mp3',
        completed: true,
        skipped: false,
        positionSec: 200,
      }),
      false,
    )
    stop()
  })

  it('flushes near-end state on pagehide with keepalive', () => {
    const stop = startPlaybackTelemetry(() => state, () => position, 1000)
    position.position = 198
    vi.advanceTimersByTime(1000)

    window.dispatchEvent(new Event('pagehide'))

    expect(report).toHaveBeenCalledWith(
      expect.objectContaining({
        file: 'a.mp3',
        completed: true,
        skipped: false,
      }),
      true,
    )
    stop()
  })

  it('ignores podcasts', () => {
    state = { currentSong: { ...song('pod.mp3'), isPodcast: true }, isPlaying: true }
    const stop = startPlaybackTelemetry(() => state, () => position, 1000)
    position.position = 10
    vi.advanceTimersByTime(1000)
    state = { currentSong: null, isPlaying: false }
    vi.advanceTimersByTime(1000)

    expect(report).not.toHaveBeenCalled()
    stop()
  })
})

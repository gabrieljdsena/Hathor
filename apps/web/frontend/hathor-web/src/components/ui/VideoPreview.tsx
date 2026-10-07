import { useEffect, useState } from 'react'
import { api } from '../../api/client'

// Embed-first YouTube preview with a direct-audio fallback. The backend
// probes oEmbed: embeddable videos play in the iframe as before;
// embedding-disabled ones (error 153 / "Watch on YouTube") play the
// resolved audio stream in an <audio> element instead. When neither works,
// a watch link is shown — some videos (age-restricted, deleted, private)
// cannot preview at all.
export default function VideoPreview({ id, title }: { id: string; title?: string }) {
  const [probe, setProbe] = useState<{ embeddable: boolean; audioUrl: string | null } | null>(null)

  useEffect(() => {
    let live = true
    setProbe(null)
    api
      .youtubePreview(id)
      .then((p) => {
        if (live) setProbe(p)
      })
      .catch(() => {
        // Probe failed (offline?): assume embeddable so the iframe — which
        // needs no backend round trip — still gets its chance.
        if (live) setProbe({ embeddable: true, audioUrl: null })
      })
    return () => {
      live = false
    }
  }, [id])

  if (probe === null) {
    return <p className="text-zinc-500 text-sm animate-pulse py-8 text-center">Finding preview…</p>
  }

  if (probe.embeddable) {
    return (
      <div className="relative w-full" style={{ paddingBottom: '56.25%' }}>
        <iframe
          className="absolute top-0 left-0 w-full h-full rounded-lg"
          src={`https://www.youtube.com/embed/${id}?autoplay=1`}
          title={title ?? 'YouTube video player'}
          allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
          allowFullScreen
        />
      </div>
    )
  }

  const watchUrl = `https://www.youtube.com/watch?v=${id}`
  return (
    <div className="flex flex-col gap-3 py-2">
      <p className="text-xs text-zinc-500">
        This video can't be embedded here — playing audio directly instead.
      </p>
      {probe.audioUrl ? (
        <audio className="w-full" controls autoPlay src={probe.audioUrl} preload="none" />
      ) : (
        <p className="text-sm text-zinc-400">No preview available for this video.</p>
      )}
      <a
        className="text-sm text-orange-400 hover:text-orange-300 hover:underline underline-offset-2 w-fit"
        href={watchUrl}
        target="_blank"
        rel="noreferrer"
      >
        Watch on YouTube ↗
      </a>
    </div>
  )
}

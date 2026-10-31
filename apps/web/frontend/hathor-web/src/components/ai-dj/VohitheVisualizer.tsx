import { useEffect, useRef } from 'react'
import { useAudioSpectrum } from '../../hooks/useAudioSpectrum'

const RADIAL_BARS = 72
const LINE_BARS = 64

function sample(data: Uint8Array, index: number, count: number): number {
  const ratio = count <= 1 ? 0 : index / (count - 1)
  const bin = Math.min(data.length - 1, 2 + Math.floor(Math.pow(ratio, 1.35) * 104))
  return data[bin] / 255
}

function setupCanvas(canvas: HTMLCanvasElement) {
  const rect = canvas.getBoundingClientRect()
  if (rect.width < 2 || rect.height < 2) return null
  const ctx = canvas.getContext('2d')
  if (!ctx) return null
  const dpr = Math.min(2, window.devicePixelRatio || 1)
  canvas.width = Math.round(rect.width * dpr)
  canvas.height = Math.round(rect.height * dpr)
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  return { ctx, width: rect.width, height: rect.height }
}

export function VohitheVisualizer({
  className = '',
  active = false,
  voice = false,
}: {
  className?: string
  active?: boolean
  voice?: boolean
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const { read } = useAudioSpectrum(256)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false
    const levels = new Float32Array(RADIAL_BARS)
    const spectrum = new Uint8Array(256)
    let raf = 0
    let width = 0
    let height = 0
    let visible = !document.hidden

    const resize = () => {
      const size = setupCanvas(canvas)
      if (!size) return
      width = size.width
      height = size.height
    }

    const draw = (time: number) => {
      if (width < 2 || height < 2) return
      ctx.clearRect(0, 0, width, height)
      const cx = width / 2
      const cy = height / 2
      const size = Math.min(width, height)
      const base = size * 0.335
      const max = size * 0.115
      const hasAudio = read(spectrum)
      const accent = voice ? '251, 146, 60' : '249, 115, 22'

      ctx.beginPath()
      ctx.arc(cx, cy, base, 0, Math.PI * 2)
      ctx.strokeStyle = 'rgba(255,255,255,0.07)'
      ctx.lineWidth = 1
      ctx.stroke()

      ctx.beginPath()
      ctx.arc(cx, cy, base + max + 10, 0, Math.PI * 2)
      ctx.strokeStyle = 'rgba(255,255,255,0.035)'
      ctx.lineWidth = 1
      ctx.stroke()

      for (let i = 0; i < RADIAL_BARS; i += 1) {
        const target = hasAudio ? sample(spectrum, i, RADIAL_BARS) : 0
        const idle = 0.025 + (active ? 0.018 : 0.008) * Math.sin(time / 1200 + i * 0.55)
        levels[i] = Math.max(target, idle, levels[i] * 0.86)
        const magnitude = Math.min(1, levels[i])
        const angle = (i / RADIAL_BARS) * Math.PI * 2 - Math.PI / 2
        const start = base + 5
        const end = start + 4 + magnitude * max
        ctx.beginPath()
        ctx.moveTo(cx + Math.cos(angle) * start, cy + Math.sin(angle) * start)
        ctx.lineTo(cx + Math.cos(angle) * end, cy + Math.sin(angle) * end)
        ctx.strokeStyle = `rgba(${accent},${0.12 + magnitude * 0.72})`
        ctx.lineWidth = i % 6 === 0 ? 2.2 : 1.4
        ctx.stroke()
      }
    }

    const loop = (time: number) => {
      if (!visible) return
      draw(time)
      raf = requestAnimationFrame(loop)
    }

    const onVisibility = () => {
      visible = !document.hidden
      if (visible && !reduced) {
        cancelAnimationFrame(raf)
        raf = requestAnimationFrame(loop)
      }
    }

    resize()
    if (reduced) draw(0)
    else raf = requestAnimationFrame(loop)

    const observer = new ResizeObserver(() => {
      resize()
      if (reduced) draw(0)
    })
    observer.observe(canvas)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      cancelAnimationFrame(raf)
      observer.disconnect()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [active, read, voice])

  return <canvas ref={canvasRef} aria-hidden="true" className={className} />
}

export function AudioWaveform({
  className = '',
  active = false,
}: {
  className?: string
  active?: boolean
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const { read } = useAudioSpectrum(256)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    const levels = new Float32Array(LINE_BARS)
    const spectrum = new Uint8Array(256)
    let raf = 0
    let width = 0
    let height = 0
    let visible = !document.hidden

    const resize = () => {
      const size = setupCanvas(canvas)
      if (!size) return
      width = size.width
      height = size.height
    }

    const draw = (time: number) => {
      if (width < 2 || height < 2) return
      ctx.clearRect(0, 0, width, height)
      const hasAudio = read(spectrum)
      const mid = height / 2
      const gap = 2
      const barWidth = Math.max(1, (width - gap * (LINE_BARS - 1)) / LINE_BARS)
      for (let i = 0; i < LINE_BARS; i += 1) {
        const target = hasAudio ? sample(spectrum, i, LINE_BARS) : 0
        const idle = 0.04 + (active ? 0.02 : 0.01) * Math.sin(time / 900 + i * 0.5)
        levels[i] = Math.max(target, idle, levels[i] * 0.82)
        const barHeight = 2 + Math.min(1, levels[i]) * (height - 4)
        const x = i * (barWidth + gap)
        ctx.fillStyle = `rgba(249,115,22,${0.12 + Math.min(1, levels[i]) * 0.7})`
        ctx.fillRect(x, mid - barHeight / 2, barWidth, barHeight)
      }
    }

    const loop = (time: number) => {
      if (!visible) return
      draw(time)
      raf = requestAnimationFrame(loop)
    }

    const onVisibility = () => {
      visible = !document.hidden
      if (visible) {
        cancelAnimationFrame(raf)
        raf = requestAnimationFrame(loop)
      }
    }

    resize()
    raf = requestAnimationFrame(loop)
    const observer = new ResizeObserver(resize)
    observer.observe(canvas)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      cancelAnimationFrame(raf)
      observer.disconnect()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [active, read])

  return <canvas ref={canvasRef} aria-hidden="true" className={className} />
}

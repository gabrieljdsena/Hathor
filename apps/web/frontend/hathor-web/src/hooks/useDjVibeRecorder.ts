import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api/client'

const VAD_THRESHOLD = 0.008
const WINDOW_SECONDS = 6

function blockRms(block: Float32Array): number {
  let sum = 0
  for (let i = 0; i < block.length; i += 1) sum += block[i] * block[i]
  return Math.sqrt(sum / block.length)
}

function encodeWav(samples: Float32Array, sampleRate: number): string {
  const dataSize = samples.length * 2
  const buffer = new ArrayBuffer(44 + dataSize)
  const view = new DataView(buffer)
  const write = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i += 1) view.setUint8(offset + i, text.charCodeAt(i))
  }
  write(0, 'RIFF')
  view.setUint32(4, 36 + dataSize, true)
  write(8, 'WAVE')
  write(12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true)
  view.setUint16(22, 1, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true)
  view.setUint16(32, 2, true)
  view.setUint16(34, 16, true)
  write(36, 'data')
  view.setUint32(40, dataSize, true)
  for (let i = 0; i < samples.length; i += 1) {
    const clamped = Math.max(-1, Math.min(1, samples[i]))
    view.setInt16(44 + i * 2, clamped * 0x7fff, true)
  }
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (let i = 0; i < bytes.length; i += 1) binary += String.fromCharCode(bytes[i])
  return btoa(binary)
}

export function useDjVibeRecorder(onText: (text: string) => void) {
  const [recording, setRecording] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const active = useRef(false)
  const context = useRef<AudioContext | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const processor = useRef<ScriptProcessorNode | null>(null)
  const source = useRef<MediaStreamAudioSourceNode | null>(null)
  const sink = useRef<GainNode | null>(null)

  const stop = useCallback(() => {
    active.current = false
    setRecording(false)
    try {
      processor.current?.disconnect()
    } catch {
      /* ignore */
    }
    try {
      source.current?.disconnect()
    } catch {
      /* ignore */
    }
    try {
      sink.current?.disconnect()
    } catch {
      /* ignore */
    }
    processor.current = null
    source.current = null
    sink.current = null
    stream.current?.getTracks().forEach((track) => track.stop())
    stream.current = null
    void context.current?.close().catch(() => {})
    context.current = null
  }, [])

  useEffect(() => stop, [stop])

  const start = useCallback(async () => {
    setError(null)
    if (active.current) {
      stop()
      return
    }
    if (!navigator.mediaDevices?.getUserMedia) {
      setError('Microphone is not available in this browser.')
      return
    }
    try {
      const media = await navigator.mediaDevices.getUserMedia({ audio: true })
      const audioContext = new AudioContext()
      const micSource = audioContext.createMediaStreamSource(media)
      const proc = audioContext.createScriptProcessor(4096, 1, 1)
      const silent = audioContext.createGain()
      silent.gain.value = 0
      micSource.connect(proc)
      proc.connect(silent)
      silent.connect(audioContext.destination)
      stream.current = media
      context.current = audioContext
      source.current = micSource
      processor.current = proc
      sink.current = silent
      active.current = true
      setRecording(true)
      const sampleRate = audioContext.sampleRate
      let chunks: Float32Array[] = []
      let total = 0
      let peak = 0
      proc.onaudioprocess = (event) => {
        if (!active.current) return
        const chunk = new Float32Array(event.inputBuffer.getChannelData(0))
        peak = Math.max(peak, blockRms(chunk))
        chunks.push(chunk)
        total += chunk.length
        if (total < sampleRate * WINDOW_SECONDS) return
        const merged = new Float32Array(total)
        let offset = 0
        for (const part of chunks) {
          merged.set(part, offset)
          offset += part.length
        }
        const hadSpeech = peak >= VAD_THRESHOLD
        chunks = []
        total = 0
        peak = 0
        if (!hadSpeech) return
        void api
          .aiDjTranscribe(encodeWav(merged, sampleRate), navigator.language)
          .then((result) => {
            if (result.text) onText(result.text)
          })
          .catch(() => {
            setError('Could not transcribe that. Try typing instead.')
          })
      }
    } catch {
      setError('Microphone permission was denied.')
      stop()
    }
  }, [onText, stop])

  return { recording, error, start, stop }
}

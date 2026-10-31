import { useCallback, useRef } from 'react'
import { engine } from '../audio/engine'

export function useAudioSpectrum(bins = 256) {
  const data = useRef(new Uint8Array(bins))
  const read = useCallback((target?: Uint8Array) => engine.getSpectrum(target ?? data.current), [])
  return { data: data.current, read }
}

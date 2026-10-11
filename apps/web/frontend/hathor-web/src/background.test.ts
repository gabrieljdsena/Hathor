import { describe, expect, it, vi } from 'vitest'
import {
  MAX_BACKGROUND_BYTES,
  uploadBackgroundFile,
  validateBackgroundFile,
} from './views/Settings'

const f = (type: string, size: number) => ({ type, size }) as File

describe('validateBackgroundFile', () => {
  it('accepts a normal jpeg under the cap', () => {
    expect(validateBackgroundFile(f('image/jpeg', 1024))).toBeNull()
  })

  it('accepts exactly the 10 MB cap', () => {
    expect(validateBackgroundFile(f('image/png', MAX_BACKGROUND_BYTES))).toBeNull()
  })

  it('rejects over-limit files before any bytes move', () => {
    expect(validateBackgroundFile(f('image/jpeg', MAX_BACKGROUND_BYTES + 1))).toMatch(/under 10 MB/)
  })

  it('rejects types the server would refuse (heic, avif, video)', () => {
    for (const type of ['image/heic', 'image/avif', 'video/mp4', 'application/pdf']) {
      expect(validateBackgroundFile(f(type, 1024))).toMatch(/Only JPEG/)
    }
  })

  it('lets an empty type through for the server to decide', () => {
    expect(validateBackgroundFile(f('', 1024))).toBeNull()
  })
})

describe('uploadBackgroundFile', () => {
  it('never calls upload when validation fails', async () => {
    const upload = vi.fn(async () => 'Background updated.')
    await expect(uploadBackgroundFile(f('image/jpeg', MAX_BACKGROUND_BYTES + 1), upload)).rejects.toThrow(
      /under 10 MB/,
    )
    expect(upload).not.toHaveBeenCalled()
  })

  it('maps a killed connection (fetch TypeError) to a friendly message', async () => {
    const upload = vi.fn(async () => {
      throw new TypeError('NetworkError when attempting to fetch resource.')
    })
    await expect(uploadBackgroundFile(f('image/jpeg', 1024), upload)).rejects.toThrow(
      /check your connection/,
    )
  })

  it('renders server JSON errors readably', async () => {
    const upload = vi.fn(async () => {
      throw new Error('{"message":"Image must be non-empty and under 10 MB."}')
    })
    await expect(uploadBackgroundFile(f('image/jpeg', 1024), upload)).rejects.toThrow(
      'Image must be non-empty and under 10 MB.',
    )
  })

  it('passes success through untouched', async () => {
    const upload = vi.fn(async () => 'Background updated.')
    await expect(uploadBackgroundFile(f('image/webp', 2048), upload)).resolves.toBe(
      'Background updated.',
    )
  })
})

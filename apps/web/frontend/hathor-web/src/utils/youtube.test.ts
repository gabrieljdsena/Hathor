import { describe, expect, it } from 'vitest'
import { isBotCheckError } from './youtube'

describe('isBotCheckError', () => {
  it('matches the sign-in bot-check failure', () => {
    expect(
      isBotCheckError('ERROR: [youtube] Z3HV_tdUfUw: Sign in to confirm you’re not a bot.'),
    ).toBe(true)
  })

  it('matches cookie-guidance failures', () => {
    expect(isBotCheckError('Use --cookies-from-browser or --cookies')).toBe(true)
  })

  it('ignores unrelated failures and empty input', () => {
    expect(isBotCheckError('yt-dlp failed (exit 1): Unable to extract video')).toBe(false)
    expect(isBotCheckError(null)).toBe(false)
    expect(isBotCheckError('')).toBe(false)
  })
})

// YouTube bot-check signature: the failure text yt-dlp / YoutubeExplode
// surface when YouTube demands a sign-in ("Sign in to confirm you're not
// a bot"). Matched against job errors to point at the one-click
// bot-check protection instead of a bare retry.
const BOTCHECK = /sign in to confirm|not a bot|cookies-from-browser|--cookies\b/i

export function isBotCheckError(message: string | null | undefined): boolean {
  if (!message) return false
  return BOTCHECK.test(message)
}

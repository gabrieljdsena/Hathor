// Typo-tolerant search for every search box (library table, podcasts).
// Mirrors Hathor.Domain.Services.FuzzySearch: exact substring always
// matches, otherwise every query token must match a haystack token within
// a small Damerau-Levenshtein distance.

export function fuzzyMatch(haystack: string | null | undefined, query: string | null | undefined): boolean {
  if (!query || query.trim().length === 0) return true
  if (!haystack || haystack.trim().length === 0) return false
  const hay = normalize(haystack)
  const needle = normalize(query)
  if (hay.includes(needle)) return true
  const hayTokens = tokenize(hay)
  if (hayTokens.length === 0) return false
  return tokenize(needle).every((t) => matchesToken(hayTokens, t))
}

export function fuzzyFields(
  fields: Array<string | null | undefined>,
  query: string | null | undefined,
): boolean {
  if (!query || query.trim().length === 0) return true
  const parts = fields.filter((f): f is string => !!f && f.trim().length > 0)
  if (parts.length === 0) return false
  const joined = normalize(parts.join(' '))
  const needle = normalize(query)
  if (joined.includes(needle)) return true
  const hayTokens = parts.flatMap((p) => tokenize(normalize(p)))
  if (hayTokens.length === 0) return false
  return tokenize(needle).every((t) => matchesToken(hayTokens, t))
}

function matchesToken(hayTokens: string[], token: string): boolean {
  for (const hay of hayTokens) {
    if (hay.includes(token)) return true
    const max = maxDistance(token)
    if (Math.abs(hay.length - token.length) > max) continue
    if (damerauLevenshtein(hay, token, max) <= max) return true
  }
  return false
}

function maxDistance(token: string): number {
  return token.length < 4 ? 1 : Math.min(3, Math.floor(token.length / 3))
}

// Optimal string alignment: adjacent transpositions count as one edit.
function damerauLevenshtein(a: string, b: string, maxDist: number): number {
  if (a === b) return 0
  if (a.length === 0) return b.length
  if (b.length === 0) return a.length
  let prevPrev = Array.from({ length: b.length + 1 }, (_, j) => j)
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j)
  let cur = new Array<number>(b.length + 1)
  for (let i = 1; i <= a.length; i++) {
    cur[0] = i
    let rowMin = i
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1
      let best = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
        best = Math.min(best, prevPrev[j - 2] + 1)
      }
      cur[j] = best
      if (best < rowMin) rowMin = best
    }
    if (rowMin > maxDist) return maxDist + 1
    const tmp = prevPrev
    prevPrev = prev
    prev = cur
    cur = tmp
  }
  return prev[b.length]
}

function normalize(value: string): string {
  // Strip diacritics so "beyonce" matches "Beyoncé".
  const folded = value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
  return folded.split(/\s+/).filter(Boolean).join(' ')
}

function tokenize(normalized: string): string[] {
  return normalized.split(/[\s\-_/,.;:!?"'()[\]]+/).filter(Boolean)
}

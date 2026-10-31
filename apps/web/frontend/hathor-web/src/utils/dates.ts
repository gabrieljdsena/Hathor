// Server instants are UTC ISO-8601 strings (ideally with an explicit Z).
// A bare `new Date("2026-10-11T14:00:00")` parses as LOCAL time, which
// shifts every displayed date by the browser's UTC offset — so all server
// date displays go through here: offset-less datetimes are read as UTC.
// Date-only strings ("2026-10-11", daily mix / discover) are left alone:
// those mean a calendar day, and midnight-local is the intended reading.
export function parseServerDate(iso: string | null | undefined): Date | null {
  if (!iso) return null
  const s = iso.trim()
  if (!s) return null
  if (!s.includes('T')) {
    const d = new Date(s)
    return Number.isNaN(d.getTime()) ? null : d
  }
  const hasOffset = /([zZ]|[+-]\d{2}:?\d{2})$/.test(s)
  const d = new Date(hasOffset ? s : `${s}Z`)
  return Number.isNaN(d.getTime()) ? null : d
}

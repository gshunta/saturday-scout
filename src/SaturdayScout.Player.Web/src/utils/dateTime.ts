/** Formats a UTC/API date for the browser's local timezone. */
export function formatLocalDateTime(value: string | null | undefined): string | null {
  if (typeof value !== 'string' || value.trim().length === 0) return null;

  try {
    const date = new Date(value);
    if (Number.isNaN(date.valueOf())) return null;

    // dateStyle/timeStyle cannot be combined with timeZoneName. Specify their
    // component equivalents so the local timezone abbreviation remains visible.
    return new Intl.DateTimeFormat(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZoneName: 'short',
    }).format(date);
  } catch {
    // Bad provider data or an unsupported runtime locale must not take down the page.
    return null;
  }
}

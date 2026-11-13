import { describe, expect, it } from 'vitest';
import { formatLocalDateTime } from './dateTime';

describe('formatLocalDateTime', () => {
  it('formats API timestamps in local time and includes the timezone abbreviation', () => {
    const result = formatLocalDateTime('2026-01-01T12:30:00Z');

    expect(result).toContain('2026');
    expect(result).toMatch(/\d{1,2}:30/);
    expect(result).not.toBeNull();
  });

  it.each([null, undefined, '', 'not-a-date', '2026-99-99T88:77:00Z'])(
    'returns no display value for missing or malformed input (%s)', value => {
      expect(formatLocalDateTime(value)).toBeNull();
    },
  );
});

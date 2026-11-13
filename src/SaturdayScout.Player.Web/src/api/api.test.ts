import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, bffRequest } from './api';

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': 'application/problem+json' },
  });
}

describe('BFF API client', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('handles 204 without trying to parse JSON and disables browser caching', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(204));
    vi.stubGlobal('fetch', fetchMock);

    await expect(bffRequest<null>('/bff/api/v1/leagues/me')).resolves.toBeNull();
    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ credentials: 'same-origin', cache: 'no-store' });
  });

  it('preserves validation details from a 422 Problem Details response', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(422, {
      title: 'Invalid picks', status: 422, errors: { confidence: ['Ranks must be unique.'] },
    })));

    const result = bffRequest('/bff/api/v1/slates/1/picks');
    await expect(result).rejects.toMatchObject({ status: 422, message: 'Invalid picks' } satisfies Partial<ApiError>);
    await expect(result).rejects.toMatchObject({ details: { errors: { confidence: ['Ranks must be unique.'] } } });
  });

  it.each([400, 401, 403, 404, 409, 500, 502, 503])('maps HTTP %s to a typed error', async status => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(status, { title: `HTTP ${status}`, status })));
    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({ status });
  });

  it('uses a safe actionable message for forbidden BFF responses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Forbidden', detail: 'raw policy name' }), {
      status: 403, headers: { 'Content-Type': 'application/problem+json' },
    })));
    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({
      status: 403, message: 'You do not have access to this league or action.',
    });
  });

  it('does not surface internal details from unexpected server errors', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Internal Server Error', detail: 'database stack trace' }), {
      status: 500, headers: { 'Content-Type': 'application/problem+json' },
    })));
    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({
      status: 500, message: 'Saturday Scout is temporarily unavailable. Try again later.',
    });
  });

  it('reports network failures and rejects paths outside the fixed BFF prefix', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')));
    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({ status: null });
    await expect(bffRequest('/api/v1/admin/dashboard')).rejects.toMatchObject({ status: null });
  });

  it('clears the UI session only when a BFF 401 is confirmed by the session endpoint', async () => {
    const listener = vi.fn();
    window.addEventListener('saturday-scout:unauthorized', listener);
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === '/auth/session'
        ? response(401, { title: 'Authentication is required.', status: 401 })
        : response(401, { title: 'API assertion rejected', status: 401 }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls.map(([input]) => String(input))).toEqual([
      '/bff/api/v1/leagues/me', '/auth/session',
    ]);
    window.removeEventListener('saturday-scout:unauthorized', listener);
  });

  it('preserves the authenticated Player session when only the downstream API returns 401', async () => {
    const listener = vi.fn();
    window.addEventListener('saturday-scout:unauthorized', listener);
    const playerSession = { userId: 'player-guid', displayName: null, roles: ['Player'] };
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
      String(input) === '/auth/session'
        ? new Response(JSON.stringify(playerSession), { status: 200, headers: { 'Content-Type': 'application/json' } })
        : response(401, { title: 'API assertion rejected', status: 401 })));

    await expect(bffRequest('/bff/api/v1/leagues/me')).rejects.toMatchObject({
      status: 401,
      message: 'This request was not authorized. Your Player session may still be active; try again or contact support.',
    });
    expect(listener).not.toHaveBeenCalled();
    window.removeEventListener('saturday-scout:unauthorized', listener);
  });
});

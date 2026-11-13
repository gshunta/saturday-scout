import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { LeaderboardDto, LeagueDto, PlayerSessionDto, PlayerSlateDto } from './api/contracts';
import { renderPlayerRoute } from './test/render';

const session: PlayerSessionDto = {
  userId: '11111111-1111-1111-1111-111111111111',
  displayName: null,
  roles: ['Player'],
};
const leagues: LeagueDto[] = [
  { id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', name: 'Saturday Scout Beta', commissionerUserId: session.userId },
  { id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', name: 'Friends League', commissionerUserId: session.userId },
];

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(handler: (url: URL, init?: RequestInit) => Response | Promise<Response>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) =>
    handler(new URL(String(input), 'https://localhost:5173'), init));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('Player read-only portal', () => {
  it('restores a session and displays the selected league dashboard', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/');

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Saturday Scout Beta' })).toBeTruthy();
    expect(screen.getByText('There is no active published slate for this league right now.')).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Player navigation' })).toBeTruthy();
  });

  it('redirects an unauthenticated protected route to login', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json({ title: 'Authentication is required.', status: 401 }, 401);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/standings');

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeTruthy();
    expect(screen.queryByRole('navigation', { name: 'Player navigation' })).toBeNull();
  });

  it('logs in through the existing antiforgery-protected Identity endpoint', async () => {
    let authenticated = false;
    const fetchMock = mockFetch((url, init) => {
      if (url.pathname === '/auth/session') {
        return authenticated
          ? json(session)
          : json({ title: 'Authentication is required.', status: 401 }, 401);
      }
      if (url.pathname === '/auth/antiforgery') return json({ token: 'test-antiforgery-token' });
      if (url.pathname === '/auth/login') {
        expect(init?.method).toBe('POST');
        expect(init?.credentials).toBe('same-origin');
        expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('test-antiforgery-token');
        expect(init?.redirect).toBe('follow');
        expect(new URLSearchParams(String(init?.body)).get('email')).toBe('player@example.test');
        authenticated = true;
        // The browser follows the Identity 302 and fetch resolves with the final
        // same-origin response; the Set-Cookie is applied before session read.
        return new Response(null, { status: 200 });
      }
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    const user = userEvent.setup();
    renderPlayerRoute('/login');

    await user.type(await screen.findByLabelText('Email'), 'player@example.test');
    await user.type(screen.getByLabelText('Password'), 'test-only-password');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeTruthy();
    expect(fetchMock.mock.calls.some(([input]) => String(input) === '/auth/login')).toBe(true);
  });

  it('restores the exact session response shape with a null display name', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json({ userId: session.userId, displayName: null, roles: ['Player'] });
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    renderPlayerRoute('/profile');

    expect(await screen.findByRole('heading', { name: 'Saturday Scout player' })).toBeTruthy();
  });

  it('keeps the protected route authenticated when a BFF request is unauthorized but session is valid', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json({ title: 'API assertion rejected', status: 401 }, 401);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    renderPlayerRoute('/picks');

    expect(await screen.findByText(/This request was not authorized/)).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Player navigation' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Sign in' })).toBeNull();
  });

  it('rejects a saved league id that is not in the authorized membership list', async () => {
    localStorage.setItem('saturdayScout.selectedLeagueId', 'ffffffff-ffff-ffff-ffff-ffffffffffff');
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/');

    const selector = await screen.findByRole('combobox', { name: 'Select league' });
    await waitFor(() => expect((selector as HTMLSelectElement).value).toBe(leagues[0]!.id));
    expect(localStorage.getItem('saturdayScout.selectedLeagueId')).toBe(leagues[0]!.id);
  });

  it('shows a useful empty state when the player has no leagues', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([]);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/');

    expect(await screen.findByRole('heading', { name: 'No leagues yet' })).toBeTruthy();
    expect(screen.getByText('Your invited league will appear here when your membership is active.')).toBeTruthy();
  });

  it.each([
    { lockUtc: '2026-01-01T12:30:00Z', shouldShowLock: true },
    { lockUtc: 'provider-returned-an-invalid-date', shouldShowLock: false },
    { lockUtc: null, shouldShowLock: false },
  ])('renders dashboard safely for lock time $lockUtc', async ({ lockUtc, shouldShowLock }) => {
    const slate: PlayerSlateDto = {
      id: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
      leagueId: leagues[0]!.id,
      season: 2026,
      week: 11,
      status: 'Published',
      lockUtc,
      publishedUtc: null,
      games: [],
    };
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(slate);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/');

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeTruthy();
    expect(await screen.findByText('2026 · Week 11')).toBeTruthy();
    expect(Boolean(screen.queryByText(/^Locks /))).toBe(shouldShowLock);
  });

  it('renders standings from the authorized leaderboard response without calculating scores', async () => {
    const leaderboard: LeaderboardDto = {
      leagueId: leagues[0]!.id,
      scope: 'college',
      season: 2026,
      week: null,
      entries: [
        { userId: session.userId, displayName: 'Scout Player', points: 10, correctPicks: 8, pendingGames: 2 },
      ],
    };
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/leaderboard')) return json(leaderboard);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/standings');

    expect(await screen.findByRole('heading', { name: 'Standings' })).toBeTruthy();
    expect(await screen.findByRole('cell', { name: '10' })).toBeTruthy();
    expect(screen.getByRole('row', { name: /Scout Player 10 8 2/ })).toBeTruthy();
  });

  it('signs out through the antiforgery-protected endpoint and returns to login', async () => {
    const fetchMock = mockFetch((url, init) => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      if (url.pathname === '/auth/antiforgery') return json({ token: 'test-antiforgery-token' });
      if (url.pathname === '/auth/logout') {
        expect(init?.method).toBe('POST');
        expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('test-antiforgery-token');
        expect(init?.redirect).toBe('follow');
        return new Response(null, { status: 200 });
      }
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    const user = userEvent.setup();
    renderPlayerRoute('/');

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeTruthy();
    expect(fetchMock.mock.calls.some(([input]) => String(input) === '/auth/logout')).toBe(true);
  });

  it('selects only an authorized league and uses it for subsequent reads', async () => {
    const fetchMock = mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    renderPlayerRoute('/');
    const selector = await screen.findByRole('combobox', { name: 'Select league' });
    await screen.findByRole('option', { name: 'Friends League' });
    await userEvent.setup().selectOptions(selector, leagues[1]!.id);

    await waitFor(() => expect(localStorage.getItem('saturdayScout.selectedLeagueId')).toBe(leagues[1]!.id));
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes(leagues[1]!.id))).toBe(true);
  });
});

import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { LeaderboardDto, LeagueDto, PickSheetDto, PlayerSessionDto, PlayerSlateDto } from './api/contracts';
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
const activeSlate: PlayerSlateDto = {
  id: 'cccccccc-cccc-cccc-cccc-cccccccccccc', leagueId: leagues[0]!.id, season: 2026, week: 12,
  status: 'Published', lockUtc: '2099-11-16T01:54:29Z', publishedUtc: null, games: [],
};

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

  it.each([
    { sheet: null, status: 'Not Started', action: 'Open Picks' },
    { sheet: { id: 'sheet-draft', slateId: activeSlate.id, status: 'Draft', submittedUtc: null, version: 1, picks: [] }, status: 'Draft', action: 'Open Picks' },
    { sheet: { id: 'sheet-submitted', slateId: activeSlate.id, status: 'Submitted', submittedUtc: '2026-11-01T12:00:00Z', version: 2, picks: [] }, status: 'Submitted', action: 'View submitted picks' },
  ] satisfies Array<{ sheet: PickSheetDto | null; status: string; action: string }>)('shows dashboard pick status $status', async ({ sheet, status, action }) => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/picks/me')) return sheet ? json(sheet) : json({ title: 'Not found' }, 404);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/');

    expect(await screen.findByText(status)).toBeTruthy();
    expect(await screen.findByRole('link', { name: action })).toBeTruthy();
  });

  it('reports pick-sheet loading and errors without offering a misleading action', async () => {
    let finishSheetRead!: (response: Response) => void;
    let sheetReads = 0;
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/picks/me')) {
        sheetReads++;
        return sheetReads === 1
          ? new Promise<Response>(resolve => { finishSheetRead = resolve; })
          : json({ title: 'Not found' }, 404);
      }
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    const user = userEvent.setup();
    renderPlayerRoute('/');
    expect(await screen.findByText('Loading your pick-sheet status…')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Open Picks' })).toBeNull();
    finishSheetRead!(json({ title: 'Temporary error' }, 503));
    expect(await screen.findByText('Saturday Scout is temporarily unavailable. Try again later.')).toBeTruthy();
    expect(screen.getByText('Unavailable')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Open Picks' })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Not Started')).toBeTruthy();
    expect(await screen.findByRole('link', { name: 'Open Picks' })).toBeTruthy();
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
    const slate: PlayerSlateDto = { ...activeSlate, week: 11, lockUtc };
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(slate);
      if (url.pathname.endsWith('/picks/me')) return json({ title: 'Not found' }, 404);
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
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/slates/weeks')) return json([10, 11, 12]);
      if (url.pathname.endsWith('/leaderboard')) return json(leaderboard);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/standings');

    expect(await screen.findByRole('heading', { name: 'Standings' })).toBeTruthy();
    expect((await screen.findAllByRole('cell', { name: '10' })).length).toBeGreaterThanOrEqual(2);
    expect(screen.getByRole('row', { name: /1 Scout Player 10 10 8 2/ })).toBeTruthy();
  });

  it('uses the active slate season and displays authoritative overall and active-week points', async () => {
    const calls: string[] = [];
    const user = userEvent.setup();
    const fetchMock = mockFetch(url => {
      calls.push(url.pathname + url.search);
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/slates/weeks')) return json([10, 11, 12]);
      if (url.pathname.endsWith('/leaderboard')) {
        const week = new URLSearchParams(url.search).get('week');
        const weeklyValues: Record<string, { points: number; correctPicks: number; pendingGames: number }> = {
          '10': { points: 0, correctPicks: 0, pendingGames: 0 },
          '11': { points: 5, correctPicks: 2, pendingGames: 6 },
          '12': { points: 7, correctPicks: 3, pendingGames: 4 },
        };
        const selected = week ? weeklyValues[week] : undefined;
        return json({
          leagueId: leagues[0]!.id, scope: 'college', season: 2026, week: week ? Number(week) : null,
          entries: [{ userId: session.userId, displayName: 'Scout Player', points: selected?.points ?? 48,
            correctPicks: selected?.correctPicks ?? 8, pendingGames: selected?.pendingGames ?? 10 }],
        } satisfies LeaderboardDto);
      }
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/standings');

    expect(await screen.findByRole('columnheader', { name: 'Overall points' })).toBeTruthy();
    expect(await screen.findByRole('columnheader', { name: 'Week 12 points' })).toBeTruthy();
    expect(await screen.findByRole('columnheader', { name: 'Week 12 correct picks' })).toBeTruthy();
    expect(await screen.findByRole('columnheader', { name: 'Week 12 pending picks' })).toBeTruthy();
    expect(screen.getByRole('row', { name: /Scout Player 48 7 3 4/ })).toBeTruthy();
    const periodOptions = screen.getByRole('combobox', { name: 'Standings period' }).querySelectorAll('option');
    expect(Array.from(periodOptions).map(option => option.textContent)).toEqual([
      'Season overall only', 'Week 10', 'Week 11', 'Week 12 · active',
    ]);
    expect(screen.queryByRole('option', { name: 'Week 1' })).toBeNull();
    expect(calls.some(call => call.includes('season=2026') && !call.includes('week='))).toBe(true);
    expect(calls.some(call => call.includes('season=2026') && call.includes('week=12'))).toBe(true);
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('season=2026'))).toBe(true);
    await user.selectOptions(screen.getByRole('combobox', { name: 'Standings period' }), '11');
    expect(await screen.findByRole('columnheader', { name: 'Week 11 pending picks' })).toBeTruthy();
    expect(await screen.findByRole('row', { name: /Scout Player 48 5 2 6/ })).toBeTruthy();
    await waitFor(() => expect(calls.some(call => call.includes('season=2026') && call.includes('week=11'))).toBe(true));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Standings period' }), '');
    expect(await screen.findByRole('columnheader', { name: 'Overall pending picks' })).toBeTruthy();
    expect(await screen.findByRole('row', { name: /Scout Player 48 8 10/ })).toBeTruthy();
  });

  it('allows switching the weekly standings query and shows season standings when there is no active slate', async () => {
    const calls: string[] = [];
    mockFetch(url => {
      calls.push(url.pathname + url.search);
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      if (url.pathname.endsWith('/slates/weeks')) return json([8]);
      if (url.pathname.endsWith('/leaderboard')) return json({
        leagueId: leagues[0]!.id, scope: 'college', season: 2026,
        week: new URLSearchParams(url.search).get('week') ? Number(new URLSearchParams(url.search).get('week')) : null,
        entries: [{ userId: session.userId, displayName: 'Scout Player', points: 12, correctPicks: 3, pendingGames: 0 }],
      } satisfies LeaderboardDto);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    const user = userEvent.setup();
    renderPlayerRoute('/standings');

    expect(await screen.findByRole('heading', { name: 'Standings' })).toBeTruthy();
    expect(await screen.findByRole('columnheader', { name: 'Overall points' })).toBeTruthy();
    expect(screen.queryByRole('columnheader', { name: /Week .* points/ })).toBeNull();
    await user.selectOptions(screen.getByLabelText('Standings period'), '8');
    expect(await screen.findByRole('columnheader', { name: 'Week 8 points' })).toBeTruthy();
    await waitFor(() => expect(calls.some(call => call.includes('week=8'))).toBe(true));
  });

  it('uses weekly correct and pending counts for weeks with no submitted sheets', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/slates/weeks')) return json([10, 11, 12]);
      if (url.pathname.endsWith('/leaderboard')) {
        const week = new URLSearchParams(url.search).get('week');
        return json({
          leagueId: leagues[0]!.id, scope: 'college', season: 2026, week: week ? Number(week) : null,
          entries: [{ userId: session.userId, displayName: 'Scout Player', points: week ? 0 : 48,
            correctPicks: week ? 0 : 8, pendingGames: week ? 0 : 10 }],
        } satisfies LeaderboardDto);
      }
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    const user = userEvent.setup();
    renderPlayerRoute('/standings');
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Standings period' }), '10');
    expect(await screen.findByRole('row', { name: /Scout Player 48 0 0 0/ })).toBeTruthy();
  });

  it('shows a season empty state and no week options when that season has no slates', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/slates/weeks')) return json([]);
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    renderPlayerRoute('/standings');
    expect(await screen.findByText('There are no college football slates for 2026 in this league.')).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Standings period' }).querySelectorAll('option')).toHaveLength(1);
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('moves to an available week when the selected week disappears after changing season', async () => {
    const calls: string[] = [];
    mockFetch(url => {
      calls.push(url.pathname + url.search);
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return json(activeSlate);
      if (url.pathname.endsWith('/slates/weeks')) {
        return json(new URLSearchParams(url.search).get('season') === '2025' ? [8] : [10, 11, 12]);
      }
      if (url.pathname.endsWith('/leaderboard')) {
        const week = new URLSearchParams(url.search).get('week');
        return json({
          leagueId: leagues[0]!.id, scope: 'college', season: Number(new URLSearchParams(url.search).get('season')),
          week: week ? Number(week) : null,
          entries: [{ userId: session.userId, displayName: 'Scout Player', points: week === '8' ? 4 : 12,
            correctPicks: week === '8' ? 1 : 3, pendingGames: week === '8' ? 2 : 5 }],
        } satisfies LeaderboardDto);
      }
      throw new Error(`Unexpected request: ${url.pathname}`);
    });

    const user = userEvent.setup();
    renderPlayerRoute('/standings');
    const period = await screen.findByRole('combobox', { name: 'Standings period' });
    await user.selectOptions(period, '11');
    expect(await screen.findByRole('columnheader', { name: 'Week 11 pending picks' })).toBeTruthy();
    const season = screen.getByRole('spinbutton', { name: 'Season' });
    fireEvent.change(season, { target: { value: '2025' } });
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Standings period' }) as HTMLSelectElement).value).toBe('8'));
    expect(await screen.findByRole('columnheader', { name: 'Week 8 pending picks' })).toBeTruthy();
    expect(screen.getByRole('row', { name: /Scout Player 12 4 1 2/ })).toBeTruthy();
    expect(calls.some(call => call.includes('season=2025') && call.includes('week=8'))).toBe(true);
  });

  it('lets players change leagues directly from the League page', async () => {
    mockFetch(url => {
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json(leagues);
      if (url.pathname.endsWith('/slates/active')) return new Response(null, { status: 204 });
      throw new Error(`Unexpected request: ${url.pathname}`);
    });
    const user = userEvent.setup();
    renderPlayerRoute('/league');

    expect(await screen.findByText('Selected league')).toBeTruthy();
    await user.click(await screen.findByRole('button', { name: 'Select Friends League' }));
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Select league' }) as HTMLSelectElement).value).toBe(leagues[1]!.id));
    expect(screen.getByText('Current league')).toBeTruthy();
    expect(localStorage.getItem('saturdayScout.selectedLeagueId')).toBe(leagues[1]!.id);
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

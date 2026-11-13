import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { LeagueDto, PickDto, PickSheetDto, PlayerSessionDto, PlayerSlateDto } from '../api/contracts';
import { renderPlayerRoute } from '../test/render';

const session: PlayerSessionDto = { userId: 'u1', displayName: 'Player One', roles: ['Player'] };
const league: LeagueDto = { id: 'league-1', name: 'Saturday Scout Beta', commissionerUserId: 'c1' };
const slate: PlayerSlateDto = {
  id: 'slate-1', leagueId: league.id, season: 2026, week: 12, status: 'Published',
  lockUtc: '2099-11-16T01:54:29Z', publishedUtc: '2026-11-01T12:00:00Z',
  games: Array.from({ length: 10 }, (_, index) => ({
    displayOrder: index + 1, gameId: `game-${index + 1}`, homeTeamId: `home-${index + 1}`,
    homeTeam: `Home ${index + 1}`, homeAbbreviation: `H${index + 1}`, awayTeamId: `away-${index + 1}`,
    awayTeam: `Away ${index + 1}`, awayAbbreviation: `A${index + 1}`,
    kickoffUtc: `2099-11-14T${12 + index}:00:00Z`, status: 'Scheduled', homeScore: null, awayScore: null,
  })),
};

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function pickFetch(options: {
  sheet?: PickSheetDto | null;
  onSave?: (picks: PickDto[], version: number) => PickSheetDto;
  onSubmit?: (version: number) => PickSheetDto;
  slateResponse?: Response;
}) {
  let currentSheet = options.sheet ?? null;
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'https://127.0.0.1:7276');
    if (url.pathname === '/auth/session') return json(session);
    if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
    if (url.pathname.endsWith('/slates/active')) return options.slateResponse ?? json(slate);
    if (url.pathname.endsWith('/picks/me')) return currentSheet ? json(currentSheet) : json({ title: 'Not found' }, 404);
    if (url.pathname === '/auth/antiforgery') return json({ token: 'csrf-test' });
    if (url.pathname.endsWith('/picks') && init?.method === 'PUT') {
      expect(new Headers(init.headers).get('X-CSRF-TOKEN')).toBe('csrf-test');
      const body = JSON.parse(String(init.body)) as { picks: PickDto[]; expectedVersion: number };
      currentSheet = options.onSave?.(body.picks, body.expectedVersion) ?? {
        id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null,
        version: body.expectedVersion + 1, picks: body.picks,
      };
      return json(currentSheet);
    }
    if (url.pathname.endsWith('/submit') && init?.method === 'POST') {
      expect(new Headers(init.headers).get('X-CSRF-TOKEN')).toBe('csrf-test');
      const body = JSON.parse(String(init.body)) as { expectedVersion: number };
      currentSheet = options.onSubmit?.(body.expectedVersion) ?? {
        ...(currentSheet ?? { id: 'sheet-1', slateId: slate.id, picks: [] }),
        status: 'Submitted', submittedUtc: '2099-11-14T20:00:00Z', version: body.expectedVersion + 1,
      };
      return json(currentSheet);
    }
    throw new Error(`Unexpected request: ${url.pathname} ${init?.method ?? 'GET'}`);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('React Picks workflow', () => {
  it('renders an active slate and handles the no-slate response', async () => {
    pickFetch({});
    const view = renderPlayerRoute('/picks');
    expect(await screen.findByRole('heading', { name: 'Week 12 picks' })).toBeTruthy();
    expect(screen.getAllByText('Choose the winner')).toHaveLength(10);

    view.unmount();
    pickFetch({ slateResponse: new Response(null, { status: 204 }) });
    renderPlayerRoute('/picks');
    expect(await screen.findByRole('heading', { name: 'No published slate available' })).toBeTruthy();
  });

  it('restores an existing draft and keeps a submitted sheet read-only', async () => {
    const saved: PickSheetDto = { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null,
      version: 4, picks: [{ gameId: 'game-1', selectedTeamId: 'away-1', confidenceRank: 5 }] };
    pickFetch({ sheet: saved });
    const view = renderPlayerRoute('/picks');
    const winner = await screen.findByRole('radio', { name: /^Away 1 A1/ }) as HTMLInputElement;
    await waitFor(() => expect(winner.checked).toBe(true));
    expect((screen.getByLabelText('Confidence rank for Away 1 at Home 1') as HTMLSelectElement).value).toBe('5');

    view.unmount();
    pickFetch({ sheet: { ...saved, status: 'Submitted', submittedUtc: '2099-11-14T20:00:00Z' } });
    renderPlayerRoute('/picks');
    expect(await screen.findByText(/Your submitted picks are read-only/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save draft' })).toBeNull();
    expect((await screen.findByRole('radio', { name: /^Away 1 A1/ })).closest('fieldset')?.hasAttribute('disabled')).toBe(true);
  });

  it('saves a valid partial draft through the antiforgery-protected BFF and restores it', async () => {
    const fetchMock = pickFetch({ onSave: (picks, version) => {
      expect(version).toBe(0);
      expect(picks).toEqual([{ gameId: 'game-1', selectedTeamId: 'away-1', confidenceRank: 1 }]);
      return { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null, version: 1, picks };
    } });
    const user = userEvent.setup();
    renderPlayerRoute('/picks');
    await user.click(await screen.findByRole('radio', { name: /^Away 1 A1/ }));
    await user.selectOptions(screen.getByLabelText('Confidence rank for Away 1 at Home 1'), '1');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByText('Draft saved. Your selections are available when you return.')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([input]) => String(input).endsWith('/picks') )).toBe(true);
    expect(fetchMock.mock.calls.some(([input, init]) => String(input).endsWith('/picks') && init?.method === 'PUT')).toBe(true);
  });

  it('shows missing and duplicate-rank validation during review without submitting', async () => {
    pickFetch({ sheet: { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null, version: 1, picks: [] } });
    const user = userEvent.setup();
    const fetchMock = vi.mocked(globalThis.fetch);
    renderPlayerRoute('/picks');
    await user.click(await screen.findByRole('button', { name: 'Review submission' }));
    expect(screen.getByText(/This sheet is not ready to submit/)).toBeTruthy();
    expect(screen.getByText(/10 game\(s\) still need a winner/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Confirm submission' })).toHaveProperty('disabled', true);
    expect(fetchMock.mock.calls.some(([input]) => String(input).endsWith('/submit'))).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Back to picks' }));
    await user.click(screen.getByRole('radio', { name: /^Away 1 A1/ }));
    await user.selectOptions(screen.getByLabelText('Confidence rank for Away 1 at Home 1'), '1');
    await user.click(screen.getByRole('radio', { name: /^Away 2 A2/ }));
    const secondRanks = screen.getByLabelText('Confidence rank for Away 2 at Home 2') as HTMLSelectElement;
    expect([...secondRanks.options].find(option => option.value === '1')?.disabled).toBe(true);
  });

  it('submits a complete sheet once and renders the authoritative submitted state', async () => {
    let submitCalls = 0;
    const fetchMock = pickFetch({ onSave: (picks, version) => ({ id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null, version: version + 1, picks }),
      onSubmit: version => {
        submitCalls++;
        const body = JSON.parse(String(fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body)) as { picks: PickDto[] };
        return { id: 'sheet-1', slateId: slate.id, status: 'Submitted', submittedUtc: '2099-11-14T20:00:00Z', version: version + 1, picks: body.picks };
      } });
    const user = userEvent.setup();
    renderPlayerRoute('/picks');
    for (let i = 1; i <= 10; i++) {
      await user.click(await screen.findByRole('radio', { name: new RegExp(`^Away ${i} A${i}`) }));
      await user.selectOptions(screen.getByLabelText(`Confidence rank for Away ${i} at Home ${i}`), String(i));
    }
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText('Draft saved. Your selections are available when you return.');
    await user.click(screen.getByRole('button', { name: 'Review submission' }));
    const confirm = screen.getByRole('button', { name: 'Confirm submission' });
    await user.dblClick(confirm);
    expect(await screen.findByText('Your picks are submitted and now read-only.')).toBeTruthy();
    expect(submitCalls).toBe(1);
    expect(screen.queryByRole('button', { name: 'Save draft' })).toBeNull();
    expect((await screen.findByRole('radio', { name: /^Away 1 A1/ })).closest('fieldset')?.hasAttribute('disabled')).toBe(true);
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
  });

  it('shows locked state and does not permit edits', async () => {
    pickFetch({ sheet: null });
    const expired = { ...slate, lockUtc: '2000-01-01T00:00:00Z' };
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'https://127.0.0.1');
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
      if (url.pathname.endsWith('/slates/active')) return json(expired);
      if (url.pathname.endsWith('/picks/me')) return json({ title: 'Not found' }, 404);
      throw new Error(url.pathname);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPlayerRoute('/picks');
    expect(await screen.findByText('This slate is locked. Existing picks remain visible, but changes and submissions are disabled.')).toBeTruthy();
    expect((await screen.findByRole('radio', { name: /^Away 1 A1/ })).closest('fieldset')?.hasAttribute('disabled')).toBe(true);
    expect(fetchMock.mock.calls.some(([input]) => String(input).endsWith('/picks'))).toBe(false);
  });

  it('preserves local selections after a concurrency conflict and requires an explicit server choice', async () => {
    const remote: PickSheetDto = { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null,
      version: 8, picks: [{ gameId: 'game-1', selectedTeamId: 'home-1', confidenceRank: 4 }] };
    let saveAttempts = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'https://127.0.0.1:7276');
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
      if (url.pathname.endsWith('/slates/active')) return json(slate);
      if (url.pathname.endsWith('/picks/me')) return json(remote);
      if (url.pathname === '/auth/antiforgery') return json({ token: 'csrf-test' });
      if (url.pathname.endsWith('/picks') && init?.method === 'PUT') { saveAttempts++; return json({ title: 'Conflict' }, 409); }
      throw new Error(url.pathname);
    }));
    const user = userEvent.setup();
    renderPlayerRoute('/picks');
    const awayWinner = await screen.findByRole('radio', { name: /^Away 1 A1/ });
    await user.click(awayWinner);
    await user.selectOptions(screen.getByLabelText('Confidence rank for Away 1 at Home 1'), '2');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByText(/server draft changed or the slate locked/i)).toBeTruthy();
    expect((screen.getByRole('radio', { name: /^Away 1 A1/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByRole('button', { name: 'Load server version' })).toBeTruthy();
    expect(saveAttempts).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Load server version' }));
    expect((screen.getByRole('radio', { name: /^Home 1 H1/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText('Confidence rank for Away 1 at Home 1') as HTMLSelectElement).value).toBe('4');
  });

  it('reports duplicate confidence ranks returned by a draft and rejects saving that draft', async () => {
    const invalidDraft: PickSheetDto = { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null, version: 2,
      picks: [
        { gameId: 'game-1', selectedTeamId: 'away-1', confidenceRank: 1 },
        { gameId: 'game-2', selectedTeamId: 'home-2', confidenceRank: 1 },
      ] };
    const fetchMock = pickFetch({ sheet: invalidDraft });
    const user = userEvent.setup();
    renderPlayerRoute('/picks');
    expect(await screen.findByText('Confidence ranks used more than once: 1.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Save draft' })).toHaveProperty('disabled', true);
    await user.click(screen.getByRole('button', { name: 'Review submission' }));
    expect(screen.getByText('Rank 1 is assigned more than once.')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([input, init]) => String(input).endsWith('/picks') && init?.method === 'PUT')).toBe(false);
    expect(fetchMock.mock.calls.some(([input]) => String(input).endsWith('/submit'))).toBe(false);
  });

  it('refreshes the slate after the server rejects a write because its lock has passed', async () => {
    const remote: PickSheetDto = { id: 'sheet-1', slateId: slate.id, status: 'Draft', submittedUtc: null, version: 1, picks: [] };
    let activeReads = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'https://127.0.0.1:7276');
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
      if (url.pathname.endsWith('/slates/active')) return json(activeReads++ === 0 ? slate : { ...slate, lockUtc: '2000-01-01T00:00:00Z' });
      if (url.pathname.endsWith('/picks/me')) return json(remote);
      if (url.pathname === '/auth/antiforgery') return json({ token: 'csrf-test' });
      if (url.pathname.endsWith('/picks') && init?.method === 'PUT') return json({ title: 'Conflict' }, 409);
      throw new Error(url.pathname);
    }));
    const user = userEvent.setup();
    renderPlayerRoute('/picks');
    await user.click(await screen.findByRole('radio', { name: /^Away 1 A1/ }));
    await user.selectOptions(screen.getByLabelText('Confidence rank for Away 1 at Home 1'), '1');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByText('Locked')).toBeTruthy();
    expect(await screen.findByText(/server draft changed or the slate locked/i)).toBeTruthy();
    expect(activeReads).toBeGreaterThan(1);
    expect((screen.getByRole('radio', { name: /^Away 1 A1/ })).closest('fieldset')?.hasAttribute('disabled')).toBe(true);
  });

  it('routes a confirmed expired session to login and displays forbidden responses safely', async () => {
    let sessionChecks = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'https://127.0.0.1:7276');
      if (url.pathname === '/auth/session') return ++sessionChecks === 1 ? json(session) : json({ title: 'Expired' }, 401);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
      if (url.pathname.endsWith('/slates/active')) return json({ title: 'Expired', status: 401 }, 401);
      throw new Error(url.pathname);
    }));
    const expiredView = renderPlayerRoute('/picks');
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeTruthy();
    expiredView.unmount();

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'https://127.0.0.1:7276');
      if (url.pathname === '/auth/session') return json(session);
      if (url.pathname === '/bff/api/v1/leagues/me') return json([league]);
      if (url.pathname.endsWith('/slates/active')) return json({ title: 'Forbidden', status: 403 }, 403);
      throw new Error(url.pathname);
    }));
    renderPlayerRoute('/picks');
    expect(await screen.findByText('You do not have access to this league or action.')).toBeTruthy();
  });
});

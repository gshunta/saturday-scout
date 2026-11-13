import type { LeaderboardDto, LeagueDto, PickDto, PickSheetDto, PlayerSlateDto, PlayerSessionDto } from './contracts';

export const sessionQueryKey = ['player-session'] as const;
export const leaguesQueryKey = ['player-leagues'] as const;

export class ApiError extends Error {
  constructor(
    readonly status: number | null,
    message: string,
    readonly details?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.status === 204) return null as T;

  const contentType = response.headers.get('content-type') ?? '';
  const text = await response.text();
  let body: unknown;
  if (text && contentType.includes('json')) {
    try {
      body = JSON.parse(text) as unknown;
    } catch {
      throw new ApiError(response.status, 'The server returned an unreadable response.');
    }
  } else if (text) {
    body = text;
  }

  if (!response.ok) {
    const problem = body && typeof body === 'object' ? body as Record<string, unknown> : undefined;
    const title = typeof problem?.['title'] === 'string' ? problem['title'] : undefined;
    const detail = typeof problem?.['detail'] === 'string' ? problem['detail'] : undefined;
    const message = response.status === 401 || response.status === 403 || response.status >= 500
      ? messageForStatus(response.status)
      : detail ?? title ?? messageForStatus(response.status);
    throw new ApiError(response.status, message, body);
  }

  if (body === undefined) {
    throw new ApiError(response.status, 'The server returned an empty response.');
  }
  return body as T;
}

function messageForStatus(status: number): string {
  switch (status) {
    case 400:
    case 422: return 'Review the request and try again.';
    case 401: return 'This request was not authorized. Your Player session may still be active; try again or contact support.';
    case 403: return 'You do not have access to this league or action.';
    case 404: return 'The requested information is no longer available.';
    case 409: return 'The information changed. Refresh and try again.';
    case 500:
    case 502:
    case 503: return 'Saturday Scout is temporarily unavailable. Try again later.';
    default: return 'The request could not be completed.';
  }
}

export async function bffRequest<T>(path: string, init: RequestInit = {}): Promise<T> {
  if (!path.startsWith('/bff/api/v1/')) {
    throw new ApiError(null, 'The request is outside the Player API boundary.');
  }

  let response: Response;
  try {
    response = await fetch(path, {
      ...init,
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json', ...init.headers },
    });
  } catch {
    throw new ApiError(null, 'Could not connect to Saturday Scout. Check your connection and try again.');
  }

  if (response.status === 401) {
    // A BFF 401 may be the downstream API rejecting its server assertion, not
    // Identity rejecting the Player cookie. Only expire the UI session when the
    // host's authoritative session endpoint also says there is no session.
    try {
      if (await getPlayerSession() === null) {
        window.dispatchEvent(new Event('saturday-scout:unauthorized'));
      }
    } catch {
      // A failed session probe cannot prove that the Identity cookie expired.
    }
  }
  return parseResponse<T>(response);
}

export async function getPlayerSession(): Promise<PlayerSessionDto | null> {
  let response: Response;
  try {
    response = await fetch('/auth/session', {
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
  } catch {
    throw new ApiError(null, 'Could not connect to Saturday Scout.');
  }

  if (response.status === 401) return null;
  return parseResponse<PlayerSessionDto>(response);
}

export async function getMyLeagues(): Promise<LeagueDto[]> {
  return bffRequest<LeagueDto[]>('/bff/api/v1/leagues/me');
}

export async function getActiveSlate(leagueId: string): Promise<PlayerSlateDto | null> {
  return bffRequest<PlayerSlateDto | null>(`/bff/api/v1/leagues/${encodeURIComponent(leagueId)}/slates/active`);
}

export async function getCollegeSlateWeeks(leagueId: string, season: number): Promise<number[]> {
  const query = new URLSearchParams({ season: String(season) });
  return bffRequest<number[]>(
    `/bff/api/v1/leagues/${encodeURIComponent(leagueId)}/slates/weeks?${query.toString()}`,
  );
}

export async function getMyPickSheet(slateId: string): Promise<PickSheetDto | null> {
  try {
    return await bffRequest<PickSheetDto>(`/bff/api/v1/slates/${encodeURIComponent(slateId)}/picks/me`);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

export async function saveMyPickSheet(
  slateId: string,
  picks: PickDto[],
  expectedVersion: number,
): Promise<PickSheetDto> {
  const token = await getAntiforgeryToken();
  return bffRequest<PickSheetDto>(`/bff/api/v1/slates/${encodeURIComponent(slateId)}/picks`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
    body: JSON.stringify({ picks, expectedVersion }),
  });
}

export async function submitMyPickSheet(slateId: string, expectedVersion: number): Promise<PickSheetDto> {
  const token = await getAntiforgeryToken();
  return bffRequest<PickSheetDto>(`/bff/api/v1/slates/${encodeURIComponent(slateId)}/submit`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
    body: JSON.stringify({ expectedVersion }),
  });
}

export async function getLeaderboard(leagueId: string, season: number, week?: number): Promise<LeaderboardDto> {
  const query = new URLSearchParams({ scope: 'college', season: String(season) });
  if (week !== undefined) query.set('week', String(week));
  return bffRequest<LeaderboardDto>(
    `/bff/api/v1/leagues/${encodeURIComponent(leagueId)}/leaderboard?${query.toString()}`,
  );
}

export async function getAntiforgeryToken(): Promise<string> {
  let response: Response;
  try {
    response = await fetch('/auth/antiforgery', {
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
  } catch {
    throw new ApiError(null, 'Could not connect to Saturday Scout.');
  }
  const result = await parseResponse<{ token: string }>(response);
  return result.token;
}

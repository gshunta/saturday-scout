import { createContext, useCallback, useContext, useEffect, useState, type PropsWithChildren } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getMyLeagues, leaguesQueryKey } from '../api/api';
import type { LeagueDto } from '../api/contracts';

const preferenceKey = 'saturdayScout.selectedLeagueId';

interface LeagueSelectionValue {
  leagues: LeagueDto[] | undefined;
  loading: boolean;
  error: Error | null;
  selectedLeague: LeagueDto | undefined;
  selectLeague: (id: string) => boolean;
  retryLeagues: () => Promise<void>;
}

const LeagueSelectionContext = createContext<LeagueSelectionValue | null>(null);

function readPreference(): string | null {
  try {
    return localStorage.getItem(preferenceKey);
  } catch {
    return null;
  }
}

function writePreference(id: string | null): void {
  try {
    if (id) localStorage.setItem(preferenceKey, id);
    else localStorage.removeItem(preferenceKey);
  } catch {
    // The preference is optional; authorized leagues always come from the server.
  }
}

export function LeagueSelectionProvider({ children }: PropsWithChildren) {
  const query = useQuery({ queryKey: leaguesQueryKey, queryFn: getMyLeagues, retry: false });
  const [preferredId, setPreferredId] = useState(readPreference);
  const leagues = query.data;
  const selectedLeague = leagues?.find(league => league.id === preferredId) ?? leagues?.[0];

  useEffect(() => {
    if (leagues === undefined) return;
    const validPreferred = preferredId && leagues.some(league => league.id === preferredId);
    const nextId = validPreferred ? preferredId : leagues[0]?.id ?? null;
    if (nextId !== preferredId) setPreferredId(nextId);
    writePreference(nextId);
  }, [leagues, preferredId]);

  const selectLeague = useCallback((id: string) => {
    if (!leagues?.some(league => league.id === id)) return false;
    setPreferredId(id);
    writePreference(id);
    return true;
  }, [leagues]);

  const value: LeagueSelectionValue = {
    leagues,
    loading: query.isPending,
    error: query.error,
    selectedLeague,
    selectLeague,
    retryLeagues: async () => { await query.refetch(); },
  };

  return <LeagueSelectionContext.Provider value={value}>{children}</LeagueSelectionContext.Provider>;
}

export function useLeagueSelection(): LeagueSelectionValue {
  const context = useContext(LeagueSelectionContext);
  if (!context) throw new Error('useLeagueSelection must be used inside LeagueSelectionProvider.');
  return context;
}

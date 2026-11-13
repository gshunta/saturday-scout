import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { getActiveSlate, getMyPickSheet } from '../api/api';
import type { PickSheetDto, PlayerSlateDto } from '../api/contracts';
import { useLeagueSelection } from '../leagues/LeagueSelection';
import { EmptyState, ErrorMessage, Loading } from '../components/States';
import { formatLocalDateTime } from '../utils/dateTime';

export function DashboardPage() {
  const { leagues, loading: leaguesLoading, error: leaguesError, selectedLeague, retryLeagues } = useLeagueSelection();
  const [now, setNow] = useState(Date.now());
  const slateQuery = useQuery({
    queryKey: ['active-player-slate', selectedLeague?.id],
    queryFn: () => getActiveSlate(selectedLeague!.id),
    enabled: Boolean(selectedLeague),
    retry: false,
  });
  const slate = slateQuery.data ?? null;
  const sheetQuery = useQuery({
    queryKey: ['player-pick-sheet', slate?.id],
    queryFn: () => getMyPickSheet(slate!.id),
    enabled: Boolean(slate),
    retry: false,
  });
  const formattedLock = slateQuery.data?.lockUtc
    ? formatLocalDateTime(slateQuery.data.lockUtc)
    : null;

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 15_000);
    return () => window.clearInterval(timer);
  }, []);

  const pickStatus = slateQuery.isPending ? 'Loading' : slateQuery.error ? 'Unavailable'
    : getPickStatus(slate, sheetQuery.data ?? null, sheetQuery.isPending, sheetQuery.error, now);

  if (leaguesLoading) return <Loading label="Loading your leagues…" />;
  if (leaguesError) return <ErrorMessage message={leaguesError.message} onRetry={() => void retryLeagues()} />;
  if (!leagues?.length) {
    return <EmptyState title="No leagues yet"><p>Your invited league will appear here when your membership is active.</p></EmptyState>;
  }

  return (
    <section className="page-stack" aria-labelledby="dashboard-title">
      <div className="page-heading">
        <p className="eyebrow">Player home</p>
        <h1 id="dashboard-title">Dashboard</h1>
        <p className="muted">Your league and current college football slate.</p>
      </div>

      <article className="panel league-panel">
        <div>
          <p className="eyebrow">Selected league</p>
          <h2>{selectedLeague?.name ?? 'Choose a league'}</h2>
          <p className="muted">{leagues.length} {leagues.length === 1 ? 'league' : 'leagues'} available to your account.</p>
        </div>
        <Link className="text-link" to="/league">View league</Link>
      </article>

      <article className="panel slate-panel" aria-live="polite">
        <div className="section-title-row">
          <div>
            <p className="eyebrow">Current slate</p>
            <h2>Weekly games</h2>
          </div>
          <span className="status-pill" role="status">{pickStatus}</span>
        </div>
        {slateQuery.isPending && <Loading label="Checking for a published slate…" />}
        {slateQuery.error && <ErrorMessage message={slateQuery.error.message} onRetry={() => void slateQuery.refetch()} />}
        {slate && sheetQuery.error && <ErrorMessage message={sheetQuery.error.message} onRetry={() => void sheetQuery.refetch()} />}
        {slate && sheetQuery.isPending && <Loading label="Loading your pick-sheet status…" />}
        {!slateQuery.isPending && !slateQuery.error && !slateQuery.data && (
          <p className="muted">There is no active published slate for this league right now.</p>
        )}
        {slateQuery.data && (
          <div className="slate-summary">
            <p className="slate-week">{slateQuery.data.season} · Week {slateQuery.data.week}</p>
            <p>{slateQuery.data.games.length} {slateQuery.data.games.length === 1 ? 'game' : 'games'} in this slate</p>
            {formattedLock && <p className="muted">Locks {formattedLock} (your local time)</p>}
            {!sheetQuery.error && !sheetQuery.isPending && <Link className="button button-primary" to="/picks">
              {sheetQuery.data?.status === 'Submitted' ? 'View submitted picks' : 'Open Picks'}
            </Link>}
          </div>
        )}
      </article>
    </section>
  );
}

function getPickStatus(slate: PlayerSlateDto | null, sheet: PickSheetDto | null,
  loading: boolean, error: Error | null, now: number): string {
  if (!slate) return 'No active slate';
  if (loading) return 'Loading';
  if (error) return 'Unavailable';
  if (slate.status === 'Finalized') return 'Finalized';
  if (sheet?.status === 'Submitted') return 'Submitted';
  if (slate.status === 'Locked' || (slate.lockUtc !== null && Date.parse(slate.lockUtc) <= now)) return 'Locked';
  return sheet === null ? 'Not Started' : 'Draft';
}

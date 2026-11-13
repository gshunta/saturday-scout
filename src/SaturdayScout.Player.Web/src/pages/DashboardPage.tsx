import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { getActiveSlate } from '../api/api';
import { useLeagueSelection } from '../leagues/LeagueSelection';
import { EmptyState, ErrorMessage, Loading } from '../components/States';
import { formatLocalDateTime } from '../utils/dateTime';

export function DashboardPage() {
  const { leagues, loading: leaguesLoading, error: leaguesError, selectedLeague, retryLeagues } = useLeagueSelection();
  const slateQuery = useQuery({
    queryKey: ['active-player-slate', selectedLeague?.id],
    queryFn: () => getActiveSlate(selectedLeague!.id),
    enabled: Boolean(selectedLeague),
    retry: false,
  });
  const formattedLock = slateQuery.data?.lockUtc
    ? formatLocalDateTime(slateQuery.data.lockUtc)
    : null;

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
          {slateQuery.data && <span className="status-pill">{slateQuery.data.status}</span>}
        </div>
        {slateQuery.isPending && <Loading label="Checking for a published slate…" />}
        {slateQuery.error && <ErrorMessage message={slateQuery.error.message} onRetry={() => void slateQuery.refetch()} />}
        {!slateQuery.isPending && !slateQuery.error && !slateQuery.data && (
          <p className="muted">There is no active published slate for this league right now.</p>
        )}
        {slateQuery.data && (
          <div className="slate-summary">
            <p className="slate-week">{slateQuery.data.season} · Week {slateQuery.data.week}</p>
            <p>{slateQuery.data.games.length} {slateQuery.data.games.length === 1 ? 'game' : 'games'} in this slate</p>
            {formattedLock && <p className="muted">Locks {formattedLock} (your local time)</p>}
            <Link className="button button-primary" to="/picks">Open Picks</Link>
          </div>
        )}
      </article>
    </section>
  );
}

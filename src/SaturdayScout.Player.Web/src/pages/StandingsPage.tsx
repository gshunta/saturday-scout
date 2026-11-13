import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getLeaderboard } from '../api/api';
import { useLeagueSelection } from '../leagues/LeagueSelection';
import { EmptyState, ErrorMessage, Loading } from '../components/States';

export function StandingsPage() {
  const { leagues, loading: leaguesLoading, error: leaguesError, selectedLeague, retryLeagues } = useLeagueSelection();
  const [season, setSeason] = useState(new Date().getFullYear());
  const [week, setWeek] = useState<number | null>(null);
  const leaderboard = useQuery({
    queryKey: ['leaderboard', selectedLeague?.id, season, week],
    queryFn: () => getLeaderboard(selectedLeague!.id, season, week ?? undefined),
    enabled: Boolean(selectedLeague),
    retry: false,
  });

  if (leaguesLoading) return <Loading label="Loading your leagues…" />;
  if (leaguesError) return <ErrorMessage message={leaguesError.message} onRetry={() => void retryLeagues()} />;
  if (!leagues?.length) return <EmptyState title="No league standings yet"><p>Join your invited league to see standings.</p></EmptyState>;

  return (
    <section className="page-stack" aria-labelledby="standings-title">
      <div className="page-heading">
        <p className="eyebrow">{selectedLeague?.name}</p>
        <h1 id="standings-title">Standings</h1>
        <p className="muted">Official points and results from Saturday Scout.</p>
      </div>

      <div className="panel standings-panel">
        <div className="standings-controls">
          <label>
            Season
            <input aria-label="Season" type="number" min={2000} max={2200} value={season}
              onChange={event => setSeason(Number(event.target.value))} />
          </label>
          <label>
            View
            <select aria-label="Standings period" value={week ?? ''}
              onChange={event => setWeek(event.target.value ? Number(event.target.value) : null)}>
              <option value="">Season overall</option>
              {Array.from({ length: 20 }, (_, index) => index + 1).map(value =>
                <option key={value} value={value}>Week {value}</option>)}
            </select>
          </label>
        </div>

        {leaderboard.isPending && <Loading label="Loading standings…" />}
        {leaderboard.error && <ErrorMessage message={leaderboard.error.message} onRetry={() => void leaderboard.refetch()} />}
        {leaderboard.data && !leaderboard.data.entries.length && (
          <p className="muted standings-empty">There are no standings to show for this period yet.</p>
        )}
        {leaderboard.data && leaderboard.data.entries.length > 0 && (
          <div className="table-wrap">
            <table>
              <caption>{week === null ? `${season} season standings` : `${season} week ${week} standings`}</caption>
              <thead><tr><th scope="col">Rank</th><th scope="col">Player</th><th scope="col">Points</th><th scope="col">Correct</th><th scope="col">Pending</th></tr></thead>
              <tbody>
                {leaderboard.data.entries.map((entry, index) => (
                  <tr key={entry.userId}>
                    <td><span className="rank-number">{index + 1}</span></td>
                    <th scope="row">{entry.displayName}</th>
                    <td>{entry.points}</td>
                    <td>{entry.correctPicks}</td>
                    <td>{entry.pendingGames}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}

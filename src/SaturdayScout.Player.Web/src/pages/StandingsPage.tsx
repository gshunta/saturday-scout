import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getActiveSlate, getCollegeSlateWeeks, getLeaderboard } from '../api/api';
import { useLeagueSelection } from '../leagues/LeagueSelection';
import { EmptyState, ErrorMessage, Loading } from '../components/States';

export function StandingsPage() {
  const { leagues, loading: leaguesLoading, error: leaguesError, selectedLeague, retryLeagues } = useLeagueSelection();
  const [filterLeagueId, setFilterLeagueId] = useState<string>();
  const [seasonOverride, setSeasonOverride] = useState<number | null>(null);
  const [weekOverride, setWeekOverride] = useState<string>();
  useEffect(() => {
    setFilterLeagueId(undefined);
    setSeasonOverride(null);
    setWeekOverride(undefined);
  }, [selectedLeague?.id]);
  const activeSlate = useQuery({
    queryKey: ['active-player-slate', selectedLeague?.id],
    queryFn: () => getActiveSlate(selectedLeague!.id),
    enabled: Boolean(selectedLeague),
    retry: false,
  });
  const ownsFilters = filterLeagueId === selectedLeague?.id;
  const season = ownsFilters && seasonOverride !== null
    ? seasonOverride
    : activeSlate.data?.season ?? new Date().getUTCFullYear();
  const requestedWeek = ownsFilters && weekOverride !== undefined
    ? (weekOverride === '' ? null : Number(weekOverride))
    : activeSlate.data?.week ?? null;
  const slateWeeks = useQuery({
    queryKey: ['college-slate-weeks', selectedLeague?.id, season],
    queryFn: () => getCollegeSlateWeeks(selectedLeague!.id, season),
    enabled: Boolean(selectedLeague) && !activeSlate.isPending && !activeSlate.error,
    retry: false,
  });
  const availableWeeks = slateWeeks.data ?? [];
  const activeWeekIsAvailable = activeSlate.data?.season === season &&
    availableWeeks.includes(activeSlate.data.week);
  const fallbackWeek = activeWeekIsAvailable ? activeSlate.data!.week : availableWeeks.at(-1) ?? null;
  const week = requestedWeek === null
    ? null
    : availableWeeks.includes(requestedWeek) ? requestedWeek : fallbackWeek;
  const canLoadStandings = Boolean(selectedLeague) && !activeSlate.isPending && !activeSlate.error &&
    !slateWeeks.isPending && !slateWeeks.error;
  const overall = useQuery({
    queryKey: ['leaderboard-overall', selectedLeague?.id, season],
    queryFn: () => getLeaderboard(selectedLeague!.id, season),
    enabled: canLoadStandings && availableWeeks.length > 0,
    retry: false,
  });
  const weekly = useQuery({
    queryKey: ['leaderboard-weekly', selectedLeague?.id, season, week],
    queryFn: () => getLeaderboard(selectedLeague!.id, season, week!),
    enabled: canLoadStandings && week !== null && availableWeeks.includes(week),
    retry: false,
  });

  if (leaguesLoading) return <Loading label="Loading your leagues…" />;
  if (leaguesError) return <ErrorMessage message={leaguesError.message} onRetry={() => void retryLeagues()} />;
  if (!leagues?.length) return <EmptyState title="No league standings yet"><p>Join your invited league to see standings.</p></EmptyState>;
  if (activeSlate.isPending) return <Loading label="Finding the active college week…" />;
  if (activeSlate.error) return <ErrorMessage message={activeSlate.error.message} onRetry={() => void activeSlate.refetch()} />;
  if (slateWeeks.isPending) return <Loading label="Finding league slate weeks…" />;
  if (slateWeeks.error) return <ErrorMessage message={slateWeeks.error.message} onRetry={() => void slateWeeks.refetch()} />;
  if (availableWeeks.length > 0 && (overall.isPending || (week !== null && weekly.isPending)))
    return <Loading label="Loading standings…" />;
  if (overall.error) return <ErrorMessage message={overall.error.message} onRetry={() => void overall.refetch()} />;
  if (availableWeeks.length > 0 && !overall.data)
    return <EmptyState title="No standings available"><p>There are no standings for this season yet.</p></EmptyState>;

  const overallByUser = new Map((overall.data?.entries ?? []).map(entry => [entry.userId, entry]));
  const weeklyByUser = new Map((weekly.data?.entries ?? []).map(entry => [entry.userId, entry]));

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
              onChange={event => {
                const value = Number(event.target.value);
                if (Number.isInteger(value) && value >= 1900 && value <= 9999) {
                  setFilterLeagueId(selectedLeague!.id);
                  setSeasonOverride(value);
                  setWeekOverride(undefined);
                }
              }} />
          </label>
          <label>
            View
            <select aria-label="Standings period" value={week ?? ''}
              onChange={event => {
                setFilterLeagueId(selectedLeague!.id);
                setWeekOverride(event.target.value);
              }}>
              <option value="">Season overall only</option>
              {availableWeeks.map(value =>
                <option key={value} value={value}>Week {value}{activeSlate.data?.week === value ? ' · active' : ''}</option>)}
            </select>
          </label>
        </div>

        {availableWeeks.length === 0 && <p className="muted standings-empty" role="status">
          There are no college football slates for {season} in this league.
        </p>}
        {weekly.error && <ErrorMessage message={weekly.error.message} onRetry={() => void weekly.refetch()} />}
        {(overall.data?.entries.length ?? 0) === 0 && availableWeeks.length > 0 && (
          <p className="muted standings-empty">There are no standings to show for this period yet.</p>
        )}
        {(overall.data?.entries.length ?? 0) > 0 && (
          <div className="table-wrap">
            <table>
              <caption>{week === null ? `${season} season standings` : `${season} standings · Week ${week}`}</caption>
              <thead><tr>
                <th scope="col">Rank</th>
                <th scope="col">Player</th>
                <th scope="col">Overall points</th>
                {week !== null && <th scope="col">Week {week} points</th>}
                <th scope="col">{week === null ? 'Overall correct picks' : `Week ${week} correct picks`}</th>
                <th scope="col">{week === null ? 'Overall pending picks' : `Week ${week} pending picks`}</th>
              </tr></thead>
              <tbody>
                {(overall.data?.entries ?? []).map((entry, index) => {
                  const overallEntry = overallByUser.get(entry.userId);
                  const weeklyEntry = weeklyByUser.get(entry.userId);
                  return (
                  <tr key={entry.userId}>
                    <td><span className="rank-number">{index + 1}</span></td>
                    <th scope="row">{entry.displayName}</th>
                    <td>{overallEntry?.points ?? 0}</td>
                    {week !== null && <td>{weekly.error ? '—' : weeklyEntry?.points ?? 0}</td>}
                    <td>{week === null ? overallEntry?.correctPicks ?? 0 : weekly.error ? '—' : weeklyEntry?.correctPicks ?? 0}</td>
                    <td>{week === null ? overallEntry?.pendingGames ?? 0 : weekly.error ? '—' : weeklyEntry?.pendingGames ?? 0}</td>
                  </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}

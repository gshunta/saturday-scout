import { useLeagueSelection } from '../leagues/LeagueSelection';
import { EmptyState, ErrorMessage, Loading } from '../components/States';

export function LeaguePage() {
  const { leagues, loading, error, selectedLeague, retryLeagues } = useLeagueSelection();
  if (loading) return <Loading label="Loading league membership…" />;
  if (error) return <ErrorMessage message={error.message} onRetry={() => void retryLeagues()} />;
  if (!leagues?.length) return <EmptyState title="No active league"><p>Your private league membership will appear here.</p></EmptyState>;

  return (
    <section className="page-stack" aria-labelledby="league-title">
      <div className="page-heading"><p className="eyebrow">Your membership</p><h1 id="league-title">League</h1></div>
      <article className="panel">
        <p className="eyebrow">Selected league</p>
        <h2>{selectedLeague?.name}</h2>
        <p className="muted">Your membership is authorized by Saturday Scout. League creation and invitations are managed by an administrator.</p>
      </article>
      <div className="panel-list">
        {leagues.map(league => (
          <article className={`panel league-item${league.id === selectedLeague?.id ? ' selected' : ''}`} key={league.id}>
            <span className="league-initial" aria-hidden="true">{league.name.slice(0, 1).toUpperCase()}</span>
            <div><h2>{league.name}</h2><p className="muted">{league.id === selectedLeague?.id ? 'Selected' : 'Available'}</p></div>
          </article>
        ))}
      </div>
    </section>
  );
}

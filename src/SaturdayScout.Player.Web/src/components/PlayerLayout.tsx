import { useState } from 'react';
import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useLeagueSelection } from '../leagues/LeagueSelection';

const navigation = [
  { to: '/', label: 'Dashboard', end: true },
  { to: '/picks', label: 'Picks' },
  { to: '/standings', label: 'Standings' },
  { to: '/league', label: 'League' },
  { to: '/profile', label: 'Profile' },
];

export function PlayerLayout({ children }: { children: ReactNode }) {
  const { session, signOut } = useAuth();
  const { leagues, selectedLeague, selectLeague } = useLeagueSelection();
  const [signOutError, setSignOutError] = useState<string | null>(null);
  const [signingOut, setSigningOut] = useState(false);

  async function handleSignOut() {
    setSigningOut(true);
    setSignOutError(null);
    try {
      await signOut();
    } catch {
      setSignOutError('Sign out could not be completed. Check your connection and retry.');
    } finally {
      setSigningOut(false);
    }
  }

  return (
    <div className="player-app">
      <header className="app-header">
        <NavLink className="brand" to="/" aria-label="Saturday Scout dashboard">
          <span className="brand-mark" aria-hidden="true">S</span>
          <span>Saturday Scout</span>
        </NavLink>
        <div className="header-actions">
          <label className="league-select-label" htmlFor="current-league">League</label>
          <select
            id="current-league"
            aria-label="Select league"
            value={selectedLeague?.id ?? ''}
            disabled={!leagues?.length}
            onChange={event => { selectLeague(event.target.value); }}
          >
            {leagues?.length
              ? leagues.map(league => <option key={league.id} value={league.id}>{league.name}</option>)
              : <option value="">No leagues</option>}
          </select>
          <span className="user-chip">{session?.displayName || 'Player'}</span>
          <button className="button button-quiet sign-out-button" onClick={() => void handleSignOut()} disabled={signingOut}>
            {signingOut ? 'Signing out…' : 'Sign out'}
          </button>
        </div>
      </header>

      {signOutError && <p className="global-error" role="alert">{signOutError}</p>}

      <div className="app-frame">
        <nav className="primary-nav" aria-label="Player navigation">
          {navigation.map(item => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
        <main className="main-content">{children}</main>
      </div>
    </div>
  );
}

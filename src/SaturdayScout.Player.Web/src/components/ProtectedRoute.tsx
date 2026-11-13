import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { LeagueSelectionProvider } from '../leagues/LeagueSelection';
import { Loading } from './States';
import { PlayerLayout } from './PlayerLayout';

export function ProtectedRoute() {
  const { session, loading, error, retrySession } = useAuth();
  const location = useLocation();

  if (loading) return <Loading label="Restoring your Player session…" />;
  if (error) {
    return (
      <main className="auth-state">
        <h1>Saturday Scout</h1>
        <p role="alert">We could not check your session.</p>
        <button className="button button-primary" onClick={() => void retrySession()}>Retry</button>
      </main>
    );
  }
  if (!session) return <Navigate to="/login" replace state={{ from: location.pathname }} />;

  return (
    <LeagueSelectionProvider>
      <PlayerLayout><Outlet /></PlayerLayout>
    </LeagueSelectionProvider>
  );
}

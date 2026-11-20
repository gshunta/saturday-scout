import { Link, Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { LeagueSelectionProvider } from '../leagues/LeagueSelection';
import { Loading } from './States';
import { PlayerLayout } from './PlayerLayout';
import { DraftGuardProvider } from '../picks/DraftGuard';
import { useDraftGuard } from '../picks/DraftGuard';

function SessionExpiredNotice() {
  const { dirty } = useDraftGuard();
  const location = useLocation();
  return (
    <section className="notice notice-error session-expired" role="alert" aria-live="assertive">
      <div>
        <h1>Your session expired</h1>
        <p>{dirty
          ? 'Your unsaved picks are still on this page while it remains open. They are not saved, and leaving or reloading may discard them.'
          : 'Sign in again to continue.'}</p>
      </div>
      <Link
        className="button button-primary"
        to={{ pathname: '/login', search: `?returnUrl=${encodeURIComponent(location.pathname)}` }}
        state={{ from: location.pathname }}
      >Sign in again</Link>
    </section>
  );
}

export function ProtectedRoute() {
  const { session, loading, error, retrySession, sessionExpired } = useAuth();
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
  if (!session && !sessionExpired) return <Navigate to="/login" replace state={{ from: location.pathname }} />;

  return (
    <LeagueSelectionProvider>
      <DraftGuardProvider>
        <PlayerLayout>
          {sessionExpired && <SessionExpiredNotice />}
          <Outlet />
        </PlayerLayout>
      </DraftGuardProvider>
    </LeagueSelectionProvider>
  );
}

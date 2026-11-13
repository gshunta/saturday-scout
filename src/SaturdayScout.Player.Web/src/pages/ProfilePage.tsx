import { useAuth } from '../auth/AuthContext';

export function ProfilePage() {
  const { session } = useAuth();
  return (
    <section className="page-stack" aria-labelledby="profile-title">
      <div className="page-heading"><p className="eyebrow">Account</p><h1 id="profile-title">Profile</h1></div>
      <article className="panel profile-panel">
        <div className="profile-avatar" aria-hidden="true">{session?.displayName?.slice(0, 1).toUpperCase() || 'P'}</div>
        <div><p className="eyebrow">Player</p><h2>{session?.displayName || 'Saturday Scout player'}</h2><p className="muted">Account ID {session?.userId}</p></div>
      </article>
      <p className="muted">Profile preferences will be available in a later milestone.</p>
    </section>
  );
}

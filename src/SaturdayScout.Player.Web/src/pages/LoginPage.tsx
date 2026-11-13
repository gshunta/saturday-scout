import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { ApiError } from '../api/api';
import { useAuth } from '../auth/AuthContext';

export function LoginPage() {
  const { session, loading, signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (!loading && session) return <Navigate to="/" replace />;

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const success = await signIn(email.trim(), password);
      if (!success) {
        setPassword('');
        setError('Unable to sign in. Check your email and password or try again later.');
        return;
      }
      const destination = (location.state as { from?: string } | null)?.from ?? '/';
      navigate(destination.startsWith('/') ? destination : '/', { replace: true });
    } catch (cause) {
      setPassword('');
      setError(cause instanceof ApiError ? cause.message : 'Unable to sign in. Try again later.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="login-page">
      <section className="login-card" aria-labelledby="login-title">
        <div className="brand-mark login-mark" aria-hidden="true">S</div>
        <p className="eyebrow">Invitation beta</p>
        <h1 id="login-title">Sign in</h1>
        <p className="muted">Use the account details from your Saturday Scout invitation.</p>
        {error && <p className="notice notice-error" role="alert">{error}</p>}
        <form onSubmit={event => void handleSubmit(event)}>
          <label htmlFor="email">Email</label>
          <input
            id="email"
            name="email"
            type="email"
            autoComplete="username"
            required
            maxLength={256}
            value={email}
            onChange={event => setEmail(event.target.value)}
          />
          <label htmlFor="password">Password</label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
            maxLength={1024}
            value={password}
            onChange={event => setPassword(event.target.value)}
          />
          <button className="button button-primary login-submit" type="submit" disabled={submitting || loading}>
            {submitting ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </section>
    </main>
  );
}

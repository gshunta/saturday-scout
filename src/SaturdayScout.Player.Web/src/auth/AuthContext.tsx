import { createContext, useCallback, useContext, useEffect, useRef, useState, type PropsWithChildren } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { ApiError, getAntiforgeryToken, getPlayerSession, sessionQueryKey } from '../api/api';
import type { PlayerSessionDto } from '../api/contracts';

interface AuthContextValue {
  session: PlayerSessionDto | null | undefined;
  loading: boolean;
  error: Error | null;
  sessionExpired: boolean;
  signIn: (email: string, password: string) => Promise<boolean>;
  signOut: () => Promise<void>;
  retrySession: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: PropsWithChildren) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const lastResumeCheck = useRef(0);
  const [sessionExpired, setSessionExpired] = useState(false);
  const sessionQuery = useQuery({
    queryKey: sessionQueryKey,
    queryFn: getPlayerSession,
    retry: false,
    staleTime: 30_000,
  });

  useEffect(() => {
    const expireSession = () => {
      // Keep the mounted page and its in-memory edits available while the
      // player decides how to recover. Protected writes are gated separately.
      setSessionExpired(true);
      queryClient.setQueryData(sessionQueryKey, null);
    };
    window.addEventListener('saturday-scout:unauthorized', expireSession);
    return () => window.removeEventListener('saturday-scout:unauthorized', expireSession);
  }, [queryClient]);

  useEffect(() => {
    let active = true;
    const revalidate = () => {
      if (document.visibilityState === 'hidden') return;
      const now = Date.now();
      // Focus, online, and visibility events often arrive together on mobile.
      if (now - lastResumeCheck.current < 15_000) return;
      lastResumeCheck.current = now;
      // Read directly first so the explicit expired state is committed before
      // replacing cached authenticated session data with null.
      void getPlayerSession()
        .then(session => {
          if (!active) return;
          if (!session) {
            setSessionExpired(true);
            queryClient.setQueryData(sessionQueryKey, null);
            return;
          }
          queryClient.setQueryData(sessionQueryKey, session);
          setSessionExpired(false);
          // Refresh authoritative reads only; never retry a write on resume.
          return Promise.all([
            queryClient.invalidateQueries({ queryKey: ['player-leagues'] }),
            queryClient.invalidateQueries({ queryKey: ['active-player-slate'] }),
            queryClient.invalidateQueries({ queryKey: ['player-pick-sheet'] }),
          ]);
        })
        .catch(() => {
          // A temporary network failure does not prove that the cookie expired.
        });
    };
    const onVisible = () => { if (document.visibilityState === 'visible') revalidate(); };
    window.addEventListener('focus', revalidate);
    window.addEventListener('online', revalidate);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      active = false;
      window.removeEventListener('focus', revalidate);
      window.removeEventListener('online', revalidate);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [queryClient]);

  const signIn = useCallback(async (email: string, password: string): Promise<boolean> => {
    const token = await getAntiforgeryToken();
    let response: Response;
    try {
      response = await fetch('/auth/login', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        // Follow the Identity endpoint's same-origin 302. In browsers, manual
        // redirects are opaque (status 0), even though the cookie was issued.
        redirect: 'follow',
        headers: {
          Accept: 'text/html',
          'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8',
          'X-CSRF-TOKEN': token,
        },
        body: new URLSearchParams({ email, password }),
      });
    } catch {
      throw new ApiError(null, 'Could not connect to Saturday Scout.');
    }

    if (response.status !== 302 && response.status !== 303 && !response.ok) {
      throw new ApiError(response.status, 'Unable to sign in. Check your email and password or try again later.');
    }

    const session = await getPlayerSession();
    if (!session) return false;
    setSessionExpired(false);
    queryClient.setQueryData(sessionQueryKey, session);
    await queryClient.invalidateQueries({ predicate: query => query.queryKey[0] !== sessionQueryKey[0] });
    return true;
  }, [queryClient]);

  const signOut = useCallback(async (): Promise<void> => {
    const token = await getAntiforgeryToken();
    let response: Response;
    try {
      response = await fetch('/auth/logout', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        redirect: 'follow',
        headers: { 'X-CSRF-TOKEN': token },
      });
    } catch {
      throw new ApiError(null, 'Could not connect to Saturday Scout.');
    }

    if (response.status !== 302 && response.status !== 303 && !response.ok) {
      throw new ApiError(response.status, 'Sign out could not be completed.');
    }
    queryClient.clear();
    queryClient.setQueryData(sessionQueryKey, null);
    setSessionExpired(false);
    navigate('/login', { replace: true });
  }, [navigate, queryClient]);

  const retrySession = useCallback(async () => {
    const result = await sessionQuery.refetch();
    if (result.data === null) setSessionExpired(true);
  }, [sessionQuery]);

  const value: AuthContextValue = {
    session: sessionQuery.data,
    loading: sessionQuery.isPending,
    error: sessionQuery.error,
    sessionExpired,
    signIn,
    signOut,
    retrySession,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used inside AuthProvider.');
  return context;
}

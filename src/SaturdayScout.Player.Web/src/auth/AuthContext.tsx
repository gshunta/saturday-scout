import { createContext, useCallback, useContext, useEffect, type PropsWithChildren } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { ApiError, getAntiforgeryToken, getPlayerSession, sessionQueryKey } from '../api/api';
import type { PlayerSessionDto } from '../api/contracts';

interface AuthContextValue {
  session: PlayerSessionDto | null | undefined;
  loading: boolean;
  error: Error | null;
  signIn: (email: string, password: string) => Promise<boolean>;
  signOut: () => Promise<void>;
  retrySession: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: PropsWithChildren) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const sessionQuery = useQuery({
    queryKey: sessionQueryKey,
    queryFn: getPlayerSession,
    retry: false,
    staleTime: 30_000,
  });

  useEffect(() => {
    const expireSession = () => {
      queryClient.removeQueries({ predicate: query => query.queryKey[0] !== sessionQueryKey[0] });
      queryClient.setQueryData(sessionQueryKey, null);
    };
    window.addEventListener('saturday-scout:unauthorized', expireSession);
    return () => window.removeEventListener('saturday-scout:unauthorized', expireSession);
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
    navigate('/login', { replace: true });
  }, [navigate, queryClient]);

  const retrySession = useCallback(async () => {
    await sessionQuery.refetch();
  }, [sessionQuery]);

  const value: AuthContextValue = {
    session: sessionQuery.data,
    loading: sessionQuery.isPending,
    error: sessionQuery.error,
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

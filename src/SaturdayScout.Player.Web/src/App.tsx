import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createBrowserRouter, Navigate, Route, RouterProvider, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { DashboardPage } from './pages/DashboardPage';
import { LeaguePage } from './pages/LeaguePage';
import { LoginPage } from './pages/LoginPage';
import { PicksPage } from './pages/PicksPage';
import { ProfilePage } from './pages/ProfilePage';
import { StandingsPage } from './pages/StandingsPage';
import './styles.css';

export function PlayerRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<ProtectedRoute />}>
        <Route index element={<DashboardPage />} />
        <Route path="standings" element={<StandingsPage />} />
        <Route path="league" element={<LeaguePage />} />
        <Route path="picks" element={<PicksPage />} />
        <Route path="profile" element={<ProfilePage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export function App() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false, staleTime: 30_000 },
    },
  });

  const router = createBrowserRouter([
    { path: '*', element: <AuthProvider><PlayerRoutes /></AuthProvider> },
  ], { basename: '/react' });

  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

import { Component, type ErrorInfo, type ReactNode } from 'react';

interface AppErrorBoundaryProps {
  children: ReactNode;
}

interface AppErrorBoundaryState {
  hasError: boolean;
}

export class AppErrorBoundary extends Component<AppErrorBoundaryProps, AppErrorBoundaryState> {
  override state: AppErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(): AppErrorBoundaryState {
    return { hasError: true };
  }

  override componentDidCatch(_error: Error, _info: ErrorInfo): void {
    // Keep exception details out of the UI and browser console; telemetry can be
    // connected later through an approved, privacy-reviewed error reporting path.
  }

  override render() {
    if (this.state.hasError) {
      return (
        <main className="auth-state" role="alert">
          <h1>Saturday Scout is having trouble</h1>
          <p>We couldn’t display this page. Reload to try again.</p>
          <button className="button button-primary" onClick={() => window.location.reload()}>
            Reload
          </button>
        </main>
      );
    }

    return this.props.children;
  }
}

import { Component, type ErrorInfo, type ReactNode } from 'react';

type Props = { children: ReactNode };
type State = { error: Error | null };

/**
 * A React error boundary catches render / lifecycle exceptions and shows an
 * actionable diagnostic instead of leaving the user with a blank background.
 * It does not replace normal API error handling.
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('DeveloperTwin frontend crashed:', error, info.componentStack);
  }

  render(): ReactNode {
    if (!this.state.error) return this.props.children;
    return (
      <div className="runtime-error" role="alert">
        <div className="runtime-error-card">
          <h1>DeveloperTwin encountered a frontend error</h1>
          <p>The backend may still be running. Check the browser Console (F12) for the full stack trace.</p>
          <pre>{this.state.error.message}</pre>
          <div className="runtime-error-actions">
            <button onClick={() => window.location.reload()}>Reload application</button>
            <button onClick={() => {
              if (!window.confirm('Clear only DeveloperTwin v4 saved conversations?')) return;
              try {
                localStorage.removeItem('developertwin-v4-chat-demo');
                localStorage.removeItem('developertwin-v4-active');
              } catch (error) { console.warn('Could not clear browser storage:', error); }
              window.location.reload();
            }}>Clear saved conversations and reload</button>
          </div>
        </div>
      </div>
    );
  }
}

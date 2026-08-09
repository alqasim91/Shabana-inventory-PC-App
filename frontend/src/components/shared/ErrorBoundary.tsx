import { Component, type ErrorInfo, type ReactNode } from 'react';
import { ERROR_BOUNDARY, APP_NAME } from '@/labels';

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: Error | null;
}

/** Last-resort fallback for render errors anywhere in the tree — Arabic, RTL, matches the app shell. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('ErrorBoundary caught:', error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;

    return (
      <div dir="rtl" className="flex min-h-screen items-center justify-center bg-sand px-4">
        <div className="w-full max-w-md rounded-card border border-border bg-white p-8 text-center">
          <div className="mb-1 text-[15px] font-bold text-teal-dark">{APP_NAME}</div>
          <h1 className="mb-2 text-lg font-bold text-ink">{ERROR_BOUNDARY.title}</h1>
          <p className="mb-6 text-[13.5px] leading-relaxed text-muted">{ERROR_BOUNDARY.message}</p>
          <div className="flex flex-col gap-2.5">
            <button
              onClick={() => window.location.reload()}
              className="rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover"
            >
              {ERROR_BOUNDARY.reload}
            </button>
            <button
              onClick={() => {
                window.location.href = '/dashboard';
              }}
              className="rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
            >
              {ERROR_BOUNDARY.backToDashboard}
            </button>
          </div>
        </div>
      </div>
    );
  }
}

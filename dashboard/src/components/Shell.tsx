import { Suspense, type ReactNode } from 'react';
import { Nav } from './Nav';
import { authState } from '@/lib/auth';

/**
 * The chrome every page shares.
 *
 * Mark, wordmark and page tabs all sit together on the left, so the eye lands
 * on identity and navigation in one place. Sign-out, when there is one, is
 * pushed to the far right because it is a control rather than a label.
 *
 * No range selector here: the only thing a window changes is the daily peak
 * chart, so the dropdown lives on that chart instead.
 */
export function Shell({ children }: { children: ReactNode }) {
  // Only shown when a password is actually set. With the dashboard open, a
  // "Sign out" that signs you out of nothing is a control that lies.
  const guarded = authState().mode === 'password';

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          {/*
            Served by the app router from src/app/icon.svg, the same file the
            browser tab uses, so the mark cannot drift between the two and there
            is no third copy of the artwork to keep in step.
          */}
          <img src="/icon.svg" alt="" width={22} height={22} className="brand-mark" />
          <strong>Speed Meter</strong>
        </div>

        <Suspense fallback={null}>
          <Nav />
        </Suspense>

        {guarded && (
          <div className="topbar-end">
            <form method="post" action="/api/auth/logout">
              <button type="submit" className="tab tab--action">
                Sign out
              </button>
            </form>
          </div>
        )}
      </header>
      <main className="content">{children}</main>
    </div>
  );
}

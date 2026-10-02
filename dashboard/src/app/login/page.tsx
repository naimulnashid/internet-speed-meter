import type { Metadata } from 'next';
import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import { ENV_VAR, MIN_LENGTH, SESSION_COOKIE, authState, safeNext, sessionValid } from '@/lib/auth';

export const dynamic = 'force-dynamic';

export const metadata: Metadata = { title: 'Sign in' };

const MESSAGES: Record<string, string> = {
  bad: 'That is not the password.',
  empty: 'Enter the password.',
  wait: 'Too many attempts. Wait a moment and try again.',
  broken: 'The password is not usable. See below.',
};

/**
 * The sign-in screen, and the only page the gate lets through unauthenticated.
 *
 * It also has to explain the two states that are not "type your password": no
 * password set at all, and one set to something unusable. Both are reached by
 * someone who has just followed a redirect here and needs to be told what to do
 * next, so neither can be a bare error.
 *
 * Plain form POST rather than a client component. The page has one control, it
 * should work before React has hydrated, and a password field is the last place
 * to introduce a code path that can be half-loaded.
 */
export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ next?: string; error?: string }>;
}) {
  const sp = await searchParams;
  const next = safeNext(sp.next);
  const state = authState();

  if (state.mode === 'open') {
    return (
      <div className="gate">
        <div className="gate-card">
          <Brand />
          <h1 className="gate-title">No password is set</h1>
          <p className="gate-sub">
            This dashboard is open to anything that can reach <code>localhost:7845</code> — which
            on a single-user machine is only you, and is why it ships this way.
          </p>
          <p className="gate-sub">
            To require one, put a line in <code>dashboard\.env.local</code>:
          </p>
          <p className="gate-command">
            <code>{ENV_VAR}=your-password-here</code>
          </p>
          <Restart lead="Then restart the dashboard, which reads that file only at boot:" />
          <p className="gate-foot">
            <a href="/">Go to the dashboard</a>
          </p>
        </div>
      </div>
    );
  }

  if (state.mode === 'misconfigured') {
    return (
      <div className="gate">
        <div className="gate-card">
          <Brand />
          <h1 className="gate-title">The password is not usable</h1>
          <p className="gate-sub">
            One is set, so the dashboard has stayed shut rather than assume it was meant to be
            open — but it will not do the job it was set to do.
          </p>
          <p className="gate-error">{state.reason}</p>
          <p className="gate-sub">
            Nothing here slows a guess down but the attempt limit, so the length is the whole
            defence. Edit <code>dashboard\.env.local</code>, then restart:
          </p>
          <Restart />
        </div>
      </div>
    );
  }

  // Already signed in and typing /login by hand. Sending them to the form would
  // ask for a password they have already given.
  const token = (await cookies()).get(SESSION_COOKIE)?.value;
  if (sessionValid(state.password, token)) redirect(next);

  const message = sp.error ? MESSAGES[sp.error] ?? MESSAGES['bad'] : null;

  return (
    <div className="gate">
      <form className="gate-card" method="post" action="/api/auth/login">
        <Brand />
        <h1 className="gate-title">Sign in</h1>
        <p className="gate-sub">This dashboard is password protected.</p>

        <input type="hidden" name="next" value={next} />

        <label className="gate-label" htmlFor="password">
          Password
        </label>
        {/* eslint-disable-next-line jsx-a11y/no-autofocus -- one field, one purpose. */}
        <input
          id="password"
          className="gate-input"
          type="password"
          name="password"
          autoComplete="current-password"
          autoFocus
          required
        />

        {message && (
          <p className="gate-error" role="alert">
            {message}
          </p>
        )}

        <button type="submit" className="chip chip--go gate-submit">
          Sign in
        </button>

        <p className="gate-foot">
          Forgotten it? It is in plain text in <code>dashboard\.env.local</code> on this machine,
          under <code>{ENV_VAR}</code>. At least {MIN_LENGTH} characters. The recorded history is
          untouched either way.
        </p>
      </form>
    </div>
  );
}

function Brand() {
  return (
    <div className="gate-brand">
      {/* The same src/app/icon.svg the tab and the top bar use. */}
      <img src="/icon.svg" alt="" width={26} height={26} className="brand-mark" />
      <strong>Speed Meter</strong>
    </div>
  );
}

/**
 * Next reads .env.local once, when the server boots, so a change to it is not
 * live the way the rest of this dashboard's settings are. The dashboard runs
 * hidden from a logon task and has no window to Ctrl+C, which makes "restart
 * it" two specific commands rather than an instruction.
 */
function Restart({ lead }: { lead?: string }) {
  return (
    <>
      {lead && <p className="gate-sub">{lead}</p>}
      <p className="gate-command">
        <code>powershell -ExecutionPolicy Bypass -File scripts\dashboard-stop.ps1</code>
      </p>
      <p className="gate-command">
        <code>wscript scripts\dashboard-hidden.vbs</code>
      </p>
    </>
  );
}

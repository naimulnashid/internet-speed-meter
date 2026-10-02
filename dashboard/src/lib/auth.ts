/**
 * The optional site password.
 *
 * Off by default, and deliberately so: a dashboard bound to localhost on a
 * single-user machine gains nothing from a login it has to be told about. It
 * earns its keep the moment the port is reachable by anything else -- a shared
 * PC, a second account, a tunnel or a port forward opened for a phone -- which
 * is exactly when nobody remembers to add one.
 *
 * Set in `dashboard/.env.local`, which .gitignore covers along with every other
 * `.env` shape, because the password is in that file in plain text:
 *
 *     DASHBOARD_PASSWORD=something-long
 *
 * The name has no NEXT_PUBLIC_ prefix, and must never get one. That prefix is
 * the switch that inlines a value into the browser bundle, which for this
 * particular value would publish it to exactly the people it is keeping out.
 *
 * Read per request rather than captured at module load, so nothing here is
 * baked into the build output. It still takes a restart to change, because
 * Next reads .env.local once when the server boots -- see the README for the
 * two commands.
 *
 * No `server-only` import, unlike the rest of lib/. Middleware is bundled
 * without the react-server condition, so that package resolves to the module
 * that throws and takes the whole gate down with it. Nothing here is importable
 * from a client component anyway -- it is all node:crypto.
 */

import { createHash, createHmac, pbkdf2Sync, timingSafeEqual } from 'node:crypto';

export const SESSION_COOKIE = 'speedmeter_session';

/**
 * How long a sign-in lasts.
 *
 * Long, because the threat this defends against is a second person reaching
 * the port, not a stolen browser profile -- and a dashboard that asks for a
 * password every morning is a dashboard whose password ends up on a sticky
 * note. Changing the password ends every existing session regardless: the
 * signing key is derived from it.
 */
export const SESSION_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

/**
 * Nothing slows a guess down now that the stored value is the password itself
 * rather than something expensive to derive from it. The attempt throttle in
 * the login route is the whole defence, so the password has to carry its own
 * weight -- a four-character one would fall to it inside an afternoon.
 */
export const MIN_LENGTH = 8;

/**
 * `open` is no password configured, and means the dashboard behaves exactly as
 * it did before this existed.
 *
 * `misconfigured` is a password that is set but unusable. That has to lock the
 * site rather than fall back to `open`: a typo quietly disabling the gate would
 * be the one failure mode nobody would notice.
 */
export type AuthState =
  | { mode: 'open' }
  | { mode: 'misconfigured'; reason: string }
  | { mode: 'password'; password: string };

export const ENV_VAR = 'DASHBOARD_PASSWORD';

export function authState(): AuthState {
  // Bracket access, and inside the function. A bare `process.env.NAME` at
  // module scope is the shape build tooling substitutes a literal for, which
  // would freeze whatever was in .env.local at build time into the output.
  const raw = process.env[ENV_VAR];

  // Unset and empty are the same intent: no password wanted. An .env.local
  // holding `DASHBOARD_PASSWORD=` is how you switch it off without deleting
  // the line.
  if (raw === undefined || raw.trim() === '') return { mode: 'open' };

  // Not trimmed. A trailing space is part of a password someone may have meant,
  // and silently dropping it would reject the password they think they set.
  if (raw.length < MIN_LENGTH) {
    return {
      mode: 'misconfigured',
      reason: `${ENV_VAR} is only ${raw.length} characters. Use at least ${MIN_LENGTH}.`,
    };
  }

  return { mode: 'password', password: raw };
}

/**
 * Constant-time, and over digests rather than the passwords themselves so the
 * comparison cannot leak the length of the real one through how long it takes.
 */
export function passwordMatches(expected: string, offered: string): boolean {
  const a = createHash('sha256').update(offered, 'utf8').digest();
  const b = createHash('sha256').update(expected, 'utf8').digest();
  return timingSafeEqual(a, b);
}

/**
 * OWASP's current floor for PBKDF2-HMAC-SHA256. It runs once per process, not
 * per request (see `sessionKey`), and the same cost lands on every guess made
 * offline against a captured cookie.
 */
const KDF_ITERATIONS = 600_000;

/** Bump to sign every browser out if the derivation ever changes again. */
const KDF_SALT_PREFIX = 'speedmeter-session-v2';

/** Optional high-entropy secret mixed into the key; empty when unset. */
function sessionSecret(): string {
  return process.env.SESSION_SECRET?.trim() ?? '';
}

const derivedKeys = new Map<string, Buffer>();

/**
 * The cookie signing key, derived from the password rather than stored
 * anywhere. It means changing the password signs every browser out, with
 * nothing having to keep track of who was signed in.
 *
 * DERIVED SLOWLY, because of network access. The key was once one HMAC of the
 * password, which is fine on localhost but not on a LAN: the cookie crosses
 * plain HTTP, and anyone who captured one could test password guesses offline
 * at full HMAC speed without ever touching the login throttle. So the key goes
 * through PBKDF2, and SESSION_SECRET, when set, is mixed into the salt - a
 * captured cookie is then useless without it, however weak the password. The
 * same approach as the AI Usage dashboard's gate.
 *
 * Cached per (secret, password), so only the first request after a restart
 * pays for it.
 */
function sessionKey(password: string): Buffer {
  const secret = sessionSecret();
  // Length-prefixed so no (secret, password) pair can collide with another.
  const cacheKey = `${secret.length}:${secret}${password}`;
  let key = derivedKeys.get(cacheKey);
  if (!key) {
    key = pbkdf2Sync(password, `${KDF_SALT_PREFIX}:${secret}`, KDF_ITERATIONS, 32, 'sha256');
    // A rotated password or secret makes the old entry useless; keep one.
    derivedKeys.clear();
    derivedKeys.set(cacheKey, key);
  }
  return key;
}

function sign(password: string, payload: string): string {
  return createHmac('sha256', sessionKey(password)).update(payload).digest('base64url');
}

/**
 * A session is its own expiry, signed. Nothing is kept server-side, because
 * there is exactly one account and a server-side table would only add a thing
 * to lose on restart.
 */
export function issueSession(password: string, now: number = Date.now()): string {
  const payload = String(now + SESSION_MAX_AGE_SECONDS * 1000);
  return `${payload}.${sign(password, payload)}`;
}

export function sessionValid(
  password: string, token: string | undefined, now: number = Date.now(),
): boolean {
  if (!token) return false;

  const dot = token.lastIndexOf('.');
  if (dot <= 0) return false;

  const payload = token.slice(0, dot);
  const offered = Buffer.from(token.slice(dot + 1), 'base64url');
  const expected = Buffer.from(sign(password, payload), 'base64url');

  // Length first: timingSafeEqual throws rather than returning false on a
  // mismatch, and a thrown error inside the gate is a 500, not a refusal.
  if (offered.length !== expected.length || !timingSafeEqual(offered, expected)) return false;

  const expires = Number(payload);
  return Number.isFinite(expires) && expires > now;
}

/**
 * Where to land after signing in.
 *
 * The gate puts the page you asked for in the query string, which makes it
 * attacker-controlled: anything not a plain path on this site is discarded
 * rather than followed, so the login form cannot be turned into a redirector.
 */
export function safeNext(value: string | null | undefined): string {
  if (typeof value !== 'string' || !value.startsWith('/')) return '/';
  // `//host` and `/\host` are both protocol-relative once a browser sees them.
  if (value.startsWith('//') || value.startsWith('/\\')) return '/';
  if (value === '/login' || value.startsWith('/login?')) return '/';
  return value;
}

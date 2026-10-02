import { NextResponse } from 'next/server';
import {
  SESSION_COOKIE, SESSION_MAX_AGE_SECONDS,
  authState, issueSession, passwordMatches, safeNext,
} from '@/lib/auth';

export const dynamic = 'force-dynamic';

/**
 * Attempt throttling.
 *
 * This is the only thing standing between the form and an unlimited guessing
 * loop: the password is compared to a stored string, so a wrong answer costs a
 * hash and nothing else. Five free attempts, then a wait that grows by five
 * seconds each time and stops at five minutes -- which caps a determined script
 * at a handful of guesses a minute, and is why MIN_LENGTH exists.
 *
 * In memory, and reset by a restart. Not the hole it looks like: an attacker
 * who can restart this server already has the machine.
 *
 * Not keyed by client address on purpose. There is one account, so a per-caller
 * bucket would only let an attacker spread guesses across spoofed X-Forwarded
 * headers and get more attempts than a single honest user.
 */
let failures = 0;
let blockedUntil = 0;

const FREE_ATTEMPTS = 5;
const BLOCK_STEP_MS = 5_000;
const BLOCK_CEILING_MS = 5 * 60 * 1_000;

function refuse(request: Request, next: string, error: string) {
  const url = new URL('/login', request.url);
  if (next !== '/') url.searchParams.set('next', next);
  url.searchParams.set('error', error);
  // 303, so the browser turns this POST into a GET and a reload of the login
  // page does not re-submit the password.
  return NextResponse.redirect(url, 303);
}

export async function POST(request: Request) {
  const form = await request.formData().catch(() => null);

  const rawNext = form?.get('next');
  const next = safeNext(typeof rawNext === 'string' ? rawNext : null);

  const state = authState();
  if (state.mode === 'open') {
    // No password to give. Nothing to do but let them in.
    return NextResponse.redirect(new URL(next, request.url), 303);
  }
  if (state.mode === 'misconfigured') return refuse(request, next, 'broken');

  if (Date.now() < blockedUntil) return refuse(request, next, 'wait');

  const rawPassword = form?.get('password');
  const password = typeof rawPassword === 'string' ? rawPassword : '';
  if (!password) return refuse(request, next, 'empty');

  if (!passwordMatches(state.password, password)) {
    failures += 1;
    if (failures > FREE_ATTEMPTS) {
      const wait = Math.min(BLOCK_STEP_MS * (failures - FREE_ATTEMPTS), BLOCK_CEILING_MS);
      blockedUntil = Date.now() + wait;
    }
    return refuse(request, next, 'bad');
  }

  failures = 0;
  blockedUntil = 0;

  const response = NextResponse.redirect(new URL(next, request.url), 303);
  response.cookies.set({
    name: SESSION_COOKIE,
    value: issueSession(state.password),
    httpOnly: true,
    sameSite: 'lax',
    path: '/',
    maxAge: SESSION_MAX_AGE_SECONDS,
    // No `secure`. This is served over plain HTTP on localhost, and a Secure
    // cookie there is one the browser sets and never sends back.
  });
  return response;
}

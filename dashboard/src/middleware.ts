import { NextResponse, type NextRequest } from 'next/server';
import { SESSION_COOKIE, authState, sessionValid } from '@/lib/auth';

/**
 * The one place the site is closed.
 *
 * Deliberately middleware rather than a check in each page: pages get added,
 * and a gate you have to remember to fit is a gate that is eventually missing
 * from the one route that mattered. Everything is shut by default here, and the
 * short list below is what has to stay open for signing in to be possible.
 *
 * Node runtime, not the edge default. The edge one inlines process.env into
 * the build, which would freeze whatever .env.local held when the build ran.
 */
export const runtime = 'nodejs';

/**
 * `/icon.svg` is on the list only because the sign-in page wears the mark, and
 * a piece of artwork is not worth a login. It reveals nothing about the
 * history.
 */
const OPEN_PATHS = new Set(['/login', '/api/auth/login', '/api/auth/logout', '/icon.svg']);

export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  if (OPEN_PATHS.has(pathname)) return NextResponse.next();

  const state = authState();
  if (state.mode === 'open') return NextResponse.next();

  if (
    state.mode === 'password' &&
    sessionValid(state.password, request.cookies.get(SESSION_COOKIE)?.value)
  ) {
    return NextResponse.next();
  }

  // A fetch from an already-open page must not be answered with the HTML of the
  // login screen: the caller would parse it as its own JSON and report a
  // malformed feed rather than an expired session.
  if (pathname.startsWith('/api/')) {
    return NextResponse.json({ error: 'Not signed in.' }, { status: 401 });
  }

  const url = request.nextUrl.clone();
  url.pathname = '/login';
  url.search = '';

  const wanted = pathname + request.nextUrl.search;
  if (wanted !== '/') url.searchParams.set('next', wanted);

  return NextResponse.redirect(url);
}

export const config = {
  // Static build output carries no history and is fetched on every page, so it
  // is matched out here rather than checked and waved through on each request.
  matcher: ['/((?!_next/static|_next/image).*)'],
};

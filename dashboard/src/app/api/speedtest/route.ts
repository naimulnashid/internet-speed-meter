import { NextResponse } from 'next/server';
import { appendResult, parseResult, readResults } from '@/lib/speedtest';

export const dynamic = 'force-dynamic';

export async function GET() {
  return NextResponse.json({ results: readResults() });
}

/**
 * Records a finished run.
 *
 * The measuring happens in the browser, because the transfer has to traverse
 * the same path the user actually browses over; running it server-side would
 * measure the same machine by a different route and prove nothing extra. This
 * endpoint only persists what the browser measured, after validating it.
 */
export async function POST(request: Request) {
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ ok: false, error: 'Malformed body.' }, { status: 400 });
  }

  const result = parseResult(body);
  if (!result) {
    return NextResponse.json({ ok: false, error: 'Result failed validation.' }, { status: 400 });
  }

  const written = appendResult(result);
  if (!written.ok) {
    return NextResponse.json({ ok: false, error: written.error }, { status: 500 });
  }

  return NextResponse.json({ ok: true, result });
}

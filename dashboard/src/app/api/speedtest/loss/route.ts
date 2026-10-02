import { NextResponse } from 'next/server';
import { markPinger, startPinger, stopPinger, type LossPhase } from '@/lib/pinger';

export const dynamic = 'force-dynamic';

/**
 * Drives the packet-loss pinger for a test running in the browser.
 *
 * One route for all three actions, so they share a single copy of the
 * pinger module and its sessions. `start` begins counting as idle, `mark`
 * moves the count to the leg the browser has just entered, and `stop` returns
 * the tally per leg.
 */
export async function POST(request: Request) {
  let body: Record<string, unknown>;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ ok: false, error: 'Malformed body.' }, { status: 400 });
  }

  const id = typeof body['id'] === 'string' ? body['id'] : '';

  switch (body['action']) {
    case 'start':
      return NextResponse.json(await startPinger());

    case 'mark': {
      const phase = body['phase'];
      if (phase !== 'idle' && phase !== 'down' && phase !== 'up') {
        return NextResponse.json({ ok: false, error: 'Unknown phase.' }, { status: 400 });
      }
      return NextResponse.json({ ok: markPinger(id, phase as LossPhase) });
    }

    case 'stop': {
      const loss = await stopPinger(id);
      return NextResponse.json(loss ? { ok: true, loss } : { ok: false, error: 'No such pinger.' });
    }

    default:
      return NextResponse.json({ ok: false, error: 'Unknown action.' }, { status: 400 });
  }
}

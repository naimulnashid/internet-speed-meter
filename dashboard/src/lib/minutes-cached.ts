import 'server-only';
import { refresh, queryMinutes } from './cache';
import type { MinuteRecord } from './logs';

/**
 * The cached path for `readMinutes`.
 *
 * Separate from logs.ts to keep the dependency one-way: the cache is built from
 * the file reader, so the file reader must not import the cache. A cycle here
 * would be the kind that only shows up as an undefined at runtime.
 */

function localDate(d: Date): string {
  return (
    `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-` +
    `${String(d.getDate()).padStart(2, '0')}`
  );
}

export function readMinutesCached(from: Date, to: Date): MinuteRecord[] | null {
  // Fold in anything appended since the last request. With nothing new this is
  // one stat per month file, so it is cheap enough to do on every read and
  // removes any question of the cache being stale.
  refresh();

  const rows = queryMinutes(localDate(from), localDate(to));
  if (!rows) return null;

  return rows.map((r) => ({
    time: r.unix_minute * 60_000,
    downBytes: r.down_bytes,
    upBytes: r.up_bytes,
    maxDownBps: r.max_down_bps,
    maxUpBps: r.max_up_bps,
    samples: r.samples,
    activeSamples: r.active_samples,
    adapter: r.adapter,
    flags: r.flags,
  }));
}

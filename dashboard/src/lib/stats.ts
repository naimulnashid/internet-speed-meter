import 'server-only';
import type { MinuteRecord } from './logs';

/**
 * Aggregates over minute records.
 *
 * Only what minute rollups can honestly answer. Sustained averages shorter than
 * a minute need the individual seconds and come from the raw log instead.
 */

export interface Peak {
  bytesPerSecond: number;
  /** When, as epoch ms. Null when nothing was ever recorded. */
  time: number | null;
}

/** The fastest single second each way, from the max each minute carries. */
export function peaks(records: MinuteRecord[]): { down: Peak; up: Peak } {
  const down: Peak = { bytesPerSecond: 0, time: null };
  const up: Peak = { bytesPerSecond: 0, time: null };

  for (const r of records) {
    if (r.maxDownBps > down.bytesPerSecond) {
      down.bytesPerSecond = r.maxDownBps;
      down.time = r.time;
    }
    if (r.maxUpBps > up.bytesPerSecond) {
      up.bytesPerSecond = r.maxUpBps;
      up.time = r.time;
    }
  }

  return { down, up };
}

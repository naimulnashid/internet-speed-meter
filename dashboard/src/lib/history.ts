import 'server-only';
import type { MinuteRecord } from './logs';

/**
 * Day-shaped views over minute records.
 *
 * Everything here buckets by LOCAL time, computed from the stored UTC stamp at
 * read time. Bucketing off UTC would shift every early-morning hour onto the
 * previous day at UTC+6 — the same trap the Data Usage Tracker's schema
 * comments record, arrived at from the other direction.
 */

export interface DayStats {
  /** Local midnight, epoch ms — the key and the x value. */
  day: number;
  peakDownBps: number;
  peakUpBps: number;
}

function dayKey(time: number): number {
  const d = new Date(time);
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}

export function byDay(records: MinuteRecord[]): DayStats[] {
  const days = new Map<number, DayStats>();

  for (const r of records) {
    const key = dayKey(r.time);
    let day = days.get(key);
    if (!day) {
      day = { day: key, peakDownBps: 0, peakUpBps: 0 };
      days.set(key, day);
    }

    if (r.maxDownBps > day.peakDownBps) day.peakDownBps = r.maxDownBps;
    if (r.maxUpBps > day.peakUpBps) day.peakUpBps = r.maxUpBps;
  }

  return [...days.values()].sort((a, b) => a.day - b.day);
}

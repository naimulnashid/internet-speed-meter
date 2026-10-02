import 'server-only';
import { FLAG_GAP, forEachRaw } from './logs';

/**
 * Statistics that need the individual seconds.
 *
 * A best 10-second average cannot be computed from minute rollups: a minute
 * carries a max and a sum, and neither says how the bytes were spread inside
 * it. Anything here is therefore limited to the days still inside the raw
 * retention window.
 *
 * Everything is computed in ONE streaming pass, holding no more than a day's
 * buffer plus a few hundred numbers. The retention window guarantees the input
 * reaches 1,209,600 records within a fortnight of continuous recording, so
 * collecting them first -- or walking them once per statistic -- has a ceiling
 * that arrives on a schedule rather than by bad luck.
 */

/** Windows to report a best sustained average over, in seconds. */
const WINDOWS = [10, 60, 300] as const;

export interface Sustained {
  /** Window length in seconds. */
  window: number;
  bytesPerSecond: number;
  time: number | null;
}

export interface RawStats {
  /** True when there were no raw records at all in the window. */
  empty: boolean;

  /** Best rolling average over 10 s, 60 s and 5 min, download direction. */
  sustainedDown: Sustained[];
}

/**
 * A fixed-length rolling mean over consecutive seconds.
 *
 * Ring buffers rather than a re-scan, so all three window lengths advance
 * together in the single pass. `reset` is called at every discontinuity -- a
 * break in the second-by-second sequence, or a flagged sleep gap -- because
 * averaging across one would divide real bytes by a span that was never
 * metered, and report a peak that did not happen.
 */
class Rolling {
  private readonly values: Float64Array;
  private readonly times: Float64Array;
  private sum = 0;
  private filled = 0;
  private head = 0;

  best = 0;
  bestTime: number | null = null;

  constructor(readonly window: number) {
    this.values = new Float64Array(window);
    this.times = new Float64Array(window);
  }

  reset(): void {
    this.sum = 0;
    this.filled = 0;
    this.head = 0;
  }

  push(value: number, time: number): void {
    if (this.filled === this.window) {
      this.sum -= this.values[this.head]!;
    } else {
      this.filled++;
    }

    this.values[this.head] = value;
    this.times[this.head] = time;
    this.sum += value;
    this.head = (this.head + 1) % this.window;

    if (this.filled === this.window) {
      const average = this.sum / this.window;
      if (average > this.best) {
        this.best = average;
        // head now points at the oldest entry, which is where this window began.
        this.bestTime = this.times[this.head]!;
      }
    }
  }

  result(): Sustained {
    return { window: this.window, bytesPerSecond: this.best, time: this.bestTime };
  }
}

export function rawStats(from: Date, to: Date): RawStats {
  const rolling = WINDOWS.map((w) => new Rolling(w));

  let seen = 0;
  let previousTime = 0;

  forEachRaw(from, to, (time, downBytes, _upBytes, elapsedMs, _adapter, flags) => {
    seen++;

    const broken = previousTime !== 0 && time - previousTime > 1500;
    previousTime = time;

    if ((flags & FLAG_GAP) !== 0) {
      for (const r of rolling) r.reset();
      return;
    }

    if (broken) {
      for (const r of rolling) r.reset();
    }

    // Rate, not the byte delta: a window is nominally but not exactly a second,
    // and the whole page is about speed.
    const seconds = elapsedMs > 0 ? elapsedMs / 1000 : 1;
    for (const r of rolling) r.push(downBytes / seconds, time);
  });

  return {
    empty: seen === 0,
    sustainedDown: rolling.map((r) => r.result()),
  };
}

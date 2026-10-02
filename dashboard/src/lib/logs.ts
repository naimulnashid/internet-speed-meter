import 'server-only';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { meterSettings } from './settings';
import { readMinutesCached } from './minutes-cached';

/**
 * Reads the binary logs written by src/Recorder.cs.
 *
 * The format is documented in docs/dashboard-plan.md and defined by the writer;
 * everything here mirrors it and must not drift:
 *
 *   raw    16 bytes  u32 unixSeconds, u32 down, u32 up, u16 elapsedMs, u8 adapter, u8 flags
 *   minute 40 bytes  u32 unixMinute, u64 down, u64 up, u32 maxDownBps, u32 maxUpBps,
 *                    u16 samples, u16 activeSamples, u8 adapter, u8 flags, 6 reserved
 *
 * Little-endian throughout, stated by the writer rather than inherited from it.
 */

export const RAW_RECORD_BYTES = 16;
export const MINUTE_RECORD_BYTES = 40;

export const FLAG_CONNECTED = 1;
export const FLAG_GAP = 2;
export const FLAG_ADAPTER_CHANGED = 4;

/**
 * Minute records only: the meter started partway through this minute.
 *
 * The one thing a reader cannot work out for itself. A partial minute at the
 * edge of a recording gap is obvious; one where the meter stopped and started
 * again inside a single minute is not -- both neighbours are present and the
 * sample count is simply low, which is indistinguishable from dropped ticks.
 */
export const FLAG_METER_STARTED = 8;

export interface MinuteRecord {
  /** Start of the minute, as epoch milliseconds. */
  time: number;
  downBytes: number;
  upBytes: number;
  /** Peak one-second rate within the minute, bytes/s. The reason minutes suffice. */
  maxDownBps: number;
  maxUpBps: number;
  /** How many of the 60 seconds the meter was actually running for. */
  samples: number;
  activeSamples: number;
  adapter: number;
  flags: number;
}

/**
 * u64 via two u32 reads.
 *
 * `getBigUint64` would be exact but returns a BigInt, which cannot be summed
 * with the rest of the arithmetic here without casting at every use. A minute
 * of traffic is at most a few GB, far inside the 2^53 a double holds exactly,
 * so the precision this gives up does not exist in practice.
 */
function readU64(view: DataView, at: number): number {
  return view.getUint32(at, true) + view.getUint32(at + 4, true) * 0x1_0000_0000;
}

/** Files whose name-date can overlap [from, to], oldest first. */
function filesInRange(folder: string, monthly: boolean, from: Date, to: Date): string[] {
  let names: string[];
  try {
    names = readdirSync(folder);
  } catch {
    // No folder is the normal state before the meter has ever recorded, and an
    // empty history is a thing the pages render rather than an error.
    return [];
  }

  const pattern = monthly ? /^(\d{4})-(\d{2})\.bin$/ : /^(\d{4})-(\d{2})-(\d{2})\.bin$/;
  const fromDay = new Date(from.getFullYear(), from.getMonth(), from.getDate()).getTime();
  const toDay = new Date(to.getFullYear(), to.getMonth(), to.getDate()).getTime();

  const keep: { path: string; start: number }[] = [];
  for (const name of names) {
    const m = pattern.exec(name);
    if (!m) continue;

    const year = Number(m[1]);
    const month = Number(m[2]) - 1;
    const day = monthly ? 1 : Number(m[3]);

    const start = new Date(year, month, day).getTime();
    // A month file is in range if the range touches it anywhere; the per-record
    // filter does the exact trimming afterwards.
    const end = monthly ? new Date(year, month + 1, 0).getTime() : start;
    if (end < fromDay || start > toDay) continue;

    keep.push({ path: join(folder, name), start });
  }

  keep.sort((a, b) => a.start - b.start);
  return keep.map((k) => k.path);
}

function view(path: string): DataView | null {
  try {
    const buffer = readFileSync(path);
    return new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  } catch {
    return null;
  }
}

/**
 * Minute records covering [from, to] inclusive by local date.
 *
 * Sorted rather than trusted: the writer appends in real time, so records are
 * normally already ordered, but a backwards clock step breaks that and every
 * chart downstream assumes a monotonic x-axis.
 */
export function readMinutes(from: Date, to: Date): MinuteRecord[] {
  // The cache answers this from an index instead of parsing and sorting every
  // month file. It returns null whenever it cannot -- unavailable, unreadable,
  // mid-rebuild -- and the direct read below is then used unchanged, so a bad
  // cache costs speed and never correctness.
  const cached = readMinutesCached(from, to);
  if (cached) return cached;

  return readMinutesFromFiles(from, to);
}

/** The authority. Parses the .bin files; the cache is built from exactly this. */
export function readMinutesFromFiles(from: Date, to: Date): MinuteRecord[] {
  const { logFolder } = meterSettings();
  const out: MinuteRecord[] = [];

  const fromMs = new Date(from.getFullYear(), from.getMonth(), from.getDate()).getTime();
  const toMs = new Date(to.getFullYear(), to.getMonth(), to.getDate() + 1).getTime();

  for (const path of filesInRange(logFolder, true, from, to)) {
    const v = view(path);
    if (!v) continue;

    const count = Math.floor(v.byteLength / MINUTE_RECORD_BYTES);
    for (let i = 0; i < count; i++) {
      const at = i * MINUTE_RECORD_BYTES;
      const time = v.getUint32(at, true) * 60_000;
      if (time < fromMs || time >= toMs) continue;

      out.push({
        time,
        downBytes: readU64(v, at + 4),
        upBytes: readU64(v, at + 12),
        maxDownBps: v.getUint32(at + 20, true),
        maxUpBps: v.getUint32(at + 24, true),
        samples: v.getUint16(at + 28, true),
        activeSamples: v.getUint16(at + 30, true),
        adapter: v.getUint8(at + 32),
        flags: v.getUint8(at + 33),
      });
    }
  }

  out.sort((a, b) => a.time - b.time);
  return out;
}

/**
 * Visits raw records in time order without ever holding them all.
 *
 * Materialising every record as an object is untenable for the whole retention
 * window: fourteen days is 1,209,600 records, and turning those into objects to
 * compute a handful of aggregates costs on the order of a hundred megabytes for
 * no reason.
 *
 * Fields are passed as primitives rather than a record object so the loop
 * allocates nothing per sample. One day's buffer is held at a time -- 1.38 MB --
 * so the cost is flat in the size of the window rather than linear.
 */
export function forEachRaw(
  from: Date,
  to: Date,
  visit: (
    time: number, downBytes: number, upBytes: number,
    elapsedMs: number, adapter: number, flags: number,
  ) => void,
): void {
  const { rawFolder } = meterSettings();
  if (!rawFolder) return;

  const fromMs = new Date(from.getFullYear(), from.getMonth(), from.getDate()).getTime();
  const toMs = new Date(to.getFullYear(), to.getMonth(), to.getDate() + 1).getTime();

  for (const path of filesInRange(rawFolder, false, from, to)) {
    const v = view(path);
    if (!v) continue;

    const count = Math.floor(v.byteLength / RAW_RECORD_BYTES);
    if (count === 0) continue;

    // Sorted per file rather than trusted, honouring the same contract as the
    // minute reader. Only the order is held -- one index array for the
    // day, not the records themselves -- and files are walked oldest first, so
    // the sequence across the whole window is ordered too.
    const order = new Array<number>(count);
    for (let i = 0; i < count; i++) order[i] = i;
    order.sort((a, b) => v.getUint32(a * RAW_RECORD_BYTES, true) - v.getUint32(b * RAW_RECORD_BYTES, true));

    for (let i = 0; i < count; i++) {
      const at = order[i]! * RAW_RECORD_BYTES;
      const time = v.getUint32(at, true) * 1000;
      if (time < fromMs || time >= toMs) continue;

      visit(
        time,
        v.getUint32(at + 4, true),
        v.getUint32(at + 8, true),
        v.getUint16(at + 12, true),
        v.getUint8(at + 14),
        v.getUint8(at + 15),
      );
    }
  }
}

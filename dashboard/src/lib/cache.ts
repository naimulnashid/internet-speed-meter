import 'server-only';
import { DatabaseSync } from 'node:sqlite';
import { mkdirSync, readdirSync, openSync, readSync, closeSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { MINUTE_RECORD_BYTES } from './logs';
import { meterSettings } from './settings';

/**
 * A queryable mirror of the minute log.
 *
 * Purely a cache. Every row is derived from the .bin files and nothing else, so
 * deleting the database loses no history -- it is rebuilt on the next request.
 * That is a deliberately stronger property than the Data Usage Tracker has,
 * where the database IS the history and losing it is losing everything.
 *
 * Only the MINUTE log is cached. The raw log is bounded by its retention window
 * -- fourteen days, and never more -- so the work of scanning it has a ceiling.
 * Minutes are kept forever and grow without limit, which is exactly the
 * difference that makes one worth caching and the other not.
 *
 * Ingest reads only the TAIL of each file. Records are fixed-width and
 * append-only, so the byte offset of the first unseen record is just
 * `rows_ingested * 40` -- no re-reading a month to pick up the minute that was
 * added to it a moment ago.
 */

/** Bump to discard every cached row: the schema or the meaning of a column changed. */
const SCHEMA_VERSION = 1;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS minutes (
  unix_minute   INTEGER PRIMARY KEY,

  -- Denormalised local buckets, computed at ingest. Without them, grouping by
  -- day has to happen in JS after the fact, or in SQL against UTC -- which at
  -- UTC+6 files every early-morning minute under the wrong date.
  local_date    TEXT    NOT NULL,
  local_hour    INTEGER NOT NULL,

  down_bytes    INTEGER NOT NULL,
  up_bytes      INTEGER NOT NULL,
  max_down_bps  INTEGER NOT NULL,
  max_up_bps    INTEGER NOT NULL,
  samples       INTEGER NOT NULL,
  active_samples INTEGER NOT NULL,
  adapter       INTEGER NOT NULL,
  flags         INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_minutes_date ON minutes(local_date);

-- How much of each file has been folded in, so the next pass reads only what
-- was appended since. Keyed by name rather than full path so moving the log
-- folder does not silently orphan every row.
CREATE TABLE IF NOT EXISTS files (
  name          TEXT PRIMARY KEY,
  rows_ingested INTEGER NOT NULL,
  size          INTEGER NOT NULL,
  ingested_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`;

export interface CacheStatus {
  path: string;
  rows: number;
  files: number;
  lastIngest: string | null;
  ingestedThisPass: number;
}

let db: DatabaseSync | null = null;
let dbPath = '';

function cacheFolder(): string {
  // Not beside the minute files. The cache is rebuildable and rewritten
  // constantly, so it has no business in a folder the user may have pointed at
  // cloud sync -- the same reasoning that keeps the raw log out of there.
  const local = process.env['LOCALAPPDATA'] ?? '';
  return local ? join(local, 'InternetSpeedMeter', 'cache') : '';
}

function open(): DatabaseSync | null {
  if (db) return db;

  const folder = cacheFolder();
  if (!folder) return null;

  try {
    mkdirSync(folder, { recursive: true });
    dbPath = join(folder, 'dashboard.db');
    const handle = new DatabaseSync(dbPath);

    // WAL so a read during ingest does not block, and vice versa: several pages
    // can be rendering while the current month is being folded in.
    handle.exec('PRAGMA journal_mode = WAL');
    handle.exec(SCHEMA);

    const stored = handle.prepare(`SELECT value FROM meta WHERE key = 'schema_version'`).get() as
      | { value: string }
      | undefined;

    if (stored && Number(stored.value) !== SCHEMA_VERSION) {
      // Rebuilding costs one pass over files that are already on disk. Serving
      // rows whose columns mean something different costs correctness.
      handle.exec('DELETE FROM minutes');
      handle.exec('DELETE FROM files');
    }

    handle
      .prepare(
        `INSERT INTO meta(key, value) VALUES('schema_version', ?)
         ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
      )
      .run(String(SCHEMA_VERSION));

    db = handle;
    return db;
  } catch {
    // A cache that cannot be opened must not take the dashboard down with it.
    // Callers fall back to reading the files directly.
    return null;
  }
}

function localParts(time: number): { date: string; hour: number } {
  const d = new Date(time);
  const date =
    `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-` +
    `${String(d.getDate()).padStart(2, '0')}`;
  return { date, hour: d.getHours() };
}

/** Read `count` records starting at `offset`, without loading the whole file. */
function readTail(path: string, offset: number, count: number): Buffer | null {
  let fd: number | null = null;
  try {
    fd = openSync(path, 'r');
    const buffer = Buffer.allocUnsafe(count * MINUTE_RECORD_BYTES);
    const read = readSync(fd, buffer, 0, buffer.length, offset);
    return read === buffer.length ? buffer : buffer.subarray(0, read);
  } catch {
    return null;
  } finally {
    if (fd !== null) closeSync(fd);
  }
}

/**
 * Fold any newly appended minutes into the cache.
 *
 * Cheap enough to call on every request: with nothing appended it is one stat
 * per month file and no reads at all.
 */
export function refresh(): CacheStatus | null {
  const handle = open();
  if (!handle) return null;

  const { logFolder } = meterSettings();
  let ingested = 0;

  let names: string[] = [];
  try {
    names = readdirSync(logFolder).filter((n) => /^\d{4}-\d{2}\.bin$/.test(n));
  } catch {
    names = [];
  }

  const selectFile = handle.prepare('SELECT rows_ingested, size FROM files WHERE name = ?');
  const upsertFile = handle.prepare(
    `INSERT INTO files(name, rows_ingested, size, ingested_at) VALUES(?, ?, ?, ?)
     ON CONFLICT(name) DO UPDATE SET
       rows_ingested = excluded.rows_ingested,
       size = excluded.size,
       ingested_at = excluded.ingested_at`,
  );
  const insertMinute = handle.prepare(
    `INSERT INTO minutes(unix_minute, local_date, local_hour, down_bytes, up_bytes,
                         max_down_bps, max_up_bps, samples, active_samples, adapter, flags)
     VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(unix_minute) DO UPDATE SET
       down_bytes = excluded.down_bytes,
       up_bytes = excluded.up_bytes,
       max_down_bps = excluded.max_down_bps,
       max_up_bps = excluded.max_up_bps,
       samples = excluded.samples,
       active_samples = excluded.active_samples,
       adapter = excluded.adapter,
       flags = excluded.flags`,
  );

  for (const name of names) {
    const path = join(logFolder, name);

    let size = 0;
    try {
      size = statSync(path).size;
    } catch {
      continue;
    }

    const available = Math.floor(size / MINUTE_RECORD_BYTES);
    const seen = selectFile.get(name) as { rows_ingested: number; size: number } | undefined;

    // A file that shrank was replaced or truncated, which the writer never does.
    // Re-read it from the start rather than trusting an offset into something
    // that is no longer the same file.
    let from = seen && size >= seen.size ? seen.rows_ingested : 0;
    if (from > available) from = 0;
    if (from === available) continue;

    const buffer = readTail(path, from * MINUTE_RECORD_BYTES, available - from);
    if (!buffer) continue;

    const records = Math.floor(buffer.byteLength / MINUTE_RECORD_BYTES);
    const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);

    handle.exec('BEGIN');
    try {
      for (let i = 0; i < records; i++) {
        const at = i * MINUTE_RECORD_BYTES;
        const unixMinute = view.getUint32(at, true);
        const { date, hour } = localParts(unixMinute * 60_000);

        insertMinute.run(
          unixMinute,
          date,
          hour,
          view.getUint32(at + 4, true) + view.getUint32(at + 8, true) * 0x1_0000_0000,
          view.getUint32(at + 12, true) + view.getUint32(at + 16, true) * 0x1_0000_0000,
          view.getUint32(at + 20, true),
          view.getUint32(at + 24, true),
          view.getUint16(at + 28, true),
          view.getUint16(at + 30, true),
          view.getUint8(at + 32),
          view.getUint8(at + 33),
        );
      }

      upsertFile.run(name, from + records, size, new Date().toISOString());
      handle.exec('COMMIT');
      ingested += records;
    } catch {
      handle.exec('ROLLBACK');
    }
  }

  return { ...status(handle), ingestedThisPass: ingested };
}

function status(handle: DatabaseSync): Omit<CacheStatus, 'ingestedThisPass'> {
  const rows = handle.prepare('SELECT COUNT(*) AS n FROM minutes').get() as { n: number };
  const files = handle.prepare('SELECT COUNT(*) AS n FROM files').get() as { n: number };
  const last = handle.prepare('SELECT MAX(ingested_at) AS t FROM files').get() as { t: string | null };
  return { path: dbPath, rows: rows.n, files: files.n, lastIngest: last.t };
}

export interface CachedMinute {
  unix_minute: number;
  down_bytes: number;
  up_bytes: number;
  max_down_bps: number;
  max_up_bps: number;
  samples: number;
  active_samples: number;
  adapter: number;
  flags: number;
}

/**
 * Minutes in [from, to] inclusive by LOCAL date, or null when the cache is
 * unavailable and the caller should read the files itself.
 */
export function queryMinutes(from: string, to: string): CachedMinute[] | null {
  const handle = open();
  if (!handle) return null;

  try {
    return handle
      .prepare(
        `SELECT unix_minute, down_bytes, up_bytes, max_down_bps, max_up_bps,
                samples, active_samples, adapter, flags
         FROM minutes
         WHERE local_date >= ? AND local_date <= ?
         ORDER BY unix_minute`,
      )
      .all(from, to) as unknown as CachedMinute[];
  } catch {
    return null;
  }
}

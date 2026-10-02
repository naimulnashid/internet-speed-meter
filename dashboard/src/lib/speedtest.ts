import 'server-only';
import { appendFileSync, mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { meterSettings } from './settings';
import type { LossCount, LossResult } from './pinger';

/**
 * Stored results of active speed tests.
 *
 * These are ORIGINAL data. Unlike the SQLite cache, nothing here can be
 * regenerated from the .bin files -- a test that ran at 21:40 cannot be run
 * again at 21:40 -- so they live beside the minute log, in whatever folder the
 * history is kept in, and are as worth surviving a reset as the history is.
 *
 * Append-only JSONL rather than SQLite, deliberately. That folder may well be
 * inside cloud sync, and a WAL-mode database there is three files that must be
 * mutually consistent to restore, which is the exact trap the Data Usage
 * Tracker documented. A line of JSON appended per test has no such problem, is
 * readable in a text editor, and needs no schema migration for a file that will
 * hold a few hundred rows in its lifetime.
 */

/** Rates are bytes per second, matching every other rate in the codebase. */
export interface SpeedTestResult {
  /** ISO 8601, UTC. */
  at: string;
  /**
   * 'light' is no longer offered but stays in the union: results written while
   * it existed are still in the file, and a reader that cannot name them would
   * be a reader that discards them.
   */
  profile: 'light' | 'standard' | 'thorough' | 'heavy';
  /** Parallel transfers used. 1 means the single-connection measurement. */
  connections?: number;
  server: string;

  downBps: number;
  upBps: number;
  /** Best one-second window seen during the download. */
  peakDownBps: number;
  /** Best one-second window seen during the upload. Absent from older results. */
  peakUpBps?: number;

  /** Median round trip of the latency probes on an idle link, and their mean deviation. */
  latencyMs: number;
  jitterMs: number;

  /**
   * Median round trip measured WHILE the download was saturating the link.
   *
   * The difference between this and the idle figure is bufferbloat: how much a
   * busy connection delays everything else on it. It is the latency number that
   * predicts whether a call breaks up while something downloads, which the idle
   * figure cannot tell you at all. Optional because results recorded before this
   * existed do not have it.
   */
  loadedLatencyMs?: number;

  /**
   * The same, measured while the UPLOAD was saturating the link. Upstream queues
   * are often the worse of the two, and they are what breaks a video call while
   * something is being backed up. Absent from older results.
   */
  loadedUpLatencyMs?: number;

  /** Jitter of the probes taken during each leg, by the same rule as the idle figure. */
  loadedJitterMs?: number;
  loadedUpJitterMs?: number;

  /**
   * Echoes sent and lost in each phase, from the dashboard's ICMP pinger.
   * Counts rather than percentages, so a reader can see how much a figure
   * rests on: 1 lost of 30 is 3%, and so is 30 of 1000. Absent when the
   * pinger could not run, and from older results.
   */
  loss?: LossResult;

  /**
   * Set when the run came from another device on the network rather than
   * this PC - the test runs in the browser, so it measured THAT device's
   * connection. Packet loss and the meter's own record describe the PC, so
   * those runs have neither. Absent for runs on this PC, and from older
   * results, which all were.
   */
  device?: 'phone' | 'other';

  /** What the run actually cost, which is the point of showing it. */
  downBytes: number;
  upBytes: number;
  downSeconds: number;
  upSeconds: number;

  /**
   * Who was at each end. Worth storing per run rather than only showing live:
   * an ISP change, or being served by a different datacentre, is exactly the
   * kind of thing that explains why one week's numbers differ from another's.
   */
  clientIsp?: string;
  clientAsn?: number;
  clientPlace?: string;
  serverPlace?: string;
  protocol?: string;
}

const FILE = 'speedtests.jsonl';

function path(): string | null {
  const { logFolder } = meterSettings();
  return logFolder ? join(logFolder, FILE) : null;
}

export function readResults(limit = 50): SpeedTestResult[] {
  return readAllResults().slice(0, limit);
}

/** Every saved run, newest first. The file is one line per run and small. */
export function readAllResults(): SpeedTestResult[] {
  const p = path();
  if (!p) return [];

  let text: string;
  try {
    text = readFileSync(p, 'utf8');
  } catch {
    // No file yet is the normal state before the first test.
    return [];
  }

  const out: SpeedTestResult[] = [];
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    try {
      const parsed = JSON.parse(trimmed) as SpeedTestResult;
      // One corrupt line -- a half-written append, a hand edit -- must not throw
      // away every result before and after it.
      if (typeof parsed.at === 'string' && typeof parsed.downBps === 'number') {
        out.push(parsed);
      }
    } catch {
      continue;
    }
  }

  out.sort((a, b) => b.at.localeCompare(a.at));
  return out;
}

export function appendResult(result: SpeedTestResult): { ok: boolean; error?: string } {
  const p = path();
  if (!p) return { ok: false, error: 'No log folder configured.' };

  try {
    const { logFolder } = meterSettings();
    mkdirSync(logFolder, { recursive: true });
    appendFileSync(p, JSON.stringify(result) + '\n', 'utf8');
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

/**
 * Accepts a result posted by the browser.
 *
 * Everything is bounded and coerced rather than trusted: this arrives from a
 * client component, and a stray Infinity or a 400 MB string would otherwise be
 * written into a file that is meant to outlive the machine.
 */
export function parseResult(input: unknown): SpeedTestResult | null {
  if (typeof input !== 'object' || input === null) return null;
  const o = input as Record<string, unknown>;

  const num = (v: unknown): number | null => {
    const n = Number(v);
    return Number.isFinite(n) && n >= 0 && n < 1e15 ? n : null;
  };

  const profile = o['profile'];
  if (profile !== 'light' && profile !== 'standard' && profile !== 'thorough' && profile !== 'heavy') {
    return null;
  }

  const fields = {
    downBps: num(o['downBps']),
    upBps: num(o['upBps']),
    peakDownBps: num(o['peakDownBps']),
    latencyMs: num(o['latencyMs']),
    jitterMs: num(o['jitterMs']),
    downBytes: num(o['downBytes']),
    upBytes: num(o['upBytes']),
    downSeconds: num(o['downSeconds']),
    upSeconds: num(o['upSeconds']),
  };

  for (const v of Object.values(fields)) {
    if (v === null) return null;
  }

  const server = typeof o['server'] === 'string' ? o['server'].slice(0, 120) : 'unknown';

  /** Bounded, because this is client-supplied text going into a permanent file. */
  const text = (v: unknown): string | undefined =>
    typeof v === 'string' && v.length > 0 ? v.slice(0, 120) : undefined;

  const optionalNum = (v: unknown): number | undefined => {
    const n = Number(v);
    return Number.isFinite(n) && n >= 0 && n < 1e15 ? n : undefined;
  };

  const lossCount = (v: unknown): LossCount | undefined => {
    if (typeof v !== 'object' || v === null) return undefined;
    const c = v as Record<string, unknown>;
    const sent = optionalNum(c['sent']);
    const lost = optionalNum(c['lost']);
    if (sent === undefined || lost === undefined || lost > sent) return undefined;
    return { sent: Math.round(sent), lost: Math.round(lost) };
  };
  const lossIn = (o['loss'] ?? {}) as Record<string, unknown>;
  const loss = {
    idle: lossCount(lossIn['idle']),
    down: lossCount(lossIn['down']),
    up: lossCount(lossIn['up']),
  };

  return {
    at: new Date().toISOString(),
    profile,
    server,
    ...(optionalNum(o['connections']) !== undefined
      ? { connections: optionalNum(o['connections'])! } : {}),
    ...(optionalNum(o['loadedLatencyMs']) !== undefined
      ? { loadedLatencyMs: optionalNum(o['loadedLatencyMs'])! } : {}),
    ...(optionalNum(o['loadedUpLatencyMs']) !== undefined
      ? { loadedUpLatencyMs: optionalNum(o['loadedUpLatencyMs'])! } : {}),
    ...(optionalNum(o['loadedJitterMs']) !== undefined
      ? { loadedJitterMs: optionalNum(o['loadedJitterMs'])! } : {}),
    ...(optionalNum(o['loadedUpJitterMs']) !== undefined
      ? { loadedUpJitterMs: optionalNum(o['loadedUpJitterMs'])! } : {}),
    ...(o['device'] === 'phone' || o['device'] === 'other' ? { device: o['device'] } : {}),
    ...(loss.idle && loss.down && loss.up
      ? { loss: { idle: loss.idle, down: loss.down, up: loss.up } } : {}),
    ...(optionalNum(o['peakUpBps']) !== undefined
      ? { peakUpBps: optionalNum(o['peakUpBps'])! } : {}),
    ...(text(o['clientIsp']) ? { clientIsp: text(o['clientIsp'])! } : {}),
    ...(optionalNum(o['clientAsn']) !== undefined ? { clientAsn: optionalNum(o['clientAsn'])! } : {}),
    ...(text(o['clientPlace']) ? { clientPlace: text(o['clientPlace'])! } : {}),
    ...(text(o['serverPlace']) ? { serverPlace: text(o['serverPlace'])! } : {}),
    ...(text(o['protocol']) ? { protocol: text(o['protocol'])! } : {}),
    downBps: fields.downBps!,
    upBps: fields.upBps!,
    peakDownBps: fields.peakDownBps!,
    latencyMs: fields.latencyMs!,
    jitterMs: fields.jitterMs!,
    downBytes: fields.downBytes!,
    upBytes: fields.upBytes!,
    downSeconds: fields.downSeconds!,
    upSeconds: fields.upSeconds!,
  };
}

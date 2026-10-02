import Link from 'next/link';
import { Shell } from '@/components/Shell';
import { Card, CardTitle } from '@/components/Card';
import { SpeedTest } from '@/components/SpeedTest';
import { readAllResults, type SpeedTestResult } from '@/lib/speedtest';
import { readMinutes } from '@/lib/logs';
import { meterSettings } from '@/lib/settings';
import { formatBytes, formatRate, formatStamp, type Units } from '@/lib/format';

export const dynamic = 'force-dynamic';

/** Runs per page of the previous-runs table. */
const PAGE_SIZE = 25;

/**
 * Peak download the meter itself recorded over the minutes a test spanned.
 *
 * This is the reason the page belongs here rather than being a worse copy of
 * fast.com. The browser reports what it managed to pull; the meter reports what
 * actually crossed the adapter at the same moment, measured independently and
 * from outside the browser. Agreement is a check on both. A meter figure that
 * is higher means other traffic overlapped the test; much lower usually means
 * the recording missed the window.
 */
function meterPeakFor(at: string, seconds: number): number | null {
  const end = new Date(at).getTime();
  const start = end - Math.max(1, seconds) * 1000;

  const firstMinute = Math.floor(start / 60_000) * 60_000;
  const lastMinute = Math.floor(end / 60_000) * 60_000;

  const byTime = new Map(readMinutes(new Date(start), new Date(end)).map((m) => [m.time, m]));

  // EVERY minute the run touched has to be present, or there is no comparison
  // to make. This is not pedantry: the recorder flushes once a minute, so the
  // minute a test just ran in does not exist yet, and a window that clipped the
  // tail of the previous minute would report that minute's unrelated background
  // traffic as what the meter saw during the test. It did exactly that -- 656
  // Kbps against a measured 234 Mbps -- which reads as the two measurements
  // disagreeing when in fact one of them had not happened yet.
  let peak = 0;
  for (let t = firstMinute; t <= lastMinute; t += 60_000) {
    const m = byTime.get(t);
    if (!m) return null;
    if (m.maxDownBps > peak) peak = m.maxDownBps;
  }

  return peak > 0 ? peak : null;
}

/**
 * Idle / during download / during upload, each in its fixed place, with a dash
 * for a figure the run does not have: older runs predate the upload figure,
 * and a few had a bad download figure cleared. Without the dash, "27 / 36"
 * would read as idle and download when the 36 was the upload.
 */
function latencyCell(r: SpeedTestResult): string {
  if (r.loadedLatencyMs === undefined && r.loadedUpLatencyMs === undefined) {
    return r.latencyMs.toFixed(0);
  }
  const ms = (v: number | undefined) => (v === undefined ? '–' : v.toFixed(0));
  return `${ms(r.latencyMs)} / ${ms(r.loadedLatencyMs)} / ${ms(r.loadedUpLatencyMs)}`;
}

/** Loss over every echo of the run, with the per-leg split on hover. */
function lossCell(r: SpeedTestResult) {
  if (!r.loss) return '—';
  const { idle, down, up } = r.loss;
  const pct = (c: { sent: number; lost: number }) =>
    c.sent === 0 ? '-' : `${((c.lost / c.sent) * 100).toFixed(1)}%`;
  const total = { sent: idle.sent + down.sent + up.sent, lost: idle.lost + down.lost + up.lost };
  return (
    <span title={`idle ${pct(idle)} · download ${pct(down)} · upload ${pct(up)} · ${total.lost} of ${total.sent} pings lost`}>
      {pct(total)}
    </span>
  );
}

export default async function SpeedTestPage({
  searchParams,
}: {
  searchParams: Promise<{ page?: string }>;
}) {
  const settings = meterSettings();
  const units: Units = settings.units;

  // Page 1 is the newest runs. The page lives in the URL so a refresh or a
  // shared link keeps its place; an out-of-range number is clamped rather than
  // showing an empty table.
  const all = readAllResults();
  const pages = Math.max(1, Math.ceil(all.length / PAGE_SIZE));
  const requested = Math.floor(Number((await searchParams).page));
  const page = Number.isFinite(requested) ? Math.min(Math.max(requested, 1), pages) : 1;
  const results = all.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);

  return (
    <Shell>
      <div className="stack">
        <SpeedTest units={units} />

        {results.length > 0 && (
          <Card hover={false}>
            <CardTitle
              sub="Each run, beside what the meter independently recorded at the adapter."
              aside={<span className="stat-foot">{all.length} kept</span>}
            >
              Previous runs
            </CardTitle>

            <div style={{ overflowX: 'auto' }}>
              <table className="table">
                <thead>
                  <tr>
                    <th>When</th>
                    <th>Size</th>
                    <th className="num">Download</th>
                    <th className="num">Upload</th>
                    <th className="num">Latency idle/down/up</th>
                    <th className="num">Loss</th>
                    <th className="num">Ran for</th>
                    <th className="num">Meter saw</th>
                    <th className="num">Data spent</th>
                  </tr>
                </thead>
                <tbody>
                  {results.map((r) => {
                    const seen = meterPeakFor(r.at, r.downSeconds + r.upSeconds);
                    return (
                      <tr key={r.at}>
                        <td>{formatStamp(new Date(r.at).getTime())}</td>
                        <td>
                          {r.profile}{r.connections !== undefined && ` · ${r.connections === 1 ? 'single' : '×' + r.connections}`}
                          {r.device && ` · ${r.device === 'phone' ? 'phone' : 'other device'}`}
                        </td>
                        <td className="num" style={{ color: 'var(--down)' }}>
                          {formatRate(r.downBps, units)}
                        </td>
                        <td className="num" style={{ color: 'var(--up)' }}>
                          {formatRate(r.upBps, units)}
                        </td>
                        <td className="num" title={r.loadedLatencyMs === undefined && r.loadedUpLatencyMs === undefined ? undefined : 'idle / during download / during upload'}>
                          {latencyCell(r)} ms
                        </td>
                        <td className="num">{lossCell(r)}</td>
                        <td className="num" title={r.downSeconds < 3 ? 'Too short to be reliable' : undefined}>
                          {r.downSeconds.toFixed(1)}s{r.downSeconds < 3 ? ' ⚠' : ''}
                        </td>
                        <td className="num" title={r.device ? 'Run on another device; the meter only sees this PC' : undefined}>
                          {r.device || seen === null ? '—' : formatRate(seen, units)}
                        </td>
                        <td className="num">{formatBytes(r.downBytes + r.upBytes)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            {pages > 1 && <Pager page={page} pages={pages} />}

            <div className="stat-foot" style={{ marginTop: '1rem' }}>
              <strong>Meter saw</strong> is the fastest second the recorder logged at the adapter
              during the run, from a completely separate measurement. It reads <em>&mdash;</em> until every
              minute the run touched has been written, so a test finishes before its own comparison
              does &mdash; the recorder flushes once a minute. It also reads <em>&mdash;</em> when the
              meter was not running, and can read higher than the test when other traffic overlapped
              it, or when the meter caught a faster single second than the browser's average. Results are kept in <code>speedtests.jsonl</code> beside the history,
              one line per run.
            </div>
          </Card>
        )}
      </div>
    </Shell>
  );
}

/**
 * Newer / Older links under the table. Plain links rather than client state, so
 * each page is server-rendered with its own meter comparisons. `scroll={false}`
 * keeps the reader at the table instead of throwing them back to the dial.
 */
function Pager({ page, pages }: { page: number; pages: number }) {
  const href = (n: number) => (n === 1 ? '/speedtest' : `/speedtest?page=${n}`);
  return (
    <nav className="pager" aria-label="Previous runs pages">
      {page > 1
        ? <Link className="chip" href={href(page - 1)} scroll={false}>&larr; Newer</Link>
        : <span className="chip" aria-disabled="true">&larr; Newer</span>}
      <span className="stat-foot" style={{ margin: 0 }}>Page {page} of {pages}</span>
      {page < pages
        ? <Link className="chip" href={href(page + 1)} scroll={false}>Older &rarr;</Link>
        : <span className="chip" aria-disabled="true">Older &rarr;</span>}
    </nav>
  );
}

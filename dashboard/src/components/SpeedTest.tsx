'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { Card, CardTitle } from './Card';
import { Gauge } from './Gauge';
import { SpeedChart, type SpeedPoint } from './SpeedChart';
import { StatIcon } from './StatIcon';
import { formatBytes, formatRate, type Units } from '@/lib/format';

/**
 * An active speed test, run from the browser.
 *
 * This is the ONE place in the project that generates traffic of its own.
 * Everything else observes what was already happening; this deliberately makes
 * a connection busy in order to find out how busy it can get. It also costs
 * real data, which is why the budget is chosen by the reader rather than by us,
 * and why every screen states what a run will spend before it spends it.
 *
 * Measured in the browser rather than on the server because the transfer has to
 * cross the same path the user actually browses over. A server-side test would
 * measure the same machine by a different route and prove nothing extra.
 */

const HOST = 'https://speed.cloudflare.com';

interface Profile {
  id: 'standard' | 'thorough' | 'heavy';
  label: string;
  downBytes: number;
  upBytes: number;
  /** Parallel download connections. */
  streams: number;
  /** Parallel upload connections. */
  upStreams: number;
}

const MB = 1024 * 1024;

/**
 * Upload budgets are far smaller than download deliberately. Domestic upstream
 * is typically a fraction of downstream, so matching the two would make the
 * upload leg dominate the wall time and the data bill for no extra accuracy.
 *
 * Never more than 6 download streams. speed.cloudflare.com is HTTP/1.1 only,
 * where a browser opens at most 6 connections to one server, and each stream
 * is sized to outlast the 20-second leg - so Thorough and Heavy, which once
 * asked for 8, really ran 6 while their label said 8. Upload counts are set
 * outright rather than derived from the download's, so that change left the
 * upload leg exactly as it was.
 */
const PROFILES: Profile[] = [
  {
    id: 'standard', label: 'Standard', downBytes: 100 * MB, upBytes: 25 * MB, streams: 6, upStreams: 3,
  },
  {
    id: 'thorough', label: 'Thorough', downBytes: 250 * MB, upBytes: 60 * MB, streams: 6, upStreams: 4,
  },
  {
    id: 'heavy', label: 'Heavy', downBytes: 500 * MB, upBytes: 120 * MB, streams: 6, upStreams: 4,
  },
];

/** Neither leg may run longer than this, however slow the link. */
const LEG_TIMEOUT_MS = 20_000;

type Phase = 'idle' | 'latency' | 'download' | 'upload' | 'saving' | 'done' | 'error';

interface Live {
  bytesPerSecond: number;
  transferred: number;
  target: number;
}

/** Exactly what gets posted to /api/speedtest, and what the result cards read. */
interface Measured {
  profile: Profile['id'];
  connections: number;
  server: string;
  downBps: number;
  upBps: number;
  peakDownBps: number;
  peakUpBps: number;
  latencyMs: number;
  jitterMs: number;
  loadedLatencyMs: number;
  loadedUpLatencyMs: number;
  loadedJitterMs: number;
  loadedUpJitterMs: number;
  /** Absent when the pinger could not run, or ICMP looks blocked outright. */
  loss?: Record<'idle' | 'down' | 'up', LossCount>;
  /** Set when the run came from another device on the network; absent for this PC. */
  device?: RemoteDevice;
  downBytes: number;
  upBytes: number;
  downSeconds: number;
  upSeconds: number;
  clientIsp?: string;
  clientAsn?: number;
  clientPlace?: string;
  serverPlace?: string;
  protocol?: string;
}

interface LossCount { sent: number; lost: number }

type RemoteDevice = 'phone' | 'other';

/**
 * Which device is running this page, as far as the browser can tell.
 * Undefined means this PC.
 *
 * Decided by the address the page was opened at, not by anything the server
 * sees: the dashboard on this PC is reached as localhost, and anything else
 * reached it over the network. That matters because the test runs in the
 * browser, so from a phone it measures the PHONE's connection - and the two
 * things on this page that live on the PC, the packet-loss pinger and the
 * meter's own record of the run, would be describing a different device.
 */
function remoteDevice(): RemoteDevice | undefined {
  const host = window.location.hostname;
  if (host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1') {
    return undefined;
  }
  // A touchscreen counts too. A phone browser in "desktop site" mode, or an
  // iPad, reports a desktop user agent - the first phone run this saved was
  // labelled 'other' for exactly that reason - but it cannot hide its touch
  // points. A touchscreen laptop would be called a phone; that is the lesser
  // error here.
  const nav = navigator as Navigator & { userAgentData?: { mobile?: boolean } };
  const mobile = nav.userAgentData?.mobile === true
    || /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent)
    || navigator.maxTouchPoints > 1;
  return mobile ? 'phone' : 'other';
}

const DEVICE_LABEL: Record<RemoteDevice, string> = { phone: 'your phone', other: 'another device' };

/** What speed.cloudflare.com/meta reports about both ends of the connection. */
interface Meta {
  clientIp: string;
  asn: number;
  asOrganization: string;
  city: string;
  region: string;
  country: string;
  httpProtocol: string;
  colo: { iata: string; city: string; region: string };
}

function place(city?: string, region?: string, country?: string): string {
  return [city, region, country].filter(Boolean).join(', ');
}

/**
 * One round trip to the test server: an empty download, timed by the caller.
 *
 * Sent `credentials: 'include'`, in `no-cors` mode, and that is the whole
 * point of this function. speed.cloudflare.com speaks HTTP/1.1 only - offered
 * h2 in the TLS handshake, it picks http/1.1, and it advertises no h3 - so a
 * browser allows it 6 connections at once, and a Standard run's download
 * opens exactly 6. A probe on the same footing then raced the downloads for a
 * socket: once a download took the probe's, every later probe queued for the
 * rest of the 20-second leg, and the median of one quick probe and one
 * 20-second wait is 10 s. That was 6 of 18 saved parallel runs, all reading
 * 10.0-10.4 s; no single-connection run ever did.
 *
 * Browsers keep credentialed and anonymous requests on separate connection
 * pools, so this probe has a pool the downloads never touch. Measured with 6
 * downloads running: plain probes could not get a socket within 2.5 s at all;
 * these answered in 48-294 ms. `no-cors`, because a credentialed CORS request
 * is refused against `Access-Control-Allow-Origin: *`; the response body is
 * opaque, and only its timing is wanted. The idle probes use it too, so idle
 * and loaded latency are measured the same way.
 */
function probeOnce(signal: AbortSignal): Promise<Response> {
  return fetch(`${HOST}/__down?bytes=0&r=${Math.random()}`, {
    cache: 'no-store', mode: 'no-cors', credentials: 'include', signal,
  });
}

/**
 * Keeps probing round-trip time until told to stop.
 *
 * Run alongside the download, this measures latency on a link that is actually
 * busy. The difference from the idle figure is bufferbloat, and it is the
 * number that predicts whether a call falls apart while something downloads --
 * which an idle ping cannot tell you, because an idle link has no queue.
 */
function startProbing(signal: AbortSignal): { rtts: number[]; stop: () => Promise<void> } {
  const rtts: number[] = [];
  let stopped = false;

  const loop = (async () => {
    while (!stopped && !signal.aborted) {
      const started = performance.now();
      try {
        await probeOnce(signal);
      } catch {
        return;
      }
      rtts.push(performance.now() - started);
    }
  })();

  return {
    rtts,
    stop: async () => {
      stopped = true;
      await loop;
    },
  };
}

/** One loaded-latency line for the result card, with its rise over idle. */
function loaded(leg: 'download' | 'upload', ms: number, idleMs: number): string {
  const rise = ms - idleMs;
  return `${leg} ${ms.toFixed(0)} ms (${rise > 0 ? '+' : ''}${rise.toFixed(0)} ms)`;
}

/** Jitter as the idle figure defines it: the median step between consecutive round trips. */
function jitter(rtts: number[]): number {
  return rtts.length > 1 ? median(rtts.slice(1).map((v, i) => Math.abs(v - rtts[i]!))) : 0;
}

function percent(c: LossCount): string {
  if (c.sent === 0) return '-';
  const p = (c.lost / c.sent) * 100;
  return p === 0 ? '0%' : `${p < 10 ? p.toFixed(1) : p.toFixed(0)}%`;
}

/** Shortest idle window the pinger counts, so the idle loss rests on ~30 echoes. */
const IDLE_PING_MS = 3000;

/**
 * Talks to the dashboard's packet-loss pinger. Best effort throughout: a test
 * that cannot measure loss is still a test, so every failure becomes null.
 */
async function pinger(body: Record<string, unknown>): Promise<Record<string, unknown> | null> {
  try {
    const r = await fetch('/api/speedtest/loss', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) return null;
    const json = await r.json();
    return json.ok ? json : null;
  } catch {
    return null;
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const t = setTimeout(resolve, Math.max(0, ms));
    signal.addEventListener('abort', () => { clearTimeout(t); resolve(); });
  });
}

function median(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid]! : (sorted[mid - 1]! + sorted[mid]!) / 2;
}

export function SpeedTest({ units }: { units: Units }) {
  const router = useRouter();
  const [profile, setProfile] = useState<Profile>(PROFILES[0]!);
  const [phase, setPhase] = useState<Phase>('idle');
  const [live, setLive] = useState<Live>({ bytesPerSecond: 0, transferred: 0, target: 0 });
  const [result, setResult] = useState<Measured | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [meta, setMeta] = useState<Meta | null>(null);
  const [series, setSeries] = useState<{ down: SpeedPoint[]; up: SpeedPoint[] }>({ down: [], up: [] });

  // Read after mounting, not during render: the server renders this page
  // without a window, and a first client render that disagreed with it would
  // be a hydration mismatch.
  const [device, setDevice] = useState<RemoteDevice | undefined>(undefined);
  useEffect(() => { setDevice(remoteDevice()); }, []);

  /**
   * Parallel by default, because that is what a speed test is normally asking:
   * how fast is the line. A single connection asks a different and also useful
   * question -- how fast is ONE transfer -- and the two diverge when the limit
   * is the bandwidth-delay product rather than the link, which is the usual
   * reason a single download feels slower than the connection you pay for.
   */
  const [parallel, setParallel] = useState(true);
  const abort = useRef<AbortController | null>(null);

  // Fetched on mount rather than when a run starts, so the page can say who is
  // at each end before spending anything to find out.
  useEffect(() => {
    let cancelled = false;
    fetch(`${HOST}/meta`, { cache: 'no-store' })
      .then((r) => r.json())
      .then((m: Meta) => { if (!cancelled) setMeta(m); })
      .catch(() => { /* The card simply does not appear. */ });
    return () => { cancelled = true; };
  }, []);

  const cancel = useCallback(() => {
    abort.current?.abort();
    abort.current = null;
    setPhase('idle');
    setLive({ bytesPerSecond: 0, transferred: 0, target: 0 });
  }, []);

  const run = useCallback(async () => {
    const controller = new AbortController();
    abort.current = controller;
    setError(null);
    setResult(null);
    setSeries({ down: [], up: [] });
    let pingerId: string | null = null;
    let pingerStarted: Promise<number> | null = null;
    const runDevice = remoteDevice();

    // A sample every quarter second is plenty for a chart a few hundred pixels
    // wide; the transfer reports on every chunk, which is far more often.
    const sampler = (leg: 'down' | 'up', target: number) => {
      let last = -Infinity;
      return (bytesPerSecond: number, transferred: number, elapsedMs: number) => {
        setLive({ bytesPerSecond, transferred, target });
        if (elapsedMs - last < 250) return;
        last = elapsedMs;
        setSeries((s) => ({ ...s, [leg]: [...s[leg], { t: elapsedMs / 1000, bps: bytesPerSecond }] }));
      };
    };

    try {
      // ---- latency, and the idle window for packet loss -----------------
      setPhase('latency');
      // Started alongside the latency probes rather than before them, so the
      // moment PowerShell takes to start is not added to every run. Not at all
      // from another device: the pinger runs on the PC, so its losses would
      // be the PC's link put beside the phone's speeds.
      if (!runDevice) {
        pingerStarted = pinger({ action: 'start' }).then((r) => {
          pingerId = r ? String(r['id']) : null;
          return performance.now();
        });
      }
      const rtts: number[] = [];
      for (let i = 0; i < 12; i++) {
        const started = performance.now();
        await probeOnce(controller.signal);
        rtts.push(performance.now() - started);
      }
      // Drop the first three. The first pays for DNS, TCP and TLS, and the next
      // two land while HTTP/2 is still settling; counting them put the measured
      // jitter at 96 ms against a 39 ms median, which describes connection
      // setup rather than the connection.
      const settled = rtts.slice(3);
      const latencyMs = median(settled);

      // MEDIAN of the consecutive differences, not their mean. Jitter is meant to
      // describe the typical variation between packets, and a mean is destroyed
      // by one outlier: a single stalled probe among nine put the reported figure
      // at 158 ms on a link whose round trips were measurably 27-36 ms apart.
      // The median moves barely at all for the same spike, which is the property
      // wanted from a number that claims to be typical.
      const jitterMs = jitter(settled);

      const idleFrom = pingerStarted ? await pingerStarted : 0;
      if (pingerId) {
        await sleep(IDLE_PING_MS - (performance.now() - idleFrom), controller.signal);
        if (controller.signal.aborted) return;
      }

      // ---- download, with latency probed under load ----------------------
      setPhase('download');
      if (pingerId) await pinger({ action: 'mark', id: pingerId, phase: 'down' });
      const probe = startProbing(controller.signal);
      const downStreams = parallel ? profile.streams : 1;
      const down = await transfer({
        direction: 'down',
        target: profile.downBytes,
        streams: downStreams,
        signal: controller.signal,
        onProgress: sampler('down', profile.downBytes),
      });
      await probe.stop();
      const loadedLatencyMs = probe.rtts.length > 0 ? median(probe.rtts) : latencyMs;
      const loadedJitterMs = probe.rtts.length > 1 ? jitter(probe.rtts) : jitterMs;

      // ---- upload, with latency probed under load again -----------------
      // The probe is a download of nothing, but its request still has to queue
      // behind the upload on the way out, so this sees the upstream buffer.
      setPhase('upload');
      if (pingerId) await pinger({ action: 'mark', id: pingerId, phase: 'up' });
      const upProbe = startProbing(controller.signal);
      const up = await transfer({
        direction: 'up',
        target: profile.upBytes,
        streams: parallel ? profile.upStreams : 1,
        signal: controller.signal,
        onProgress: sampler('up', profile.upBytes),
      });
      await upProbe.stop();
      const loadedUpLatencyMs = upProbe.rtts.length > 0 ? median(upProbe.rtts) : latencyMs;
      const loadedUpJitterMs = upProbe.rtts.length > 1 ? jitter(upProbe.rtts) : jitterMs;

      // The run is over, so the dial goes back to rest: the result cards hold
      // the figures now, and a needle parked on the last upload sample would
      // only be a second, stale copy of one of them.
      setLive({ bytesPerSecond: 0, transferred: 0, target: 0 });

      const stopped = pingerId ? await pinger({ action: 'stop', id: pingerId }) : null;
      pingerId = null;
      const counts = stopped ? stopped['loss'] as Measured['loss'] : undefined;
      // Not one idle echo answered means ICMP is blocked somewhere on the way
      // (a firewall, some VPNs), not that the link drops everything -- the
      // latency probes just crossed it fine. That is no measurement at all.
      const loss = counts && counts.idle.sent > counts.idle.lost ? counts : undefined;

      const payload: Measured = {
        profile: profile.id,
        connections: downStreams,
        server: 'speed.cloudflare.com',
        downBps: down.average,
        upBps: up.average,
        peakDownBps: down.peak,
        peakUpBps: up.peak,
        latencyMs,
        jitterMs,
        loadedLatencyMs,
        loadedUpLatencyMs,
        loadedJitterMs,
        loadedUpJitterMs,
        ...(loss ? { loss } : {}),
        ...(runDevice ? { device: runDevice } : {}),
        ...(meta ? {
          clientIsp: meta.asOrganization,
          clientAsn: meta.asn,
          clientPlace: place(meta.city, meta.region, meta.country),
          serverPlace: `${meta.colo?.city ?? ''} (${meta.colo?.iata ?? '?'})`,
          protocol: meta.httpProtocol,
        } : {}),
        downBytes: down.transferred,
        upBytes: up.transferred,
        downSeconds: down.seconds,
        upSeconds: up.seconds,
      };

      setResult(payload);
      setPhase('saving');

      const response = await fetch('/api/speedtest', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(payload),
      });

      // The measurement itself is done and correct; only the write was refused.
      // Say which, so this does not read as a failed test.
      if (response.status === 401) {
        throw new Error('Your session expired, so the result was not saved. Sign in and run it again.');
      }

      const saved = await response.json();
      if (!saved.ok) throw new Error(saved.error ?? 'Could not save the result.');

      setPhase('done');

      // The previous-runs table is a server component, so it holds whatever was
      // true when the page was requested. Without this the run just saved does
      // not appear until you navigate away and back, which reads as the save
      // having silently failed. From a later page of that table, go back to
      // the first, where the new run is.
      if (new URLSearchParams(window.location.search).has('page')) {
        router.replace(window.location.pathname, { scroll: false });
      } else {
        router.refresh();
      }
    } catch (e) {
      if (controller.signal.aborted) return;
      setError(e instanceof Error ? e.message : String(e));
      setPhase('error');
    } finally {
      abort.current = null;
      // A run that was stopped or failed still has a pinger going -- or one
      // still starting, if it was stopped within the first moment. Not awaited:
      // nothing waits on its answer, and it would expire on its own anyway.
      void pingerStarted?.then(() => {
        if (pingerId) void pinger({ action: 'stop', id: pingerId });
      });
    }
  }, [profile, parallel, router, meta]);

  const busy = phase === 'latency' || phase === 'download' || phase === 'upload' || phase === 'saving';

  return (
    <div className="stack">
      {/*
        The dial is present from the start rather than appearing on the first
        run. An instrument that materialises when you press a button reads as a
        result being announced; one that is already sitting at zero reads as an
        instrument waiting, which is what it is -- and it shows the scale a
        reading will land on before there is a reading.
      */}
      <div className="grid grid--2">
      <Card hover={false}>
        <CardTitle sub="Pick what a run may spend before starting it.">Test size</CardTitle>

        <div style={{ display: 'flex', gap: '0.35rem', flexWrap: 'wrap' }}>
          {PROFILES.map((p) => (
            <button
              key={p.id}
              className="chip"
              data-active={p.id === profile.id}
              disabled={busy}
              onClick={() => setProfile(p)}
            >
              {p.label} · {formatBytes(p.downBytes + p.upBytes)}
            </button>
          ))}
        </div>

        <div className="stat-label" style={{ marginTop: '1.4rem' }}>Connections</div>
        <div style={{ display: 'flex', gap: '0.35rem', flexWrap: 'wrap', marginTop: '0.5rem' }}>
          <button
            className="chip" data-active={parallel} disabled={busy}
            onClick={() => setParallel(true)}
          >
            Parallel &times;{profile.streams}
          </button>
          <button
            className="chip" data-active={!parallel} disabled={busy}
            onClick={() => setParallel(false)}
          >
            Single
          </button>
        </div>

        <div className="stat-foot" style={{ marginTop: '0.8rem' }}>
          {parallel
            ? 'Several transfers at once, which is what a speed test usually means: how fast is the line.'
            : 'One transfer, which is what a single download gets. Often well below the line, because one connection is limited by window size and round trip rather than by bandwidth.'}
        </div>

        <div className="note" style={{ marginTop: '1.2rem' }}>
          <strong>This run will transfer up to {formatBytes(profile.downBytes + profile.upBytes)}</strong>
          {' '}to and from speed.cloudflare.com, and that traffic is real: it counts against a data
          cap{device
            ? <>. It runs on {DEVICE_LABEL[device]}, over its own connection, so the meter on the PC will
              not see it, and packet loss is not measured.</>
            : ', and the meter will record it as a spike in your history like any other transfer.'}
          {!parallel && ' A single connection may not reach the budget before the time limit, in which case it spends less.'}
        </div>

      </Card>

      <Card hover={false}>
          <CardTitle
            // A blank line at rest rather than no line, so the card does not
            // grow by a line the moment a run starts.
            sub={
              phase === 'latency' ? (device ? 'Timing small requests.' : 'Timing small requests and counting lost pings.')
                : phase === 'download' ? 'Pulling from speed.cloudflare.com.'
                : phase === 'upload' ? 'Pushing to speed.cloudflare.com.'
                : phase === 'saving' ? 'Writing the result beside the history.'
                : '\u00a0'
            }
          >
            {phase === 'latency' ? 'Warming up'
              : phase === 'download' ? 'Downloading'
              : phase === 'upload' ? 'Uploading'
              : phase === 'saving' ? 'Saving'
              : 'Ready'}
          </CardTitle>

          <Gauge
            bytesPerSecond={live.bytesPerSecond}
            units={units}
            tone={phase === 'upload' ? 'up' : 'down'}
          />

          {/*
            The progress bar only means something while something is moving, but
            it is hidden rather than removed at rest: its space stays reserved so
            starting a run does not make the card taller.
          */}
          <div style={{ visibility: busy && phase !== 'saving' ? 'visible' : 'hidden' }}>
            <div className="meter" style={{ marginTop: '0.5rem' }}>
              <div
                className={`meter-fill ${phase === 'upload' ? 'meter-fill--up' : ''}`}
                style={{ width: `${live.target ? Math.min(100, (live.transferred / live.target) * 100) : 0}%` }}
              />
            </div>

            <div className="stat-foot" style={{ marginTop: '0.6rem' }}>
              {formatBytes(live.transferred)} of {formatBytes(live.target)} transferred
            </div>
          </div>

          {/*
            Under the dial rather than under the settings, so the control and the
            instrument it drives are the same object to look at. It doubles as
            Stop while a run is going, which is another reason it belongs beside
            the thing that is moving rather than beside the thing that is not.
          */}
          <div className="gauge-actions">
            <button className="chip chip--go" onClick={busy ? cancel : run}>
              {busy ? 'Stop' : 'Start test'}
            </button>
            {busy && (
              <span className="stat-sub" style={{ margin: 0 }}>
                {phase === 'latency' && 'Measuring latency…'}
                {phase === 'download' && 'Downloading…'}
                {phase === 'upload' && 'Uploading…'}
                {phase === 'saving' && 'Saving…'}
              </span>
            )}
          </div>
        </Card>
      </div>

      {phase === 'error' && (
        <div className="note">
          <strong>The test could not finish.</strong> {error}
          {' '}This needs a working internet connection and reachable access to
          <code>speed.cloudflare.com</code>; a VPN, a proxy or a content blocker can also refuse it.
        </div>
      )}

      {phase === 'done' && result && (() => {
        const bloat = Math.max(result.loadedLatencyMs, result.loadedUpLatencyMs) - result.latencyMs;
        if (bloat < 60) return null;
        return (
          <div className="note">
            <strong>Latency rose {bloat.toFixed(0)} ms while the link was busy.</strong> That is
            bufferbloat: something between here and Cloudflare &mdash; usually the router, sometimes
            the ISP &mdash; queues packets instead of dropping them when it runs out of capacity.
            It is why a call breaks up when someone starts a download, and it is invisible to a
            latency test run on an idle connection.
          </div>
        );
      })()}

      {phase === 'done' && result && result.downSeconds < 3 && (
        <div className="note">
          <strong>That run was too short to be reliable.</strong> The download lasted{' '}
          {result.downSeconds.toFixed(1)}s, which is not long enough for the connection to reach a
          steady state, so the figure reflects burst and buffering as much as throughput. On a
          fast link a small budget is spent before the ramp-up finishes &mdash; try a larger test
          size for a number worth quoting.
        </div>
      )}

      {/*
        Present from the start, like the dial, holding a hyphen until a run
        finishes. The detail lines keep their place as blanks so the rows do
        not grow, and push the table below them down, the moment results land.
        The charts are the exception to waiting: they fill live as each leg
        runs, since a shape forming is worth watching where a number is not.
      */}
      {(() => {
        const shown = phase === 'done' ? result : null;
        const blank = ' ';
        const ms = (v: number, digits = 0) => `${v.toFixed(digits)} ms`;
        const rise = (v: number) => `${v > 0 ? '+' : ''}${v.toFixed(0)} ms`;
        const loss = shown?.loss;
        const lossTotal = loss
          ? {
            sent: loss.idle.sent + loss.down.sent + loss.up.sent,
            lost: loss.idle.lost + loss.down.lost + loss.up.lost,
          }
          : null;
        const lossLine = (label: string, c: LossCount) => `${label} ${percent(c)} (${c.lost} of ${c.sent})`;
        return (
          <>
            <div className="grid grid--2">
              <Card delay={0}>
                <div className="stat-head">
                  <div className="stat-label">Download</div>
                  <StatIcon name="download" tone="down" />
                </div>
                <div className="stat-value stat-value--down">{shown ? formatRate(shown.downBps, units) : '-'}</div>
                <div className="stat-sub">{shown ? `peak second ${formatRate(shown.peakDownBps, units)}` : blank}</div>
                <div className="stat-foot">
                  {shown ? `${formatBytes(shown.downBytes)} over ${shown.downSeconds.toFixed(1)}s` : blank}
                </div>
                <SpeedChart points={series.down} units={units} tone="down" average={shown?.downBps} />
              </Card>
              <Card delay={60}>
                <div className="stat-head">
                  <div className="stat-label">Upload</div>
                  <StatIcon name="upload" tone="up" />
                </div>
                <div className="stat-value stat-value--up">{shown ? formatRate(shown.upBps, units) : '-'}</div>
                <div className="stat-sub">{shown ? `peak second ${formatRate(shown.peakUpBps, units)}` : blank}</div>
                <div className="stat-foot">
                  {shown ? `${formatBytes(shown.upBytes)} over ${shown.upSeconds.toFixed(1)}s` : blank}
                </div>
                <SpeedChart points={series.up} units={units} tone="up" average={shown?.upBps} />
              </Card>
            </div>

            <div className="grid grid--3">
              <Card delay={120}>
                <div className="stat-head">
                  <div className="stat-label">Latency</div>
                  <StatIcon name="latency" />
                </div>
                <div className="stat-value">
                  {shown ? <>{shown.latencyMs.toFixed(0)}<span className="stat-unit">ms</span></> : '-'}
                </div>
                <div className="stat-sub">{shown ? 'idle' : blank}</div>
                <div className="stat-foot">
                  {shown ? `download ${ms(shown.loadedLatencyMs)} (${rise(shown.loadedLatencyMs - shown.latencyMs)})` : blank}
                </div>
                <div className="stat-foot">
                  {shown ? `upload ${ms(shown.loadedUpLatencyMs)} (${rise(shown.loadedUpLatencyMs - shown.latencyMs)})` : blank}
                </div>
              </Card>
              <Card delay={180}>
                <div className="stat-head">
                  <div className="stat-label">Jitter</div>
                  <StatIcon name="jitter" />
                </div>
                <div className="stat-value">
                  {shown ? <>{shown.jitterMs.toFixed(1)}<span className="stat-unit">ms</span></> : '-'}
                </div>
                <div className="stat-sub">{shown ? 'idle' : blank}</div>
                <div className="stat-foot">{shown ? `download ${ms(shown.loadedJitterMs, 1)}` : blank}</div>
                <div className="stat-foot">{shown ? `upload ${ms(shown.loadedUpJitterMs, 1)}` : blank}</div>
              </Card>
              <Card delay={240}>
                <div className="stat-head">
                  <div className="stat-label">Packet loss</div>
                  <StatIcon name="loss" />
                </div>
                <div className="stat-value">{lossTotal ? percent(lossTotal) : shown ? 'n/a' : '-'}</div>
                <div className="stat-sub">
                  {loss ? lossLine('idle', loss.idle)
                    : shown?.device ? 'measured from the PC only'
                    : shown ? 'pings got no answer at all'
                    : blank}
                </div>
                <div className="stat-foot">{loss ? lossLine('download', loss.down) : blank}</div>
                <div className="stat-foot">{loss ? lossLine('upload', loss.up) : blank}</div>
              </Card>
            </div>
          </>
        );
      })()}

      {/*
        Both ends of the path, after the result rather than before the controls:
        the test is what the page is for, so it leads, and this card sits beside
        the previous runs as context for all of them.
      */}
      {meta && (
        <Card hover={false}>
          <CardTitle sub="Both ends of what is being measured, from speed.cloudflare.com/meta.">
            Connection
          </CardTitle>
          <div className="grid grid--3">
            <div>
              <div className="stat-label">Your network</div>
              <div className="stat-sub" style={{ color: 'var(--text)' }}>{meta.asOrganization}</div>
              <div className="stat-foot">AS{meta.asn} &middot; {place(meta.city, meta.region, meta.country)}</div>
            </div>
            <div>
              <div className="stat-label">Public address</div>
              <div className="stat-sub mono" style={{ color: 'var(--text)' }}>{meta.clientIp}</div>
              <div className="stat-foot">as seen from outside your router</div>
            </div>
            <div>
              <div className="stat-label">Serving datacentre</div>
              <div className="stat-sub" style={{ color: 'var(--text)' }}>
                {meta.colo?.city} ({meta.colo?.iata})
              </div>
              <div className="stat-foot">{meta.colo?.region} &middot; over {meta.httpProtocol}</div>
            </div>
          </div>
          <div className="stat-foot" style={{ marginTop: '1rem' }}>
            The datacentre is whichever one Cloudflare routes you to, so a result says as much
            about the path to it as about your line. A distant one is itself a finding.
          </div>
        </Card>
      )}
    </div>
  );
}

/**
 * Moves `target` bytes across `streams` parallel connections and reports the
 * rate as it goes.
 *
 * Parallel because a single TCP connection is often limited by window size and
 * distance rather than by the link, and would under-report a fast connection.
 *
 * Two figures come back. `average` is total bytes over wall time, which
 * includes the ramp-up and is the conservative, honest headline. `peak` is the
 * best one-second window, which is what the link managed once it got going.
 * Reporting only one of them would either flatter or understate the connection.
 */
async function transfer(options: {
  direction: 'down' | 'up';
  target: number;
  streams: number;
  signal: AbortSignal;
  onProgress: (bytesPerSecond: number, transferred: number, elapsedMs: number) => void;
}): Promise<{ average: number; peak: number; transferred: number; seconds: number }> {
  const { direction, target, streams, signal, onProgress } = options;

  const perStream = Math.ceil(target / streams);
  let transferred = 0;
  const started = performance.now();

  // One-second sliding window for the peak, sampled from the progress stream.
  const marks: { t: number; bytes: number }[] = [{ t: 0, bytes: 0 }];
  let peak = 0;

  // Bytes moved during the first second, subtracted out of the steady-state
  // figure below.
  const ramp = { bytes: 0 };

  const note = () => {
    const elapsed = performance.now() - started;
    if (elapsed >= 1000 && ramp.bytes === 0) ramp.bytes = transferred;
    marks.push({ t: elapsed, bytes: transferred });
    while (marks.length > 1 && elapsed - marks[0]!.t > 1000) marks.shift();

    const span = elapsed - marks[0]!.t;
    if (span > 250) {
      const rate = ((transferred - marks[0]!.bytes) * 1000) / span;
      // The first second is left out of the peak, as it is out of the average.
      // For an upload it is not just slow but wrong: progress counts bytes
      // handed to the browser's send buffers, which fill far faster than the
      // line drains them, and a run measured at 40 Mbps once peaked at 136.
      if (marks[0]!.t >= 1000 && rate > peak) peak = rate;
      onProgress(rate, transferred, elapsed);
    }
  };

  // A slow link must not hold the page hostage. When the leg times out the
  // transfer is cut short, and whatever moved is still a valid measurement of
  // the time it covers -- so the abort ends the leg rather than failing it.
  const legController = new AbortController();
  const onOuterAbort = () => legController.abort();
  signal.addEventListener('abort', onOuterAbort);
  const legTimeout = setTimeout(() => legController.abort(), LEG_TIMEOUT_MS);

  try {
    await Promise.all(
      Array.from({ length: streams }, async () => {
        if (direction === 'down') {
          const response = await fetch(
            `${HOST}/__down?bytes=${perStream}&r=${Math.random()}`,
            { cache: 'no-store', signal: legController.signal },
          );
          if (!response.ok || !response.body) throw new Error(`Server answered ${response.status}.`);

          const reader = response.body.getReader();
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            transferred += value.byteLength;
            note();
          }
        } else {
          await uploadChunk(perStream, legController.signal, (delta) => {
            transferred += delta;
            note();
          });
        }
      }),
    );
  } catch (e) {
    // An abort from the leg timeout is an expected end, not a failure. An abort
    // from the user, or any other error, is not ours to swallow.
    if (!legController.signal.aborted || signal.aborted) throw e;
  } finally {
    clearTimeout(legTimeout);
    signal.removeEventListener('abort', onOuterAbort);
  }

  const seconds = Math.max(0.001, (performance.now() - started) / 1000);

  // Where the leg ran long enough to have a steady state, measure it rather
  // than the whole thing: the first second is TCP opening its window, and
  // including it drags the headline below what the link actually sustained.
  // Below that, there is no steady state to find and the whole window is all
  // there is -- which is precisely why a short run gets flagged in the UI.
  const RAMP_SECONDS = 1;
  const ramped = ramp.bytes > 0 && seconds > 2
    ? (transferred - ramp.bytes) / (seconds - RAMP_SECONDS)
    : transferred / seconds;

  return { average: ramped, peak: peak || transferred / seconds, transferred, seconds };
}

/**
 * Uploads a chunk, reporting progress.
 *
 * XMLHttpRequest rather than fetch: fetch still has no upload progress event in
 * browsers, so a fetch-based upload can only be timed end to end and would show
 * a frozen readout for the whole leg.
 */
function uploadChunk(
  bytes: number,
  signal: AbortSignal,
  onDelta: (delta: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    // One block of noise, reused. Incompressible so a proxy cannot deflate it
    // and flatter the result.
    const block = new Uint8Array(64 * 1024);
    crypto.getRandomValues(block);
    const copies = Math.max(1, Math.ceil(bytes / block.byteLength));
    const body = new Blob(Array.from({ length: copies }, () => block));

    const xhr = new XMLHttpRequest();
    let last = 0;

    xhr.upload.onprogress = (e) => {
      onDelta(e.loaded - last);
      last = e.loaded;
    };
    xhr.onload = () => (xhr.status >= 200 && xhr.status < 400
      ? resolve()
      : reject(new Error(`Server answered ${xhr.status}.`)));
    xhr.onerror = () => reject(new Error('The upload connection failed.'));
    xhr.onabort = () => resolve();

    signal.addEventListener('abort', () => xhr.abort());

    xhr.open('POST', `${HOST}/__up?r=${Math.random()}`);
    xhr.send(body);
  });
}

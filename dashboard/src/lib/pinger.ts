import 'server-only';
import { spawn, type ChildProcess } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { createInterface } from 'node:readline';

/**
 * Packet loss, measured with ICMP from the dashboard's own process.
 *
 * The browser cannot do this. Everything it sends is TCP, and TCP repairs a
 * lost packet before the page ever hears of it -- loss only surfaces as a
 * slower transfer. A ping is not repaired, so a ping that never comes back is
 * a packet that was lost. Measuring it here rather than in the browser still
 * crosses the same link, since the browser and this server are one machine.
 *
 * Windows `ping.exe` sends one echo a second, which over a ten-second leg is
 * ten samples: one loss would read as 10%. So this drives .NET's Ping class
 * from a PowerShell child instead, at ten a second, each echo in flight
 * independently so a lost one does not stall the ones behind it.
 *
 * The browser marks which phase the test is in (idle, download, upload), and
 * each echo is counted against the phase that was active when it was SENT.
 */

export type LossPhase = 'idle' | 'down' | 'up';
export interface LossCount { sent: number; lost: number }
export type LossResult = Record<LossPhase, LossCount>;

/** The host the transfer legs use, so the echoes follow the same path. */
const TARGET = 'speed.cloudflare.com';
const INTERVAL_MS = 100;
const TIMEOUT_MS = 1000;

/** However the browser leaves (closed tab, lost network), a pinger dies by itself. */
const MAX_LIFETIME_MS = 90_000;

// One line per event: 'ready', 's <seq>' on send, 'r <seq>' on a reply, 'l <seq>'
// on a loss. ASCII only, for the same reason as scripts/*.ps1.
const SCRIPT = `
$ErrorActionPreference = 'Stop'
$all = [System.Net.Dns]::GetHostAddresses('${TARGET}')
$addr = $all | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
if (-not $addr) { $addr = $all | Select-Object -First 1 }
if (-not $addr) { [Console]::Out.WriteLine('fail'); exit 1 }
[Console]::Out.WriteLine('ready')
$pending = New-Object System.Collections.ArrayList
$seq = 0
while ($true) {
  $p = New-Object System.Net.NetworkInformation.Ping
  $t = $null
  try { $t = $p.SendPingAsync($addr, ${TIMEOUT_MS}) } catch { $t = $null }
  [Console]::Out.WriteLine('s ' + $seq)
  if ($t -eq $null) { [Console]::Out.WriteLine('l ' + $seq); $p.Dispose() }
  else { [void]$pending.Add(@($seq, $p, $t)) }
  $seq++
  Start-Sleep -Milliseconds ${INTERVAL_MS}
  for ($i = $pending.Count - 1; $i -ge 0; $i--) {
    $e = $pending[$i]
    if ($e[2].IsCompleted) {
      if (-not $e[2].IsFaulted -and $e[2].Result.Status -eq 'Success') { [Console]::Out.WriteLine('r ' + $e[0]) }
      else { [Console]::Out.WriteLine('l ' + $e[0]) }
      $e[1].Dispose()
      $pending.RemoveAt($i)
    }
  }
}
`;

interface Session {
  child: ChildProcess;
  phase: LossPhase | null;
  /** seq -> the phase it was sent in. Echoes sent between phases are not counted. */
  sent: Map<number, LossPhase>;
  /** seq -> whether it came back. */
  answered: Map<number, boolean>;
  onLine: (() => void) | null;
  expiry: NodeJS.Timeout;
}

// On globalThis so a dev-server reload of this module does not orphan a pinger
// that the previous copy started.
const g = globalThis as unknown as { __pingers?: Map<string, Session> };
const sessions = (g.__pingers ??= new Map<string, Session>());

function end(id: string): void {
  const s = sessions.get(id);
  if (!s) return;
  clearTimeout(s.expiry);
  s.child.kill();
  sessions.delete(id);
}

/** Starts pinging, already counting as 'idle'. Resolves once the first echo can go out. */
export function startPinger(): Promise<{ ok: true; id: string } | { ok: false; error: string }> {
  if (process.platform !== 'win32') {
    return Promise.resolve({ ok: false, error: 'Packet loss is measured with Windows PowerShell.' });
  }

  // One test at a time. A pinger left over from an abandoned run would add its
  // own echoes to the link this one is measuring.
  for (const id of [...sessions.keys()]) end(id);

  const encoded = Buffer.from(SCRIPT, 'utf16le').toString('base64');
  const child = spawn(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
    { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] },
  );

  const id = randomUUID();
  const session: Session = {
    child,
    phase: 'idle',
    sent: new Map(),
    answered: new Map(),
    onLine: null,
    expiry: setTimeout(() => end(id), MAX_LIFETIME_MS),
  };
  sessions.set(id, session);

  return new Promise((resolve) => {
    let settled = false;
    const settle = (r: { ok: true; id: string } | { ok: false; error: string }) => {
      if (settled) return;
      settled = true;
      if (!r.ok) end(id);
      resolve(r);
    };

    const startup = setTimeout(() => settle({ ok: false, error: 'The pinger did not start.' }), 8000);

    createInterface({ input: child.stdout! }).on('line', (line) => {
      const [kind, n] = line.trim().split(' ');
      const seq = Number(n);
      if (kind === 'ready') {
        clearTimeout(startup);
        settle({ ok: true, id });
      } else if (kind === 'fail') {
        settle({ ok: false, error: `Could not resolve ${TARGET}.` });
      } else if (kind === 's') {
        if (session.phase) session.sent.set(seq, session.phase);
      } else if (kind === 'r' || kind === 'l') {
        session.answered.set(seq, kind === 'r');
      }
      session.onLine?.();
    });

    child.on('error', () => settle({ ok: false, error: 'Could not run PowerShell.' }));
    child.on('exit', () => {
      clearTimeout(startup);
      settle({ ok: false, error: 'The pinger stopped.' });
      session.onLine?.();
    });
  });
}

export function markPinger(id: string, phase: LossPhase): boolean {
  const s = sessions.get(id);
  if (!s) return false;
  s.phase = phase;
  return true;
}

/**
 * Stops counting, waits for the echoes still in flight, and tallies.
 *
 * The wait matters. A reply arrives in milliseconds but a loss is only known
 * once the timeout has run out, so cutting off immediately would drop the
 * in-flight echoes that were about to be declared lost -- a sample biased
 * towards the ones that came back.
 */
export async function stopPinger(id: string): Promise<LossResult | null> {
  const s = sessions.get(id);
  if (!s) return null;
  s.phase = null;

  const outstanding = () => [...s.sent.keys()].some((seq) => !s.answered.has(seq));
  if (outstanding() && s.child.exitCode === null) {
    await new Promise<void>((resolve) => {
      const done = () => {
        clearTimeout(limit);
        s.onLine = null;
        resolve();
      };
      const limit = setTimeout(done, TIMEOUT_MS + 500);
      s.onLine = () => { if (!outstanding() || s.child.exitCode !== null) done(); };
    });
  }

  const result: LossResult = {
    idle: { sent: 0, lost: 0 },
    down: { sent: 0, lost: 0 },
    up: { sent: 0, lost: 0 },
  };
  // If the pinger itself died, an unanswered echo says nothing about the link,
  // so it is left out rather than counted as lost.
  const died = s.child.exitCode !== null;
  for (const [seq, phase] of s.sent) {
    const answered = s.answered.get(seq);
    if (answered === undefined && died) continue;
    result[phase].sent++;
    // Never answered at all counts as lost: it outlived its own timeout.
    if (answered !== true) result[phase].lost++;
  }

  end(id);
  return result;
}

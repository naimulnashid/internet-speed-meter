# Localhost dashboard — evaluation and plan

Status: **all five phases built, plus an active speed test (Phase 6).** Written 2026-09-01.

**Update 2026-09-24:** the site has since been cut down to two pages, **Speed** and **Test**. The
Now, History and Health pages were removed, along with the percentiles, the speed distribution,
the averages and the hour-of-day heatmap. On real data those described what the machine happened
to be doing (mostly idle browsing) rather than what the connection could do. Speed keeps the
fastest seconds, the best sustained 10 s / 1 min / 5 min and the daily peak chart. The rest of
this document is the original plan, kept as a record of the reasoning.

A localhost dashboard for the Internet Speed Meter, in the shape of the Data Usage Tracker that
already runs on port 7843.

## Why this is worth building

The Data Usage Tracker answers **how much, by which app, surviving a Windows reset**. It reads
SRUM, which buckets hourly. A 300 Mbps burst lasting 90 seconds is structurally invisible
there — it only makes an hour slightly bigger.

This meter measures the one thing SRUM can never give: **rate, at 1 Hz**. Nothing on Windows
records that. The two datasets do not overlap, so this is a second dashboard with its own
subject, not a second view of the same numbers.

Scope it as a **speed and connection-quality** dashboard. The moment "GB per day" becomes a
headline figure, there are two localhost dashboards disagreeing about bytes and no way to tell
which one is lying.

### The timing argument

`TrayApp` keeps sixty samples in a `List<double>` and drops them on restart. Every day without
a recorder is a day of history that cannot be reconstructed. **Phase 1 is worth landing even if
the dashboard is never built.**

### What this costs

The project's headline claim is "no runtime to install, one ~50 KB exe, no dependencies." A
Next.js app in the repo dilutes that. Three things keep it honest:

- The `.exe` gains no dependency. It writes plain fixed-record binary files; that is the entire
  interface between the two halves.
- `--export-csv` ships in Phase 1, so the data is fully usable with no Node installed at all.
- The dashboard lives in `dashboard/`, is optional, and the README says so plainly.

### The caveat that belongs on the page, not in a footnote

Passive metering sees only **what was actually transferred**. If nothing ever pulls hard, the
recorded peak reflects the fastest thing that happened to run — it is not the line rate. Every
figure is labelled *observed*. Nothing on the site may read like a speed test result.

## What the dashboard presents

### Live

Current down/up at size, a 60 second and a 10 minute sparkline, the adapter being metered,
session uptime. The existing flyout, with room to breathe.

### Speed statistics — the reason it exists

| Metric | Why this one |
| --- | --- |
| **Fastest second**, down and up, with its timestamp | The classic peak. Without the timestamp it is unfalsifiable. |
| **Sustained peak: best 10 s / 60 s / 5 min rolling average** | The honest throughput number. A one-second max is buffer and burst; a sixty-second sustained average is what the connection actually does. |
| **p50 / p95 / p99 of active samples** | A single max can be a driver glitch. p95 is the number worth quoting as "this connection does about X". |
| **Average while active**, threshold configurable and shown | A bare average is meaningless — most seconds are near zero, so the wall-clock mean lands around 40 KB/s. Show both, labelled. |
| **Speed distribution histogram**, log buckets | The shape: how often at zero, at 2 MB/s, at line rate. Answers more than the four rows above it. |

### Time and behaviour

- **Hour-of-day × day heatmap** of median or peak speed. The most actionable thing a speed
  history can show — ISP congestion at 9pm appears here and nowhere else. The Data Usage
  Tracker's `ActivityHeatmap` ports over nearly unchanged.
- Active versus idle time per day; weekday versus weekend.
- Transfer sessions: count, and longest continuous transfer, splitting on an idle gap.

### Connection quality

`Reading.Connected` already exists in `NetMonitor` and costs nothing to record.

- Disconnect events with duration, giving daily link uptime.
- Adapter switches and time per adapter. This matters more than it looks: `auto` mode follows
  the busiest adapter, so without an adapter id on every sample the history silently conflates
  Wi-Fi and Ethernet.
- Stalls — zero-throughput seconds inside an otherwise active transfer.
- Throughput variance during active periods.

### Volume — present, deliberately demoted

Bytes per day and hour, summed from the one-second deltas, with both caveats on the card
itself: this is NIC-level and includes LAN traffic, and **it only counts while the meter was
running**. Meter coverage for the same period sits beside it, and the card links to the Data
Usage Tracker as the authoritative answer for volume.

### Meter health

Coverage percentage per day, derived from missing minutes. This is the analogue of the Data
Usage Tracker's Sync Status page, and it is what makes every other number on the site
interpretable.

### Deliberately out of scope

- **Per-app breakdown.** Not derivable from IP Helper counters without ETW or packet capture.
  That is the Data Usage Tracker's job.
- ~~**Latency and ping.**~~ **Reversed on 2026-09-01, deliberately.** This was listed as out of
  scope because "no network calls of its own" is a stated property of the app, and an active
  probe changes what it is. That reasoning still holds for the `.exe`, which remains purely
  passive. It does not hold for an optional dashboard the user chose to install: a `/speedtest`
  page now measures actively against speed.cloudflare.com, on a button press only, never on a
  schedule, with the data cost stated before it is spent. See Phase 6.

## Decisions taken

| Decision | Choice |
| --- | --- |
| Shipped defaults | `%LocalAppData%\InternetSpeedMeter\history` and `...\raw`. Neutral, because a hard-coded drive letter works on one machine and silently disables recording everywhere else. On the system drive, so a reset destroys them — stated in the README rather than worked around. |
| This machine | Minute rollups in a folder on the data drive, beside the Data Usage and Screen Time histories: it survives a Windows reset, and Google Drive already syncs the parent. Raw goes to a folder **outside** that synced tree — the same split the Data Usage Tracker makes with `scratchDir`, since a file discarded after 14 days would otherwise upload a copy of itself forever. Both are pinned in `settings.ini`, not in the code. |
| Dashboard location | `dashboard/` inside this repo. |
| Port | 7845. The Data Usage Tracker holds 7843. |

## Phase 1 — Recording, inside the exe

**Built.** `src/Recorder.cs` writes both files, `src/LogExport.cs` reads them back as CSV,
`Settings` carries the five new keys, and `TrayApp.OnTick` files every reading. No SQLite, no
NuGet — plain `FileStream`, and the exe stays dependency-free.

All integers are **little-endian**, written by hand rather than through `BitConverter`, so the
format states its own byte order instead of inheriting the writer's. Flag bits, shared by both
record types: `1` connected, `2` gap, `4` adapter changed. On a minute record, `2` means at
least one gap fell inside it and `4` means the adapter changed partway through, in which case
`adapterIx` is the one in force at the end; `8` is minute-only and means the meter started
inside that minute, so a low `samples` count there is a restart, not lost ticks.

Records are append-ordered and normally monotonic in time. A backwards clock step can break
that, so a reader must sort rather than assume.

### Two files, two purposes, two locations

This is the central design decision. The two files differ in retention, in value, and therefore
in where they live.

`<rawfolder>\YYYY-MM-DD.bin` — 16-byte fixed records, one per second, pruned after 14 days.
Disposable and unsynced:

```
uint32 unixSeconds     uint32 downBytes     uint32 upBytes
uint16 elapsedMs       uint8  adapterIx     uint8  flags
```

1.38 MB/day, about 19 MB retained. It drives the fine-grained views — the live chart, burst
shape, sub-minute stalls — and nothing older than a fortnight depends on it.

`<logfolder>\YYYY-MM.bin` — 40-byte records, one per minute, **kept forever**, and the file the
history actually lives in:

```
uint32 unixMinute      uint64 downBytes     uint64 upBytes
uint32 maxDownBps      uint32 maxUpBps      <- peak 1 s sample within the minute
uint16 samples         uint16 activeSamples <- coverage, for free
uint8  adapterIx       uint8  flags
```

57 KB/day, about 21 MB/year. Adapter names live in a sidecar `adapters.tsv` beside the minute
files, indexed by `adapterIx` — the raw records reference the same table, so it must sit with
the half that is never pruned. It is tab-separated, UTF-8 without a BOM, CRLF, `#` starts a
comment, and the columns are index, adapter id, adapter name.

Carrying **max** rather than only a sum in the minute rollup is what lets long-range peak
queries work without retaining raw seconds. And because the exe writes the minute file itself,
history survives even if the dashboard is never installed — which sidesteps the lesson the Data
Usage Tracker learned the hard way, that history dies whenever the collector stops running.

### Details that will bite otherwise

- **Sleep and wake.** `NetMonitor._clock` is a `Stopwatch`, so one window can span an hour
  across a resume. The *rate* stays sane because deltas are divided by measured elapsed time,
  but the *bytes* would land in a single one-second bucket. Reject any window over about 3
  seconds and write a flagged gap marker instead.
- **Buffer, flush once a minute.** One disk write per minute rather than per second keeps the
  "light on resources" claim true on a laptop. Crash cost is at most 60 seconds of raw samples.
- **Store UTC epoch, bucket to local dates at read time.** The Data Usage Tracker's schema
  comments record exactly this trap: bucketing off UTC shifts every early-morning hour onto the
  wrong day at UTC+6.
- **Record the adapter index on every sample**, or `auto` mode quietly merges two networks into
  one history.
- **Counter resets** are already clamped in `NetMonitor.Sample`; nothing more is needed there.

### New settings keys

Written to `%AppData%\InternetSpeedMeter\settings.ini` as usual — settings stay local even
though the data does not.

| Key | Default |
| --- | --- |
| `Record` | `true` |
| `LogFolder` | `%LocalAppData%\InternetSpeedMeter\history` |
| `RawFolder` | `%LocalAppData%\InternetSpeedMeter\raw` |
| `RawRetentionDays` | `14` |
| `ActiveThresholdBps` | 50 KB/s |

The two folders fail independently, and not symmetrically.

If `RawFolder` is unwritable, **minute recording continues** — the history is the half that
matters, and losing burst detail for a session is a small, self-repairing loss. If `LogFolder`
is unwritable — the drive absent, say — recording disables itself entirely for the session, on
the grounds that raw samples with no rollup to graduate into are 1.38 MB/day of write traffic
buying nothing. Either way it is noted once in `error.log`, and neither may take the tray icon
down with it.

Setting `RawFolder` empty is the supported way to run minutes-only: no raw file, no 14-day
churn, and everything except the sub-minute views still works.

### Also in Phase 1

`--export-csv <from> <to> [file] [--raw]`, in the same shape as the existing `--sample` mode.
The data becomes useful immediately, with no dashboard and no Node.

Two things the exporter settles that the dashboard will inherit. It sorts each file's records by
timestamp rather than trusting append order, honouring the contract above. And it opens with
`FileShare.ReadWrite`, so a reader never blocks the meter mid-write — the dashboard reads a live
file constantly, and a locked write would drop samples on the floor.

## Phase 2 — Dashboard skeleton

**Built.** `dashboard/` in this repo: Next.js 15, React 19, recharts, one Overview page reading
the minute files directly. `npm --prefix dashboard run dev`, or the `speed-meter-dashboard`
entry in `.claude/launch.json`.

**Port 7845, not 7844** — 7844 was already taken by the Screen Time Tracker. 7843 is the Data
Usage Tracker.

The dashboard has no configuration of its own. It reads
`%AppData%\InternetSpeedMeter\settings.ini` for the folder paths, the active threshold and the
bytes/bits choice, so the web page and the taskbar readout cannot disagree, and there is no
second copy of the paths to keep in sync.

Next.js 15, React 19, recharts, port 7845. `.gitignore` gains
`dashboard/node_modules/`, `dashboard/.next/`, `dashboard/tsconfig.tsbuildinfo`.

Copy `Card`, `CountUp`, `ScopeBar`, `Skeleton` and `globals.css` from the Data Usage Tracker.
Fork its `format.ts` for **rates**, with a bits/bytes toggle mirroring the meter's own `Units`
setting — the two should never disagree about what `Mbps` means.

Read the minute files directly with a `DataView`. No database yet. The goal of this phase is
real numbers on screen, end to end.

## Phase 3 — SQLite cache

**Built**, ahead of the trigger rather than because of it — at this size direct reads are still
instant, but minutes accumulate without limit and the work only grows.

`node:sqlite`: built in to Node 22.5+, no node-gyp, no native module to break after a reset.
`engines` is raised to match, and it is the dashboard's only hard version requirement.

**Only minutes are cached.** The raw log is capped by its retention window — a fortnight, and
never more — so the work of scanning it has a ceiling. Minutes are kept forever. That difference
is the whole reason one is worth caching and the other is not.

**The cache is never a second copy of the history.** Every row is derived from the `.bin` files
and nothing else, so deleting the database loses nothing and it rebuilds on the next request.
That is a deliberately stronger property than the Data Usage Tracker has, where the database
*is* the history. `readMinutes` falls back to parsing the files whenever the cache returns null,
so a broken cache costs speed and never correctness. A `SCHEMA_VERSION` bump discards every row
rather than serving columns whose meaning has changed.

**Ingest reads only the tail.** Records are fixed-width and append-only, so the offset of the
first unseen record is exactly `rows_ingested * 40`. Picking up the minute just appended to the
current month file reads 40 bytes, not the month. A file that shrank is re-read from the start,
since the writer never truncates and an offset into a different file would be silently wrong.

It lives in `%LocalAppData%\InternetSpeedMeter\cache` — not beside the minute files, for the
same reason the raw log is not: it is rebuildable and rewritten constantly, so it has no business
in a folder that may be pointed at cloud sync.

Verified against the C# exporter, which reads the same `.bin` files through a completely
independent implementation. All seven aggregates over a day matched exactly — row count, both
byte totals, both peaks, samples and active samples — including an upload total above 2^32,
which exercises the split-u64 read. Incremental ingest was confirmed advancing 75 to 77 rows on
80 new bytes.

Same `node:sqlite` approach as the Data Usage Tracker: built in to Node 22.5+, no node-gyp, no
native module to break after a reset. One table mirroring the minute record, a `days` rollup,
and a `link_events` table.

Critically, **this database is a cache, fully rebuildable from the binary files**. Deleting it
loses nothing. That is a materially safer property than the Data Usage Tracker has, where the
database is the only copy of the history.

## Phase 4 — The pages

**Built.**

| Route | Content |
| --- | --- |
| `/` | Now |
| `/speed` | Peaks, sustained windows, percentiles, distribution |
| `/history` | Hour-of-day heatmap, daily peaks, per-adapter split, volume with its caveats |
| `/health` | Coverage, sample density, absences, adapter switches, sleep gaps |

Scope bar across all but Now: Today / 7d / 30d / 90d / All. No adapter filter yet — with one
adapter on this machine it would be a control with a single option.

### What the recorder's write cadence costs the Now page

The recorder buffers a minute and writes once at each boundary, to keep a laptop's disk asleep.
So there is nothing newer than the last flush to read, and **Now can be up to a minute behind
the taskbar readout**. It polls every five seconds — faster would not produce fresher numbers —
and states the age of its newest sample rather than implying a live feed. Fixing it properly
would mean writing every second, which the recording deliberately refuses.

### Two places where honesty needed explicit code

**Unrecorded hours on the heatmap** get their own flat colour, off the green ramp. "The meter
was off" and "the connection was idle" are different claims, and a ramp that renders them
identically manufactures evidence about the network out of the meter's own downtime.

**Sample density excludes boundary minutes.** The minute the meter starts in is partial by
construction — start at 20:00:30 and it holds 30 samples, not 60 — and counting those makes a
healthy meter read as dropping ticks.

The first attempt excluded a minute only when a neighbour was missing, which left ~96.8% and
looked like real tick loss. It was not. Measured across a day: **every one of the 51 genuinely
whole minutes held exactly 60 samples**, and each deficient minute was a meter start or stop.
Two were restarts *inside a single minute*, where both neighbours are present and the count is
simply low — indistinguishable from dropped ticks by sample count alone.

So the recorder states it instead of leaving it to be inferred: flag bit `8` on a minute record
means the meter started partway through that minute, and `health.ts` excludes those. Nothing was
wrong with the timer, and the compensating-interval change written to "fix" it was reverted once
the measurement contradicted the diagnosis. Records written before the flag existed cannot be
labelled retroactively, so a window containing them still reads a little under 100%.

### The raw log is streamed, never collected

`rawStats` walks the raw log in **one pass**, holding a day's buffer and a few hundred numbers.
It used to materialise every record as an object and then walk the array three more times, once
per sustained window. That was fine at an hour of data and had a ceiling arriving on a schedule:
the retention window guarantees 1,209,600 records within a fortnight of continuous recording,
which is on the order of a hundred megabytes of objects to compute a handful of aggregates.

`forEachRaw` passes fields as primitives so the loop allocates nothing per sample, and the three
rolling windows advance together as ring buffers instead of three re-scans. Order is still not
trusted: each file's records are index-sorted before visiting, so only the order is held, never
the records.

Verified by rendering `/speed` from both implementations against a frozen raw log — the meter
was stopped so the input could not change — and diffing the full page text. Identical, every
value.

Two semantics did change, in edge cases that data did not contain, and both make the result more
correct. The sustained average now averages **rates** rather than raw byte deltas, so a window
that was 1,020 ms rather than 1,000 is divided by what it actually measured. And a flagged sleep
gap now ends a run without contributing to it, where before its zero-byte record was counted as
the first sample of the next run and dragged that average down.

### Percentiles are limited by raw retention

They need individual seconds, so they cover only the days still inside the raw window. Asked for
a longer range, `/speed` reports on what it has and says so, rather than quietly answering a
different question from the one in the URL. They are computed from a log histogram — twelve
buckets per decade, constant memory — because a fortnight of seconds is 1.2 million records and
holding them all to rank exactly would buy precision finer than anyone acts on.

## Phase 5 — Running it

**Built.** `.claude/launch.json` for development; `start-dashboard.bat` to run it by hand and
watch the output; `scripts/install-autostart.ps1` to register a **Start Speed Meter Dashboard** logon
task that starts it hidden, via `dashboard-hidden.vbs`. `scripts/dashboard-stop.ps1` stops it,
since a windowless server has no Ctrl+C.

The launch chain is the Data Usage Tracker's, and each link earns its place:

- **A VBS wrapper**, because there is no reliable way to start a console program from Task
  Scheduler without a window flashing up — the task's own *Hidden* checkbox does not suppress
  it and `powershell -WindowStyle Hidden` still blinks. `WScript.Shell.Run` with window style 0
  genuinely does not create one.
- **A logon trigger running as the user, unelevated**, so Windows never stores a password and no
  admin rights are needed.
- **Battery defaults inverted.** Task Scheduler's defaults are built for short maintenance jobs
  and actively fight a long-running server: it would refuse to start on battery and kill it after
  three days. `AllowStartIfOnBatteries`, `DontStopIfGoingOnBatteries` and a zero
  `ExecutionTimeLimit` turn that off. Verified on the registered task.
- **A staleness check before serving.** The service rebuilds whenever anything under
  `dashboard/src` is newer than `.next/BUILD_ID`. A launcher that builds once and serves stale
  code forever is a quiet, convincing kind of wrong in a dashboard whose whole job is reporting
  current numbers.

Everything goes to `logs/dashboard.log` (gitignored), which is the only way to see why a
windowless service did not come up.

The scripts are **ASCII only**, deliberately. Windows PowerShell 5.1 reads a BOM-less `.ps1` as
ANSI, where a UTF-8 em-dash decodes to CP1252 `0x94` = U+201D — which PowerShell accepts as a
string delimiter, closing a string early and silently changing the logic of the enclosing block.
Verified: zero non-ASCII bytes, and all three scripts parse clean under 5.1 rather than only
under PowerShell 7.

None of this touches recording. The meter writes to disk whether the dashboard is installed,
running or removed, and `-Remove` loses no history. It is also unrelated to the tray menu's
**Start with Windows**, which is the meter's own `HKCU\...\Run` entry.

## Phase 6 — Active speed test

**Built**, and it reverses a decision this document made. Active probing was listed as out of
scope because "no network calls of its own" is a stated property of the app. That reasoning
holds for the `.exe`, which is still purely passive and always will be. It does not hold for an
optional dashboard someone installed on purpose.

`/speedtest` measures against `speed.cloudflare.com` — public, CORS-friendly, no key. Three
sizes are offered rather than one chosen for the reader, because the only real cost of a speed
test is data, and how much is worth spending is not ours to decide: Standard 125 MB, Thorough
310 MB, Heavy 620 MB. The figure is stated on the button before it is spent. Light (33 MB) was
dropped as too small to measure a fast link — it never left the ramp-up — but stays in the stored
type, because results written while it existed are still in the file and a reader that cannot
name them is a reader that discards them.

Measured in the browser, not on the server: the transfer has to cross the same path the user
browses over, and a server-side test would measure the same machine by a different route.

**Results are original data, so they are not in the cache.** Nothing here can be regenerated
from the `.bin` files — a run at 21:40 cannot be re-run at 21:40 — so they sit beside the
minute log and are as worth surviving a reset as the history is. They are **append-only JSONL**
rather than SQLite, because that folder may be inside cloud sync, and a WAL-mode database there
is three files that must be mutually consistent to restore. One line of JSON per run has no such
problem and opens in a text editor.

### The cross-check that justifies the page existing here

Every stored run is shown beside **what the meter recorded at the adapter during the same
minutes** — a completely independent measurement, from outside the browser. On the first real
runs the browser reported 290 Mbps while the meter saw 433 Mbps, which is the expected shape:
the meter catches the true one-second peak and counts every byte crossing the NIC, while the
browser reports an average over its whole leg. Disagreement in the other direction would mean
the recording missed the window. fast.com cannot tell you this.

### The gauge

A speedometer, on a **logarithmic** scale. That is the whole design decision. A domestic
connection runs anywhere from a few hundred Kbps to a gigabit, and on a linear dial sized for the
fast case every ordinary speed sits pinned against the stop, indistinguishable from every other
ordinary speed. Four decades of arc put 10 and 100 Mbps in visibly different places, which is the
comparison anyone actually wants. The cost is that half way round is not half the speed, so the
labelled decade ticks are load-bearing rather than decoration.

### Single versus parallel

Both are offered, because they answer different questions. Parallel asks how fast the **line**
is, which is what a speed test usually means. A single connection asks how fast **one transfer**
is, and the two diverge when the limit is the bandwidth-delay product rather than the link —
which is the usual reason one download feels slower than the connection being paid for. Measured
here: 239 Mbps on one connection against 290 to 347 Mbps in parallel, on the same line within
minutes.

The count is stored per run, so the history says which question a row answered.

### The datacentre cannot be chosen

Cloudflare is anycast: the colo that answers is decided by BGP routing from wherever the request
enters their network, and `speed.cloudflare.com` takes no parameter to override it. The only
lever from this side is a VPN, which changes where the request appears to come from — and then
measures the path to the VPN rather than the line. Picking a server the way Ookla does would mean
a backend with many separately addressable endpoints, which is a different service, not a setting.

### Latency, loaded and idle

An idle ping is the least useful latency number a speed test can report, because an idle link
has no queue. The run now probes continuously **during the download**, and the difference between
that and the idle figure is bufferbloat: how much a busy connection delays everything else on it.
It is what predicts whether a call breaks up when someone starts a download, and a run over
60 ms of increase says so in plain words rather than leaving two numbers to be compared.

**Jitter was wrong, and measuring it properly showed how.** It averaged the consecutive
differences, and a mean is destroyed by one outlier: a single stalled probe among nine reported
158 ms on a link whose round trips were measurably 27 to 36 ms apart. It is the median of those
differences now. On the same data a 430 ms spike moves the mean to 82.6 ms and the median to
5.7 ms, which is the property wanted from a figure claiming to be typical. It reads 3.9 ms
against a hand-measured 3.8 ms.

### Both ends of the connection

`speed.cloudflare.com/meta` names them: ISP and ASN, the client's public address and coarse
location, and which Cloudflare datacentre answered. Fetched on mount rather than at run time, so
the page can say who is at each end before spending anything to find out, and stored with each
result — an ISP change or a different datacentre is exactly what explains why one week's numbers
differ from another's.

Worth knowing: `/meta` answers a browser and returns 403 to curl, while `/cdn-cgi/trace` answers
both. The richer of the two is the one that refuses a command line, which is a mildly annoying
thing to discover by testing the wrong one first.

### The comparison had a bug worth recording

"Meter saw" reported **656 Kbps** for a run the browser measured at 234 Mbps, which reads as the
two measurements flatly contradicting each other. Neither was wrong. The run happened at 22:35:01
and the recorder had not yet flushed minute 22:35 — it writes once a minute — so the lookup
window clipped the last five seconds of minute 22:34 and reported *that* minute's unrelated
background traffic as what the meter saw.

Now every minute a run touched must be present or the cell reads a dash. A test therefore
finishes before its own comparison does, and the footnote says so. With the fix the same runs read
234 against 211, 201 against 174, and 339 against 410 — agreement, which is what the column was
for.

### What measuring taught, immediately

The first run finished in **0.99 seconds** and reported 212 Mbps. That is not a speed test, it
is a burst measurement: TCP never left its ramp-up. Two fixes came out of it.

Latency now discards the first **three** probes rather than one — the first pays for DNS, TCP
and TLS, the next two land while HTTP/2 is still settling. Counting them put measured jitter at
96 ms against a 39 ms median, describing connection setup rather than the connection. It reads
56 ms now.

The headline rate now **excludes the first second** where a leg ran longer than two, so it
reports the steady state rather than the ramp. That alone moved a Standard run from 212 to
290 Mbps.

Neither fixes the underlying fact: on a fast link, a small data budget is spent before the
connection reaches a steady state. That is arithmetic, not a bug. So a run shorter than three
seconds is **flagged as unreliable** on the result and marked with a warning in the history,
rather than presented as a number worth quoting.

## Phase 7 — The site password

**Built**, and off unless asked for. A login on `localhost` protects a single-user machine from
itself, which is nothing; it starts mattering the moment the port stops being yours alone — a
shared PC, a second account, a tunnel or port forward opened so the dashboard can be read from a
phone. That is also exactly the moment nobody remembers to add one, so the cost of setting it had
to be near zero: one line in `dashboard/.env.local`.

    DASHBOARD_PASSWORD=something-long

Unset or empty means open, set means required, and set to something under eight characters means
**shut** — with the sign-in page explaining why. A typo quietly disabling the gate would be the
one failure mode nobody would ever notice, so there is no branch here that falls open.

### The first version hashed it, and this one does not

It stored PBKDF2-SHA256 at 600,000 iterations in `%AppData%`, written by a PowerShell script.
That bought two things: no plaintext anywhere on disk, and an iteration count that made online
guessing hopeless on its own. It cost a script, 190 lines, and a second place to look.

The trade taken instead is the ordinary Next one. What it gives up is real and worth stating
plainly:

- **The password is in plain text**, in a file inside the project folder — the folder that gets
  zipped, copied and backed up. `.gitignore` covers `dashboard/.env*` with a negation for the
  committed `.env.example`, checked with `git check-ignore` **before** any such file existed,
  because this repo is public and pushes on its own.
- **Nothing slows a guess down.** With the stored value being the password itself, a wrong answer
  costs one SHA-256. The login route's throttle — five free attempts, then a wait growing five
  seconds at a time to a five-minute ceiling — is now the whole defence rather than a supplement
  to an expensive hash, which is why a minimum length is enforced rather than suggested.
- **Changing it takes a restart.** Next reads `.env.local` once at boot. The hashed file was
  re-read whenever its mtime changed, so a new password took effect on the next request.

What it buys is that setting a password is editing one line in Notepad.

### Read per request, never at module scope

`process.env[ENV_VAR]`, inside the function, in a Node-runtime middleware. A bare
`process.env.NAME` at module scope is the shape build tooling substitutes a literal for, and the
edge runtime inlines env into the bundle outright — either would freeze whatever `.env.local`
held when the build ran, which is a bug that only shows up long after the password was changed.
Verified the way it has to be: built with no `.env.local` present, then created one and started
that same build. The gate came up.

The variable must never gain a `NEXT_PUBLIC_` prefix. That prefix is precisely the switch that
inlines a value into the browser bundle, which for this one would publish it to the people it is
keeping out.

### One gate, not one per page

The check is Next middleware rather than a call at the top of each page, because pages get added
and a gate you have to remember to fit is a gate that is eventually missing from the one route
that mattered. Four paths stay open: the sign-in page, the two auth routes, and `/icon.svg` —
artwork is not worth a login, and it reveals nothing.

An API request that is not signed in gets a **401 with JSON**, never the HTML of the login page.
The Now panel polls every five seconds from an already-open tab; handed a login page it would
parse it as its own feed and report the data as malformed rather than the session as expired. It
reloads on a 401 instead, and the gate takes the browser to the sign-in page.

### Sessions are their own expiry, signed

Nothing is stored server-side — there is one account, and a session table would only be a thing
to lose on restart. The cookie carries its expiry and an HMAC of it, under a key derived from the
password rather than stored anywhere. Changing the password changes the key, so every browser is
signed out without anything having to track who was signed in.

Thirty days, which is long on purpose. The threat is a second person reaching the port, not a
stolen browser profile, and a dashboard that asks for a password every morning is a dashboard
whose password ends up on a sticky note.

## Sizing

Phase 1 is the substantial one and stands alone. Phases 2 to 4 are largely mechanical, given how
much of the Data Usage Tracker ports across. The hedge, if one is wanted: land Phase 1 now so
data accumulates, then decide about the UI with a month of real samples in hand.

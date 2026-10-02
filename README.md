# Internet Speed Meter

> **Superseded.** This C# meter and its web dashboard have been replaced by
> [Internet Speed Meter (native)](https://github.com/naimulnashid/internet-speed-meter-native),
> one native app that reads and carries on the same settings, history and speed
> tests. This repository stays as it was, for reference.

A native Windows tray application that shows live download and upload speed as a two-line
readout in the notification area, with a details panel on click.

- **No runtime to install.** It builds with the C# compiler that ships inside Windows
  (.NET Framework 4, present on every Windows 10/11 machine) and produces one ~50 KB `.exe`
  with no dependencies.
- **Reads like the clock.** By default the speeds are drawn as **taskbar text** in the same
  font and size as the Windows clock, in the empty space just left of the notification area. A
  tray icon is a ~24 px square — four characters get six pixels each — so clock-sized text
  cannot fit there whatever you do; the clock is not an icon, it is text with room around it.
- **Tray icon fallback.** The numeric tray icon is still available (**Show speeds on → Tray
  icon numbers**) and takes over automatically when there is no horizontal taskbar to draw on.
  Its glyphs are fitted by width then stretched vertically to fill the row, and the 16 px icon
  uses a hand-made 3×7 pixel font, since no vector face survives an 8 px row.
- **Light on resources.** One timer per second reading interface counters through the IP
  Helper API; no packet capture, no admin rights, no network calls of its own. That last part is
  a property of the `.exe` and stays true: the optional dashboard has a speed test that does
  generate traffic, but only when you press its button, and the meter itself never does.

## Build

```bash
build.cmd
```

Output: `bin\InternetSpeedMeter.exe`. The build also generates `assets\app.ico` on first run
via `tools\make-icon.ps1`, so the binary icon is never checked in.

`assets\app.svg` is the vector twin of that icon on the same 32 unit grid — same corner radius,
gradient and arrow coordinates. It rasterises pixel-for-pixel identically to the generated
`.ico`, and is the file to use anywhere the artwork is needed outside the executable.

## Run

```bash
bin\InternetSpeedMeter.exe
```

**Windows 11 hides new tray icons by default.** After the first launch, click the `^` chevron
on the taskbar and drag the icon out, or go to Settings → Personalisation → Taskbar → Other
system tray icons and turn on *Internet Speed Meter*.

- **Left click** the icon — details panel: current speeds, a 60 second graph, session totals
  and the peak. Click again, press `Esc`, or click elsewhere to dismiss.
- **Right click** — menu.
- **Hover** — tooltip with the current rates and the adapter being metered.

## Menu

| Item | What it does |
| --- | --- |
| **Show speeds on** | *Taskbar text* draws the readout on the taskbar at clock size (default). *Tray icon numbers* additionally draws them into the tray icon. With taskbar text on and tray numbers off, the tray icon shows the app logo and just carries the menu. *Taskbar text position* puts the text at the left end (default) or beside the clock. |
| **Adapter** | *Automatic* follows the busiest adapter, so an idle VPN or virtual adapter never steals the reading. *All adapters* sums everything. Or pin one specific adapter. |
| **Units** | Bytes (`KB/s`, `MB/s`) or bits (`Kbps`, `Mbps`). |
| **Icon → Layout** | *Download and upload* stacks both rates. *Download only* / *Upload only* show one rate across the whole icon at roughly double the glyph height — the fix when the stacked pair is too small to read. |
| **Icon** | Show unit letters (turning them off frees a character, so the remaining digits get bigger), put upload on top, and force light or dark icon text instead of following the taskbar theme. |
| **Start with Windows** | Per-user `HKCU\...\Run` entry pointing at the current `.exe` location. Move the `.exe` after enabling this and you will need to toggle it off and on again. |
| **Reset session counters** | Zeroes the session totals and the graph. |

Settings live in `%AppData%\InternetSpeedMeter\settings.ini` and are written as you change them.
Path settings accept `%VARIABLES%`, so `logfolder=%LocalAppData%\InternetSpeedMeter\history` is
written once and resolves per machine.

## Recording

Speed history is written to disk as it is measured, so it outlives the process. Two files, kept
for different lengths of time because the two halves are worth different amounts:

| File | Record | Retention |
| --- | --- | --- |
| `%LocalAppData%\InternetSpeedMeter\raw\YYYY-MM-DD.bin` | 16 bytes, one per second | 14 days |
| `%LocalAppData%\InternetSpeedMeter\history\YYYY-MM.bin` | 40 bytes, one per minute, carrying the **max** of the seconds inside it | forever |

The minute file is the history. It costs 57 KB a day, and because it keeps the peak rather than
only the average, a peak query over a year never needs the seconds it came from. The raw file
costs 1.38 MB a day and only answers questions about the recent past — burst shape, sub-minute
stalls — so it is pruned, and it is configured separately so that moving the history into a
synced folder does not drag it along: syncing a file that is rewritten every second and deleted
a fortnight later is pure churn.

**These defaults are on the system drive, which a Windows reset destroys.** They are the default
because they are the folder every machine has; a hard-coded drive letter would work on exactly
one computer and disable recording silently on every other. If the history should outlive a
reset, point `logfolder` at another drive — and put `rawfolder` somewhere that is *not* synced,
since a file deleted after a fortnight uploads a copy of itself forever for nothing.

There is deliberately no automatic fallback in either direction. A configured folder that cannot
be written disables recording and says so, rather than quietly relocating the history somewhere
you are not looking.

One disk write per minute, buffered — an unexpected shutdown costs at most the current minute.
`adapters.tsv` beside the minute files maps the one-byte adapter index in each record to a real
adapter, so history is never silently spliced across a Wi-Fi/Ethernet switch.

| Setting | Default | |
| --- | --- | --- |
| `record` | `1` | Turns recording off entirely. |
| `logfolder` | `%LocalAppData%\InternetSpeedMeter\history` | Minute history. Unwritable — drive absent, say — stops recording for the session and notes it once in `error.log`. |
| `rawfolder` | `%LocalAppData%\InternetSpeedMeter\raw` | Raw samples. Unwritable, or set empty, keeps minute history running without them. |
| `rawretentiondays` | `14` | Days of raw samples to keep. Only files named as an exact date are ever deleted. |
| `activethresholdbps` | `51200` | Combined down+up rate at which a second counts as active. Recorded per minute rather than applied at read time, so changing it never rewrites what the past looked like. |

### Getting the data back out

```bash
bin\InternetSpeedMeter.exe --export-csv 2026-09-01 2026-09-30 september.csv
```

Dates are local and inclusive; `today` and `yesterday` are accepted in place of either. Without
a file path the CSV goes to the calling console, so it can be piped. Add `--raw` for one row per
second instead of one per minute — only for the days still inside the retention window.

The flags come back as plain `0`/`1` columns rather than a bitfield, because a spreadsheet has
no use for one: `connected`, `gap` and `adapter_changed`, plus `meter_started` on minute rows,
which marks the minute the meter began recording in. That minute holds fewer than 60 samples for
a reason that says nothing about how well it is sampling. Each row carries both local and UTC
time.

This exists so the recording is worth something on its own. A binary log that only one unwritten
program can read would make the history hostage to that program being built.

### Dashboard

An optional local web dashboard over the same history lives in `dashboard/`. The `.exe` does not
depend on it, does not know it exists, and records exactly the same either way.

```bash
start-dashboard.bat
```

It opens your browser once the server answers, and leaves a window you can watch the server in.
It builds only when there is no build at all. If the source is newer than the build, it warns
and serves the old one; run `npm run build` in `dashboard\` (or set `FORCE_BUILD=1`) to pick up
the change. If something is already serving port 7845, it just opens the browser.

Or, to have it there at every sign-in without a window:

```bash
powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1
```

That registers a logon task running as you, so Windows never stores a password and no admin
rights are needed. It rebuilds whenever a source file is newer than the last build, because a
launcher that builds once and then serves stale code forever is a quiet, convincing kind of
wrong in a dashboard whose job is reporting current numbers. Everything it does goes to
`logs\dashboard.log`, which is the only way to see why it did not come up. If the server
crashes, the service restarts it after a minute, up to 3 times in 10 minutes; a server that keeps
dying stays down, and the log says why. To stop it,
double-click `stop-dashboard.bat`, which stops this project's server however it was started and
leaves any other program on the port alone; to unregister the task, pass `-Remove` to the
install script.

None of this touches recording. The meter writes to disk whether the dashboard is installed,
running, or removed, and removing the task loses no history. It is also unrelated to the tray
menu's **Start with Windows**, which is the meter's own `HKCU\...\Run` entry.

Pages read a small SQLite cache of the minute log, kept in
`%LocalAppData%\InternetSpeedMeter\cache`. It is built entirely from the `.bin` files, so
deleting it loses nothing and it rebuilds on the next request. Only minutes are cached, because
the raw log is capped by its retention window while minutes are kept forever. Node 22.5+ is required for the built-in `node:sqlite`.

For development, `npm --prefix dashboard run dev` serves the same pages with hot reload.

Then open <http://localhost:7845>. It reads the folder paths, the active threshold and the
bytes/bits choice out of `settings.ini`, so the web page and the taskbar readout can never
disagree. The site password below is the one thing it keeps of its own.

| Page | What it answers |
| --- | --- |
| **Speed** | Fastest second today and over the chosen range, best sustained 10 s / 1 min / 5 min, and the fastest second of each day. The sustained averages need the raw log, so they cover only the retained days. |
| **Test** | An active speed test against speed.cloudflare.com, in three sizes so you choose what a run costs. Reports download and upload with their peak second and a chart of the rate through each leg, latency and jitter **idle and under load in each direction** — the difference being bufferbloat, which is what predicts whether a call survives a download — plus **packet loss**, and who is at each end: your ISP and ASN, and which Cloudflare datacentre answered. The only part of the project that makes network calls of its own. Packet loss comes from ICMP echoes the dashboard server sends to speed.cloudflare.com, ten a second for the length of the run, through Windows PowerShell's `Ping` class: the browser only speaks TCP, which repairs a lost packet before the page could count it. Results are kept in `speedtests.jsonl` beside the history, each shown next to what the meter independently recorded at the adapter. |

See [docs/dashboard-plan.md](docs/dashboard-plan.md) for what it shows and why.

#### Site password

The dashboard listens on this machine only (`127.0.0.1`), so nothing else on your network can
reach it, and it is open by default, because a login on localhost only protects a single-user
machine from itself. Set a password when the port stops being yours alone — a shared PC, a
second account, a tunnel or port forward opened to reach the dashboard from a phone. Copy
`dashboard/.env.example` to `dashboard/.env.local` and fill in the one line:

```bash
DASHBOARD_PASSWORD=something-long
```

Every page and every API route then needs it, and a **Sign out** appears in the top bar. Leave
the value empty, or delete the file, and the dashboard is open again. Use at least 8 characters:
nothing here slows a guess down but the login form's attempt limit, so the length is doing the
work.

Next reads `.env.local` once, when the server boots, so a change takes a restart — and the
dashboard runs hidden from a logon task with no window to Ctrl+C:

```bash
powershell -ExecutionPolicy Bypass -File scripts\dashboard-stop.ps1
```

```bash
wscript scripts\dashboard-hidden.vbs
```

Changing the password signs every browser out, because the session cookie is signed with a key
derived from it. `.gitignore` covers `dashboard/.env*` — the password is in that file in plain
text, and this repo is public. Never rename the variable to `NEXT_PUBLIC_DASHBOARD_PASSWORD`:
that prefix is what inlines a value into the browser bundle, which would hand this one to
exactly the people it is keeping out.

A password that is set but under 8 characters shuts the dashboard rather than being ignored, and
the sign-in page says why. Silently falling open on a typo is the one failure nobody would
notice. The history is untouched throughout — the meter neither knows nor cares that any of
this exists.

#### Using it from your phone

The dashboard can listen on your network too, so a phone on the same Wi-Fi can open it. It is
off by default and needs the password above: without one of at least 8 characters, the
launchers serve on this machine only and say why. Over the network the password is the only
thing in front of the dashboard, and it travels over plain HTTP.

1. In `dashboard/.env.local`, set `DASHBOARD_PASSWORD`, and set `SESSION_SECRET` to a long
   random value (`dashboard/.env.example` shows a command that makes one). Without the secret, a
   session cookie captured on the Wi-Fi could be used to test password guesses offline.
2. Register the logon task with network access, and start it now:

   ```bash
   powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Lan
   ```

   ```bash
   wscript scripts\dashboard-hidden.vbs -Lan
   ```

   For a visible window instead, set `DASHBOARD_LAN=1` and run `start-dashboard.bat`.
   Re-running the install script without `-Lan` switches back to this machine only.
3. On the phone, open `http://<this PC's address>:7845` (`ipconfig` shows the address).
4. Keep it off café and hotel Wi-Fi. Windows' first prompt for Node.js usually allows it on
   **Public** networks as well, so mark your home network **Private**, then run this once from an
   Administrator shell. It blocks port 7845 on Public networks only:

   ```bash
   npm --prefix dashboard run firewall
   ```

**A speed test run from the phone measures the phone.** The test runs in the browser, so on the
phone it measures the phone's Wi-Fi, not this PC's line. Those runs are saved with the rest and
marked **phone** in Previous runs. They have no packet loss and no **Meter saw** figure, because
the pinger and the meter both live on the PC and would be describing a different device.

## Taskbar text

The widget is a layered, topmost, non-activating window painted with per-pixel alpha, so the
taskbar's own background — acrylic, accent colour, whatever — shows through untouched.

The clock moves smoothly when an auto-hiding taskbar slides because it *is* part of the
taskbar window. A separate window has to follow, and Windows does animate the taskbar's
rectangle — roughly 185 ms, stepping about every 25 ms — so the widget adopts that rectangle
rather than approximating it with an easing curve of its own. Nothing is gated on a visibility
threshold, because hiding at a cutoff is precisely what makes a widget pop instead of slide; the
window is positioned regardless and the screen edge clips it.

Adopting the rectangle is only as good as the interval it is sampled on, and a WinForms timer
cannot sample it: `WM_TIMER` is rounded up to whole system ticks and delivered only when the
message queue is empty, so a timer asking for 16 ms measured a median period of 30 ms with a
tail past 90 ms. The widget skipped two thirds of the taskbar's steps and was late for the rest.
A watcher thread paced by `DwmFlush` — which blocks until the compositor has finished its next
frame — samples at exactly one frame instead: 4 ms on a 240 Hz screen, 16 ms on a 60 Hz one,
asleep in between rather than polling a clock. It only watches. The move itself stays on the UI
thread, reached by a posted message, which unlike `WM_TIMER` is picked up the moment the thread
returns to its loop. Once the bar has been still for 400 ms the watcher drops to a plain 15 ms
sleep, so a quiet taskbar is not being followed at frame rate for nothing.

The other half is what the move is allowed to do. Because the taskbar owns this window (below),
re-ordering it has to synchronise with Explorer's thread — which during a taskbar animation is
the busiest thread on the machine: `SetWindowPos` measured **100 ms a call** mid-slide, once per
frame, which is where the stutter actually came from. Ownership is what holds the z-order now,
so a move asks for `SWP_NOZORDER` and never pays that.

Measured against the taskbar over a slide, mean deviation went from 11.6 px to **0.5 px** and
the worst from 30 px to 6 px, while idle cost went *down*, from 0.90% of one core to 0.62%,
because the UI thread is no longer woken 62 times a second to discover nothing has moved.

Opening Start is what makes ownership necessary: the taskbar is moved into a higher z-order
*band* — measurably band 1 to band 6 — so that it draws over the menu's own backdrop. A band
outranks `WS_EX_TOPMOST` outright, so no z-order this process can ask for puts the readout back
on top, and the promotion that would match it (`SetWindowBand`) is refused to anything without
UIAccess. What does work is ownership: an owned window is always ordered above its owner,
whatever band the owner is in. The taskbar therefore owns this window, and it rides the
promotion rather than trying to out-rank it — for Start, for search, for the notification centre
and for anything else that lifts the bar. It also replaces the z-order heartbeat this used to
need: clicking or focusing the taskbar no longer buries the readout, because the window manager
keeps an owned window above its owner without being asked. The cost is that destroying a window
destroys everything it owns, so an Explorer restart takes the widget with the taskbar; it is
rebuilt on the next repaint.

Two caveats worth knowing:

- **With auto-hide on, the readout is visible only when the taskbar is** — exactly like the
  clock itself.
- The text sits at the **left end** of the taskbar by default, which Windows 11 leaves empty
  when the buttons are centred. **Show speeds on → Taskbar text position** moves it beside the
  clock instead. Either way it never pushes the taskbar's own content aside, so on a very busy
  taskbar the buttons can grow underneath it — pick whichever end stays clear for you.

Left click it for the details panel, right click for the menu — same as the tray icon. The
whole readout is clickable, not just the glyphs: a layered window is hit-tested through its
alpha channel, so the background is painted at alpha 1 — invisible against anything, but solid
enough to catch a click that lands in the gap between two digits.

The details panel opens clear of the taskbar rather than on top of it. The working area cannot
be trusted for this, because an auto-hiding taskbar reserves none of it; the visible taskbar
rectangle is subtracted explicitly, on whichever edge it lives.

## Reading the icon

Top line is download, bottom is upload (swappable). Values are compact: `9.4K` is 9.4 KB/s,
`118M` is 118 MB/s, `512` with unit letters off is 512 B/s. Green is down, amber is up.

## Troubleshooting

If the icon sits at zero, check what the meter actually sees:

```bash
bin\InternetSpeedMeter.exe --sample 15
```

It prints the detected adapters and one line per second with the selected adapter and its
rates, without touching the tray. Add a file path as a second argument to write the report to
a file instead of the console.

Unhandled errors are appended to `%AppData%\InternetSpeedMeter\error.log`.

**"An Application Control policy has blocked this file"** — Smart App Control (Windows
Security → App & browser control) judges unsigned executables by reputation, and a locally
built `.exe` has none, so an individual build can be blocked while others run. Rebuilding
produces a new binary and usually clears it. The durable fixes are a real code-signing
certificate or turning Smart App Control off — note that turning it off is one-way, as
re-enabling it requires resetting Windows.

## Development helpers

```bash
bin\InternetSpeedMeter.exe --preview-icon icon-preview.png
bin\InternetSpeedMeter.exe --preview-flyout flyout-preview.png
```

`--preview-icon` renders the tray readout at 16/20/24/32 px against light and dark taskbar
colours, magnified 4×, so icon legibility can be judged without squinting at the real tray.
`--preview-flyout` renders the details panel in both themes with synthetic traffic.

## Layout

| File | Role |
| --- | --- |
| `src/Program.cs` | Entry point, single-instance guard, CLI modes |
| `src/TrayApp.cs` | Tray icon, one-second sampling loop, context menu |
| `src/NetMonitor.cs` | Per-adapter counter polling and adapter selection |
| `src/IconRenderer.cs` | Two-line icon drawing, font fitting, HICON lifetime |
| `src/PixelFont.cs` | 3×5 bitmap font for the 16 px icon |
| `src/Recorder.cs` | Speed history: raw and minute-rollup binary logs, retention |
| `src/LogExport.cs` | Reads those logs back out as CSV |
| `src/TaskbarWidget.cs` | Clock-style taskbar text: layered window, taskbar tracking |
| `src/FlyoutForm.cs` | Details panel with the live graph |
| `src/Theme.cs` | Taskbar-theme aware palette |
| `src/Settings.cs` | `settings.ini` load/save |
| `src/AutoStart.cs` | Start-with-Windows registry entry |
| `src/Fmt.cs` | Speed and volume formatting |
| `src/Native.cs` | P/Invoke: DPI, tray icon metrics, `DestroyIcon`, rounded corners |

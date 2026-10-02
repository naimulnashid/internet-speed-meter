# Security policy

This project is retired in favour of
[Internet Speed Meter (native)](https://github.com/naimulnashid/internet-speed-meter-native).
A report about something both share, such as the speed test, is best made
there.

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's
**Security → Report a vulnerability** on this repository, not in a public
issue. Include what you found, how to reproduce it, and what an attacker could
do with it. You should hear back within a week.

Only the latest commit on `main` is supported; there are no maintained release
branches.

## What is in scope

This is a local tool, so the interesting boundaries are local ones:

- **The meter** (`bin\InternetSpeedMeter.exe`) makes no network calls and
  needs no admin rights. Anything that makes it send traffic, or write outside
  its settings and history folders or a file the user asked for, is in scope.
- **The dashboard** (Next.js, port 7845). It serves the speed history and the
  speed test, behind a password when one is set. Anything that reads the
  history or starts a test without the password, bypasses the login throttle,
  or lets another page act with a signed-in session is in scope. By default it
  listens on `127.0.0.1`; with network access turned on it is reachable on the
  local network over plain HTTP, which is a documented trade-off, not a
  vulnerability in itself. `npm --prefix dashboard run firewall`
  (Administrator) blocks the port on **Public** networks; see
  `scripts/firewall-private-only.ps1`.
- **The speed test**, the only traffic in the repo: browser transfers to
  speed.cloudflare.com, and ICMP echoes to it from the dashboard server
  (`dashboard/src/lib/pinger.ts`), only while a test runs. Anything that makes
  either go elsewhere, or run without a test, is in scope.
- **The logon task** (`scripts/install-autostart.ps1`), which runs as the user,
  never elevated, and stores no password.

Out of scope: anything requiring an already-elevated attacker, physical access
to an unlocked machine, or a deliberately weakened configuration (a password
under 8 characters closes the dashboard; a password shared with an attacker is
not a bug).

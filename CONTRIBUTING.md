# Contributing

Thanks for looking. This project is retired: it has been replaced by
[Internet Speed Meter (native)](https://github.com/naimulnashid/internet-speed-meter-native),
and this repository stays as it was, for reference. New features belong there.

- **Bugs and questions:** open an issue, ideally on the native app. Please
  leave out your own speeds, addresses and adapter names; describe the shape
  of the problem instead.
- **Small fixes:** a pull request is welcome.
- **Security issues:** not in a public issue; see [SECURITY.md](SECURITY.md).

## Before sending a change

```bash
build.cmd
npm --prefix dashboard run typecheck
npm --prefix dashboard run build
```

## Conventions

- **Conventional commit messages** (`feat:`, `fix:`, `docs:`), one concern per
  commit.
- **The meter is C# 5.** `build.cmd` uses the compiler that ships inside
  Windows, so no string interpolation, no `?.`, no `=>` members, no NuGet.
- **The log format does not change.** `src/Recorder.cs` writes it and
  `dashboard/src/lib/logs.ts` reads it, and the native app reads it too; the
  two sides define the same constants and must stay in lockstep.
- **The meter never knows about the dashboard,** and makes no network calls.
  The speed test is the only traffic.
- **Keep every `.ps1` pure ASCII.** Windows PowerShell 5.1 misreads a UTF-8
  dash in a BOM-less script, and silently changes its logic.
- **Never commit recorded data** or `dashboard/.env.local`; `.gitignore`
  covers both.

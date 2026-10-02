import 'server-only';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Where the meter keeps its history, read from the meter's own settings file.
 *
 * Deliberately not a config file of the dashboard's own. The folders are already
 * stated in `%AppData%\InternetSpeedMeter\settings.ini`, and a second copy here
 * would be a thing to keep in sync -- and would fail silently, showing an empty
 * dashboard while the meter recorded happily somewhere else.
 */
export interface MeterSettings {
  /** Minute rollups and adapters.tsv. The history. */
  logFolder: string;
  /** One-second samples. Empty when the user asked for minutes only. */
  rawFolder: string;
  /** Combined down+up bytes/s at which the meter counted a second as active. */
  activeThresholdBps: number;
  /** Whether the meter reports in bytes or bits, so both readouts agree. */
  units: 'bytes' | 'bits';
  /** False when the meter has recording switched off entirely. */
  recording: boolean;
  /** Where the above came from, or null when no settings file exists yet. */
  source: string | null;
}

/**
 * Mirrors the defaults in src/Settings.cs, and must not drift from them: when
 * settings.ini omits a key, this is the only way to know where the meter
 * actually wrote. Reading the wrong folder shows an empty dashboard while the
 * meter records perfectly well somewhere else.
 */
function defaults(): Omit<MeterSettings, 'source'> {
  const local = process.env['LOCALAPPDATA'] ?? '';
  return {
    logFolder: local ? join(local, 'InternetSpeedMeter', 'history') : '',
    rawFolder: local ? join(local, 'InternetSpeedMeter', 'raw') : '',
    activeThresholdBps: 50 * 1024,
    units: 'bytes',
    recording: true,
  };
}

/** Resolve %VARIABLES% the same way the meter does when it reads its own file. */
function expand(value: string): string {
  return value.replace(/%([^%]+)%/g, (whole, name: string) => process.env[name] ?? whole);
}

function settingsPath(): string {
  const appData = process.env['APPDATA'] ?? '';
  return join(appData, 'InternetSpeedMeter', 'settings.ini');
}

function parseBool(value: string, fallback: boolean): boolean {
  const v = value.toLowerCase();
  if (v === '1' || v === 'true' || v === 'yes') return true;
  if (v === '0' || v === 'false' || v === 'no') return false;
  return fallback;
}

export function meterSettings(): MeterSettings {
  const path = settingsPath();
  let text: string;
  try {
    text = readFileSync(path, 'utf8');
  } catch {
    // No settings file means the meter has never been run, or never had a
    // setting changed. Its own defaults are then what is in force.
    return { ...defaults(), source: null };
  }

  const settings: MeterSettings = { ...defaults(), source: path };

  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#') || trimmed.startsWith(';')) continue;

    const eq = trimmed.indexOf('=');
    if (eq <= 0) continue;

    const key = trimmed.slice(0, eq).trim().toLowerCase();
    const value = trimmed.slice(eq + 1).trim();

    switch (key) {
      case 'logfolder':
        if (value) settings.logFolder = expand(value);
        break;
      case 'rawfolder':
        // Empty is meaningful here -- it is how minutes-only is requested -- so
        // it overrides the default rather than being skipped as blank.
        settings.rawFolder = expand(value);
        break;
      case 'activethresholdbps': {
        const n = Number(value);
        if (Number.isFinite(n) && n >= 0) settings.activeThresholdBps = n;
        break;
      }
      case 'units':
        settings.units = value.toLowerCase() === 'bits' ? 'bits' : 'bytes';
        break;
      case 'record':
        settings.recording = parseBool(value, true);
        break;
    }
  }

  return settings;
}

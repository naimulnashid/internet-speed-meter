/**
 * Formatting for a dashboard about *rates*.
 *
 * Forked from the Data Usage Tracker's version rather than shared, because the
 * unit is the whole difference between the two projects: that one reports how
 * much, this one reports how fast, and a formatter that says "GB" where the
 * reader expects "Mbps" is worse than no formatter.
 *
 * Bits use powers of 1000 and bytes powers of 1024, matching the meter itself
 * (src/Fmt.cs) -- so the taskbar readout and the web page agree to the digit.
 *
 * Everything is rendered with `font-variant-numeric: tabular-nums`, set
 * globally, so digits keep a fixed width and count-ups do not shiver.
 */

export type Units = 'bytes' | 'bits';

const BYTE_RATE = ['B/s', 'KB/s', 'MB/s', 'GB/s', 'TB/s'];
const BIT_RATE = ['bps', 'Kbps', 'Mbps', 'Gbps', 'Tbps'];
const VOLUME = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

function scale(value: number, step: number, units: string[]): { value: number; unit: string } {
  let scaled = value;
  let i = 0;
  while (Math.abs(scaled) >= step && i < units.length - 1) {
    scaled /= step;
    i++;
  }
  return { value: scaled, unit: units[i] ?? units[units.length - 1] ?? '' };
}

function decimals(value: number): number {
  const abs = Math.abs(value);
  return abs >= 100 ? 0 : abs >= 10 ? 1 : 2;
}

/** A rate, given in bytes per second whatever the display unit. */
export function formatRate(bytesPerSecond: number, units: Units = 'bytes'): string {
  const { value, unit } = splitRate(bytesPerSecond, units);
  return `${value} ${unit}`;
}

/** Split so the unit can be styled down separately from the number. */
export function splitRate(
  bytesPerSecond: number,
  units: Units = 'bytes',
): { value: string; unit: string } {
  const bits = units === 'bits';
  const raw = bits ? bytesPerSecond * 8 : bytesPerSecond;
  const { value, unit } = scale(raw, bits ? 1000 : 1024, bits ? BIT_RATE : BYTE_RATE);
  return { value: value.toFixed(decimals(value)), unit };
}

/** A volume of traffic. Always binary units, as Windows reports them. */
export function formatBytes(bytes: number): string {
  const { value, unit } = scale(bytes, 1024, VOLUME);
  return `${value.toFixed(decimals(value))} ${unit}`;
}

export function formatCount(n: number): string {
  return n.toLocaleString('en-US');
}

export function formatStamp(time: number): string {
  return new Date(time).toLocaleString('en-GB', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  });
}

export function formatDayShort(time: number): string {
  return new Date(time).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
}

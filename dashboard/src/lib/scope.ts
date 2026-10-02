/**
 * Shared scope constants.
 *
 * Apart from the query modules, which are `server-only`, because the range
 * dropdown is a client component and both sides need these numbers.
 */

/**
 * The "All" range, as a day count rather than a sentinel, so every caller keeps
 * subtracting days and there is no special case to forget. A century back
 * covers all history without pretending to be unbounded.
 */
export const ALL_DAYS = 36500;

/**
 * Offered in the daily peak chart's dropdown, shortest first. No "Today": the
 * page already shows today's fastest seconds on their own, and a one-day chart
 * is a single pair of bars.
 */
export const RANGES = [7, 30, 90, ALL_DAYS] as const;

/**
 * Opening view.
 *
 * A month, not all history -- unlike the Data Usage Tracker, whose subject is a
 * long-run total. A week is too short to show a change in what the connection
 * can do: a step down in the daily peak reads as a few quiet days until there
 * are weeks on either side of it.
 */
export const DEFAULT_DAYS = 30;

export function rangeLabel(days: number): string {
  return days === ALL_DAYS ? 'All days' : `${days} days`;
}

/** Parse `?days=` into a range actually offered, falling back to the default. */
export function parseDays(raw: string | undefined): number {
  const n = Number(raw);
  return (RANGES as readonly number[]).includes(n) ? n : DEFAULT_DAYS;
}

/** The inclusive local-date window a day count means, ending today. */
export function windowFor(days: number): { from: Date; to: Date } {
  const to = new Date();
  const from = new Date(to.getFullYear(), to.getMonth(), to.getDate() - (days - 1));
  return { from, to };
}

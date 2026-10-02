/**
 * The pages, in one place, so the top bar and any link elsewhere cannot
 * disagree about which routes exist.
 */

export interface Page {
  href: string;
  label: string;
}

export const PAGES: Page[] = [
  { href: '/speed', label: 'Speed' },
  // Last, and deliberately so. The other page reads a recording that was
  // going to happen anyway; this one spends data to make a measurement. Putting
  // the button that costs something at the end of the row suits how often it
  // should be reached for.
  { href: '/speedtest', label: 'Test' },
];

/**
 * Whether a nav entry should read as current for the given path.
 *
 * Matches on whole path segments, not on a string prefix. A bare startsWith lit
 * up two tabs at once the moment /speedtest existed alongside /speed, because
 * one href is a prefix of the other -- and two current tabs is worse than none,
 * since the reader cannot tell which is lying.
 */
export function isCurrent(page: Page, pathname: string): boolean {
  return pathname === page.href || pathname.startsWith(page.href + '/');
}

import type { ReactNode } from 'react';

/**
 * The small badge in the corner of a speed test result card.
 *
 * Inline SVG in a 24-unit stroke style rather than an icon package: five
 * glyphs do not justify a dependency. Colour comes from the tone, matching the
 * number beneath it, so the badge reads as part of the card and never as a
 * separate status light.
 */

export type StatIconName = 'download' | 'upload' | 'latency' | 'jitter' | 'loss';

const PATHS: Record<StatIconName, ReactNode> = {
  download: (
    <>
      <path d="M12 4v11" />
      <path d="m7 10 5 5 5-5" />
      <path d="M5 20h14" />
    </>
  ),
  upload: (
    <>
      <path d="M12 15V4" />
      <path d="m7 9 5-5 5 5" />
      <path d="M5 20h14" />
    </>
  ),
  latency: (
    <>
      <circle cx="12" cy="13" r="8" />
      <path d="M12 9v4l2.5 2.5" />
      <path d="M10 2h4" />
    </>
  ),
  jitter: <path d="M2 12h3l2.5-6 4 12 3.5-9 2 3H22" />,
  // Two packets through, the third crossed out.
  loss: (
    <>
      <rect x="2" y="9" width="5" height="6" rx="1" />
      <rect x="9.5" y="9" width="5" height="6" rx="1" />
      <path d="m17.5 9.5 4.5 5" />
      <path d="m22 9.5-4.5 5" />
    </>
  ),
};

export function StatIcon({ name, tone }: { name: StatIconName; tone?: 'down' | 'up' }) {
  return (
    <span className={`stat-icon${tone ? ` stat-icon--${tone}` : ''}`} aria-hidden="true">
      <svg
        viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor"
        strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
      >
        {PATHS[name]}
      </svg>
    </span>
  );
}

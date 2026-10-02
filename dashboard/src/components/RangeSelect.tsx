'use client';

import { useRouter, usePathname, useSearchParams } from 'next/navigation';
import { useCallback, useTransition } from 'react';
import { RANGES, rangeLabel } from '@/lib/scope';

/**
 * Window length for the daily peak chart, kept in the URL.
 *
 * In the query string rather than component state so a view is linkable and
 * survives a refresh. It sits on the chart rather than in the top bar because
 * the chart is the only thing on the page it changes.
 */
export function RangeSelect({ days }: { days: number }) {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const [pending, startTransition] = useTransition();

  const setDays = useCallback(
    (value: number) => {
      const next = new URLSearchParams(params.toString());
      next.set('days', String(value));
      startTransition(() => router.push(`${pathname}?${next.toString()}`, { scroll: false }));
    },
    [params, pathname, router],
  );

  return (
    <select
      className="select"
      aria-label="Days shown"
      value={days}
      onChange={(e) => setDays(Number(e.target.value))}
      style={{ opacity: pending ? 0.55 : 1 }}
    >
      {RANGES.map((d) => (
        <option key={d} value={d}>
          {rangeLabel(d)}
        </option>
      ))}
    </select>
  );
}

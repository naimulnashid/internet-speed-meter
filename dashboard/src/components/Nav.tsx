'use client';

import Link from 'next/link';
import { usePathname, useSearchParams } from 'next/navigation';
import { PAGES, isCurrent } from '@/lib/nav';

/**
 * Page tabs.
 *
 * Each link carries the current query string forward, so switching pages keeps
 * the window you were looking at instead of silently resetting to the default
 * and showing you different numbers under the same heading.
 */
export function Nav() {
  const pathname = usePathname();
  const params = useSearchParams();
  const query = params.toString();

  return (
    <nav className="tabs">
      {PAGES.map((page) => (
        <Link
          key={page.href}
          href={query ? `${page.href}?${query}` : page.href}
          className="tab"
          data-active={isCurrent(page, pathname)}
        >
          {page.label}
        </Link>
      ))}
    </nav>
  );
}

import type { ReactNode } from 'react';

interface CardProps {
  children: ReactNode;
  hover?: boolean;
  /** Staggered entrance, in ms. Keep the total under ~400ms across a grid. */
  delay?: number;
  className?: string;
}

export function Card({ children, hover = true, delay = 0, className = '' }: CardProps) {
  return (
    <div
      className={`card ${hover ? 'card--hover' : ''} animate-in ${className}`}
      style={{ animationDelay: `${delay}ms` }}
    >
      {children}
    </div>
  );
}

export function CardTitle({
  children, sub, aside,
}: {
  children: ReactNode;
  sub?: string;
  /** Right-aligned slot, for a figure that belongs to the panel as a whole. */
  aside?: ReactNode;
}) {
  return (
    <div className="card-title">
      <div style={{ minWidth: 0 }}>
        <h2>{children}</h2>
        {sub && <p style={{ margin: '0.3rem 0 0', color: 'var(--text-dim)', fontSize: 'var(--fs-small)' }}>{sub}</p>}
      </div>
      {aside}
    </div>
  );
}

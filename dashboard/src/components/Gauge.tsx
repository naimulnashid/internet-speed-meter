'use client';

import { formatRate, splitRate, type Units } from '@/lib/format';

/**
 * A speedometer for the live rate during a test.
 *
 * The scale is LOGARITHMIC, which is the whole design decision here. A domestic
 * connection can be anywhere from a few hundred Kbps to a gigabit, and on a
 * linear dial sized for the fast case every ordinary speed sits pinned against
 * the stop, indistinguishable from every other ordinary speed. Four decades of
 * arc give 10 Mbps and 100 Mbps visibly different needle positions, which is
 * the comparison anyone actually wants to make.
 *
 * The cost is that the dial is not linear, so half way round is not half the
 * speed. Labelled decade ticks are what make that legible rather than
 * misleading, so they are not decoration and should not be dropped.
 */

/** 10 KB/s to 100 MB/s: about 80 Kbps to 800 Mbps. Four decades. */
const MIN_BPS = 1e4;
const MAX_BPS = 1e8;

/** Opens at the bottom: starts bottom-left, sweeps over the top, ends bottom-right. */
const START_ANGLE = 135;
const SWEEP = 270;

const CX = 120;
const CY = 120;
const R = 92;

function fraction(bytesPerSecond: number): number {
  if (!(bytesPerSecond > 0)) return 0;
  const f = (Math.log10(bytesPerSecond) - Math.log10(MIN_BPS))
    / (Math.log10(MAX_BPS) - Math.log10(MIN_BPS));
  return Math.max(0, Math.min(1, f));
}

function point(angleDeg: number, radius: number): [number, number] {
  const rad = (angleDeg * Math.PI) / 180;
  return [CX + radius * Math.cos(rad), CY + radius * Math.sin(rad)];
}

/** Arc path from the gauge start to `f` of the way round. */
function arc(f: number, radius: number): string {
  const end = START_ANGLE + SWEEP * Math.max(f, 0.0001);
  const [x0, y0] = point(START_ANGLE, radius);
  const [x1, y1] = point(end, radius);
  const large = SWEEP * f > 180 ? 1 : 0;
  return `M ${x0} ${y0} A ${radius} ${radius} 0 ${large} 1 ${x1} ${y1}`;
}

const TICKS = [1e4, 1e5, 1e6, 1e7, 1e8];

/** 2x and 5x within each decade, the usual subdivision of a log scale. */
const MINOR_TICKS = [1e4, 1e5, 1e6, 1e7].flatMap((decade) => [decade * 2, decade * 5]);

export function Gauge({
  bytesPerSecond, units, tone,
}: {
  bytesPerSecond: number;
  units: Units;
  tone: 'down' | 'up';
}) {
  const f = fraction(bytesPerSecond);
  const colour = tone === 'up' ? 'var(--up)' : 'var(--down)';
  // At rest a dial reads zero, not "0.00 bps" -- the formatter's two decimals
  // are for distinguishing 1.25 from 1.26, and there is nothing to distinguish.
  const shown = bytesPerSecond > 0
    ? splitRate(bytesPerSecond, units)
    : { value: '0', unit: units === 'bits' ? 'bps' : 'B/s' };
  const needle = START_ANGLE + SWEEP * f;

  const dash = `${Math.max(f * 100, 0.6)} 100`;

  return (
    <div className="gauge">
      {/* The box is sized around the tick LABELS, not the dial. They sit at radius
          118 and are centred on their point, so they overhang the dial on every
          side: the pair at the sides pushed past both vertical edges, and the one
          at the top sits at y=2 with half its height above that. Hence the
          negative origin on both axes. */}
      <svg viewBox="-12 -10 264 222" role="img" aria-label={`${formatRate(bytesPerSecond, units)}`}>
        {/*
          The track was --bg-inset on a --bg-panel card: #08080a on #0a0a0b, two
          values apart and therefore not there. A dial needs a visible body, or
          the needle and the coloured arc are just marks floating in a box. It is
          the same groove-and-fill idea as the progress bar, at a size you can
          actually see.
        */}
        <path d={arc(1, R)} fill="none" stroke="var(--border-strong)" strokeWidth="14" strokeLinecap="round" />
        <path d={arc(1, R)} fill="none" stroke="var(--text-faint)" strokeWidth="1" opacity="0.35" />

        {/*
          Minor ticks at 2x and 5x inside each decade. Without them a log dial
          reads as five arbitrary labels on a curve; with them the eye can see
          the scale compressing towards each decade, which is what tells you it
          is a log dial rather than a mis-drawn linear one.
        */}
        {MINOR_TICKS.map((t) => {
          const a = START_ANGLE + SWEEP * fraction(t);
          const [ix, iy] = point(a, R - 6);
          const [ox, oy] = point(a, R + 6);
          return (
            <line
              key={t} x1={ix} y1={iy} x2={ox} y2={oy}
              stroke="var(--text-faint)" strokeWidth="1" opacity="0.55"
            />
          );
        })}

        {/* Value. Transitioned so the needle sweeps rather than teleports between
            the four-times-a-second samples it is fed. */}
        {/* Omitted entirely at rest rather than drawn with a zero-length dash.
            A dash of length 0 with a round cap does not disappear -- SVG renders
            it as a dot at every dash position, which put two green blobs on the
            ends of an idle dial. */}
        {bytesPerSecond > 0 && (
          <path
            d={arc(1, R)}
            fill="none"
            stroke={colour}
            strokeWidth="14"
            strokeLinecap="round"
            pathLength={100}
            strokeDasharray={dash}
            style={{ transition: 'stroke-dasharray 220ms var(--ease)' }}
          />
        )}

        {TICKS.map((t) => {
          const tf = fraction(t);
          const a = START_ANGLE + SWEEP * tf;
          const [ix, iy] = point(a, R - 12);
          const [ox, oy] = point(a, R + 12);
          const [lx, ly] = point(a, R + 26);
          return (
            <g key={t}>
              <line x1={ix} y1={iy} x2={ox} y2={oy} stroke="var(--text-faint)" strokeWidth="1.5" />
              <text
                x={lx} y={ly}
                fill="var(--text-faint)" fontSize="9"
                textAnchor="middle" dominantBaseline="middle"
              >
                {formatRate(t, units)}
              </text>
            </g>
          );
        })}

        <line
          x1={CX} y1={CY}
          x2={point(needle, R - 20)[0]} y2={point(needle, R - 20)[1]}
          stroke={colour} strokeWidth="3" strokeLinecap="round"
          style={{ transition: 'all 220ms var(--ease)' }}
        />
        <circle cx={CX} cy={CY} r="7" fill="var(--bg-panel)" stroke={colour} strokeWidth="2.5" />

        <text
          x={CX} y={CY + 40}
          fill="var(--text)" fontSize="30" fontWeight="650"
          textAnchor="middle" dominantBaseline="middle"
        >
          {shown.value}
        </text>
        <text
          x={CX} y={CY + 62}
          fill="var(--text-dim)" fontSize="12" fontWeight="600"
          textAnchor="middle" dominantBaseline="middle"
        >
          {shown.unit}
        </text>
      </svg>
    </div>
  );
}

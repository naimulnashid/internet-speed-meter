'use client';

import {
  Area, AreaChart, CartesianGrid, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { formatRate, type Units } from '@/lib/format';

export interface SpeedPoint {
  /** Seconds since the leg started. */
  t: number;
  bps: number;
}

const HEIGHT = 130;

/**
 * The rate through one leg of a test, as it ran.
 *
 * The headline is one number, and one number hides the shape: a slow ramp, a
 * plateau that sagged half way, a link that stalled and recovered. This is the
 * shape. It fills live while the leg runs and stays once the result is in, with
 * the headline average drawn across it so the two can be read against each
 * other.
 *
 * Always occupies its full height, empty or not, so the card does not grow when
 * the first sample lands.
 */
export function SpeedChart({
  points, units, tone, average,
}: {
  points: SpeedPoint[];
  units: Units;
  tone: 'down' | 'up';
  average?: number;
}) {
  const colour = tone === 'up' ? 'var(--up)' : 'var(--down)';

  if (points.length < 2) {
    return <div className="speed-chart speed-chart--empty" style={{ height: HEIGHT }} />;
  }

  return (
    <div className="speed-chart" style={{ height: HEIGHT }}>
      <ResponsiveContainer>
        <AreaChart data={points} margin={{ top: 8, right: 4, bottom: 0, left: 0 }}>
          <defs>
            <linearGradient id={`speed-fill-${tone}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={colour} stopOpacity={0.28} />
              <stop offset="100%" stopColor={colour} stopOpacity={0.02} />
            </linearGradient>
          </defs>
          <CartesianGrid stroke="var(--border)" vertical={false} />
          <XAxis
            dataKey="t"
            type="number"
            domain={[0, 'dataMax']}
            tickFormatter={(v: number) => `${Math.round(v)}s`}
            stroke="var(--text-faint)"
            tickLine={false}
            axisLine={{ stroke: 'var(--border)' }}
            fontSize={11}
            minTickGap={28}
          />
          <YAxis
            tickFormatter={(v: number) => (v === 0 ? '0' : formatRate(v, units))}
            stroke="var(--text-faint)"
            tickLine={false}
            axisLine={false}
            fontSize={11}
            width={84}
            tickCount={3}
          />
          <Tooltip
            cursor={{ stroke: 'var(--text-faint)', strokeDasharray: '3 3' }}
            contentStyle={{
              background: 'var(--bg-panel)',
              border: '1px solid var(--border-strong)',
              borderRadius: 'var(--radius-sm)',
              fontSize: '0.85rem',
            }}
            labelStyle={{ color: 'var(--text-dim)' }}
            labelFormatter={(label) => `${Number(label).toFixed(1)}s`}
            formatter={(value) => [formatRate(Number(value), units), tone === 'up' ? 'Upload' : 'Download']}
          />
          {average !== undefined && average > 0 && (
            <ReferenceLine
              y={average}
              stroke="var(--text-dim)"
              strokeDasharray="4 4"
              ifOverflow="extendDomain"
            />
          )}
          <Area
            type="monotone"
            dataKey="bps"
            stroke={colour}
            strokeWidth={2}
            fill={`url(#speed-fill-${tone})`}
            isAnimationActive={false}
            dot={false}
            activeDot={{ r: 4, stroke: 'var(--bg-panel)', strokeWidth: 2 }}
          />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}

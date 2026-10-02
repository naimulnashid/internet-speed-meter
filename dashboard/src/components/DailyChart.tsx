'use client';

import {
  Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { formatDayShort, formatRate, type Units } from '@/lib/format';

export interface DailyBar {
  day: number;
  peakDownBps: number;
  peakUpBps: number;
}

/** Daily peak, down and up side by side rather than stacked — two directions, not two parts of a whole. */
export function DailyChart({ bars, units }: { bars: DailyBar[]; units: Units }) {
  return (
    <div style={{ width: '100%', height: 250 }}>
      <ResponsiveContainer>
        <BarChart data={bars} margin={{ top: 8, right: 8, bottom: 0, left: 8 }}>
          <CartesianGrid stroke="var(--border)" vertical={false} />
          <XAxis
            dataKey="day"
            tickFormatter={(v: number) => formatDayShort(v)}
            stroke="var(--text-faint)"
            tickLine={false}
            axisLine={{ stroke: 'var(--border)' }}
            fontSize={12}
            minTickGap={24}
          />
          <YAxis
            tickFormatter={(v: number) => formatRate(v, units)}
            stroke="var(--text-faint)"
            tickLine={false}
            axisLine={false}
            fontSize={12}
            width={78}
          />
          <Tooltip
            cursor={{ fill: 'rgba(255,255,255,0.04)' }}
            contentStyle={{
              background: 'var(--bg-panel)',
              border: '1px solid var(--border-strong)',
              borderRadius: 'var(--radius-sm)',
              fontSize: '0.9rem',
            }}
            labelStyle={{ color: 'var(--text-dim)' }}
            labelFormatter={(label) => formatDayShort(Number(label))}
            formatter={(value, name) => [formatRate(Number(value), units), String(name)]}
          />
          <Bar dataKey="peakDownBps" name="Peak down" fill="var(--down)" radius={[3, 3, 0, 0]} isAnimationActive={false} maxBarSize={46} />
          <Bar dataKey="peakUpBps" name="Peak up" fill="var(--up)" radius={[3, 3, 0, 0]} isAnimationActive={false} maxBarSize={46} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

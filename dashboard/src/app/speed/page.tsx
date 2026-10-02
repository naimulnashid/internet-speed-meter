import { Shell } from '@/components/Shell';
import { Card, CardTitle } from '@/components/Card';
import { DailyChart } from '@/components/DailyChart';
import { RangeSelect } from '@/components/RangeSelect';
import { readMinutes } from '@/lib/logs';
import { meterSettings } from '@/lib/settings';
import { byDay } from '@/lib/history';
import { peaks, type Peak } from '@/lib/stats';
import { rawStats } from '@/lib/rawstats';
import { ALL_DAYS, parseDays, windowFor } from '@/lib/scope';
import {
  formatRate, formatStamp, splitRate, type Units,
} from '@/lib/format';

export const dynamic = 'force-dynamic';

function peakWhen(peak: Peak): string {
  return peak.time === null ? 'never observed' : formatStamp(peak.time);
}

function StatCard({
  label, bytesPerSecond, units, tone, sub, delay,
}: {
  label: string;
  bytesPerSecond: number;
  units: Units;
  tone?: 'down' | 'up';
  sub?: string;
  delay: number;
}) {
  const { value, unit } = splitRate(bytesPerSecond, units);
  return (
    <Card delay={delay}>
      <div className="stat-label">{label}</div>
      <div className={`stat-value${tone ? ` stat-value--${tone}` : ''}`}>
        {value}
        <span className="stat-unit">{unit}</span>
      </div>
      {sub && <div className="stat-sub">{sub}</div>}
    </Card>
  );
}

export default async function SpeedPage({
  searchParams,
}: {
  searchParams: Promise<{ days?: string }>;
}) {
  const sp = await searchParams;
  const days = parseDays(sp.days);

  const settings = meterSettings();
  const units: Units = settings.units;

  // The fastest seconds are records, so they are taken over all history. Only
  // the daily peak chart follows the range dropdown, which sits on that chart.
  const all = windowFor(ALL_DAYS);
  const records = readMinutes(all.from, all.to);
  const best = peaks(records);

  const { from } = windowFor(days);
  const daily = byDay(records).filter((d) => d.day >= from.getTime());

  // Today's figures sit beside the all-time ones so a slow day shows up against
  // the best the connection has done.
  const now = new Date();
  const today = peaks(readMinutes(new Date(now.getFullYear(), now.getMonth(), now.getDate()), now));

  // The sustained windows need individual seconds, which exist only for as long
  // as the raw log is retained, so they cover the retained days only.
  const raw = rawStats(all.from, all.to);

  if (records.length === 0) {
    return (
      <Shell>
        <div className="empty">
          <h2>Nothing recorded yet</h2>
          <p>Give the meter a minute to write its first rollup.</p>
        </div>
      </Shell>
    );
  }

  return (
    <Shell>
      <div className="stack">
        <div className="grid grid--4">
          <StatCard
            label="Today’s fastest second down"
            bytesPerSecond={today.down.bytesPerSecond}
            units={units}
            tone="down"
            sub={peakWhen(today.down)}
            delay={0}
          />
          <StatCard
            label="Today’s fastest second up"
            bytesPerSecond={today.up.bytesPerSecond}
            units={units}
            tone="up"
            sub={peakWhen(today.up)}
            delay={60}
          />
          <StatCard
            label="Fastest second down"
            bytesPerSecond={best.down.bytesPerSecond}
            units={units}
            tone="down"
            sub={peakWhen(best.down)}
            delay={120}
          />
          <StatCard
            label="Fastest second up"
            bytesPerSecond={best.up.bytesPerSecond}
            units={units}
            tone="up"
            sub={peakWhen(best.up)}
            delay={180}
          />
        </div>

        {raw.empty ? (
          <div className="note">
            <strong>No second-by-second data.</strong> The best sustained averages
            are computed from the raw log, which is kept for{' '}
            {settings.rawFolder ? 'the retention period only' : 'nothing — raw recording is switched off'}.
          </div>
        ) : (
          <div className="grid grid--3">
            {raw.sustainedDown.map((s, i) => (
              <Card key={s.window} delay={i * 60}>
                <div className="stat-label">
                  Best sustained {s.window < 60 ? `${s.window} s` : `${s.window / 60} min`}
                </div>
                <div className="stat-value stat-value--down">
                  {formatRate(s.bytesPerSecond, units)}
                </div>
                <div className="stat-sub">
                  {s.time === null ? 'no unbroken window that long' : formatStamp(s.time)}
                </div>
              </Card>
            ))}
          </div>
        )}

        <Card hover={false}>
          <CardTitle
            sub="The fastest single second each day."
            aside={<RangeSelect days={days} />}
          >
            Daily peak
          </CardTitle>
          {daily.length === 0 ? (
            <div className="note">Nothing recorded in this window. Pick a longer range.</div>
          ) : (
            <>
              <DailyChart bars={daily} units={units} />
              <div className="legend legend--below">
                <span><i className="swatch" style={{ background: 'var(--down)' }} /> Down</span>
                <span><i className="swatch" style={{ background: 'var(--up)' }} /> Up</span>
              </div>
            </>
          )}
        </Card>

        <div className="note">
          <strong>These are observed speeds, not a line rate.</strong> The meter records only
          what actually crossed the adapter, so a peak is the fastest thing that happened to
          run — never proof the connection could not go faster.
        </div>
      </div>
    </Shell>
  );
}

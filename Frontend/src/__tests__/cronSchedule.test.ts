import { describe, expect, it } from 'vitest';
import { buildCron, parseCron, describeCron, type CronSpec } from '../utils/cronSchedule';

const base: CronSpec = { freq: 'daily', every: 5, minute: 0, hour: 8, dow: 1, dom: 1 };

describe('cronSchedule builder', () => {
  it('builds the expected cron for each frequency', () => {
    expect(buildCron({ ...base, freq: 'minutes', every: 15 })).toBe('*/15 * * * *');
    expect(buildCron({ ...base, freq: 'hourly', minute: 30 })).toBe('30 * * * *');
    expect(buildCron({ ...base, freq: 'daily', hour: 8, minute: 0 })).toBe('0 8 * * *');
    expect(buildCron({ ...base, freq: 'weekdays', hour: 9, minute: 15 })).toBe('15 9 * * 1-5');
    expect(buildCron({ ...base, freq: 'weekly', dow: 2, hour: 0, minute: 0 })).toBe('0 0 * * 2');
    expect(buildCron({ ...base, freq: 'monthly', dom: 1, hour: 6, minute: 45 })).toBe('45 6 1 * *');
  });

  it('clamps out-of-range values instead of emitting invalid cron', () => {
    expect(buildCron({ ...base, freq: 'daily', hour: 99, minute: -3 })).toBe('0 23 * * *');
    expect(buildCron({ ...base, freq: 'minutes', every: 0 })).toBe('*/1 * * * *');
  });

  it('round-trips build → parse for every frequency', () => {
    const specs: CronSpec[] = [
      { ...base, freq: 'minutes', every: 10 },
      { ...base, freq: 'hourly', minute: 5 },
      { ...base, freq: 'daily', hour: 8, minute: 0 },
      { ...base, freq: 'weekdays', hour: 9, minute: 30 },
      { ...base, freq: 'weekly', dow: 3, hour: 7, minute: 0 },
      { ...base, freq: 'monthly', dom: 15, hour: 12, minute: 0 },
    ];
    for (const spec of specs) {
      const parsed = parseCron(buildCron(spec));
      expect(parsed?.freq).toBe(spec.freq);
      expect(buildCron(parsed as CronSpec)).toBe(buildCron(spec));
    }
  });

  it('parses the default node cron (Tuesday midnight)', () => {
    expect(parseCron('0 0 * * 2')).toMatchObject({ freq: 'weekly', dow: 2, hour: 0, minute: 0 });
  });

  it('returns null for custom expressions the simple builder cannot represent', () => {
    expect(parseCron('0 8 * 3 *')).toBeNull(); // specific month
    expect(parseCron('0,30 8 * * *')).toBeNull(); // list of minutes
    expect(parseCron('0 8 * * 1,3,5')).toBeNull(); // list of weekdays
    expect(parseCron('not a cron')).toBeNull();
    expect(parseCron('')).toBeNull();
  });

  it('describes recognized schedules and gives up on custom ones', () => {
    expect(describeCron('15 9 * * 1-5')).toBe('Every weekday (Mon–Fri) at 09:15');
    expect(describeCron('0 8 * * *')).toBe('Every day at 08:00');
    expect(describeCron('*/5 * * * *')).toBe('Every 5 minutes');
    expect(describeCron('0 8 * * 1,3,5')).toBeNull();
  });
});

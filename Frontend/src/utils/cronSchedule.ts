// Pure helpers for the Cron Scheduler's "simple" builder: convert between a friendly schedule spec and a
// standard 5-field cron expression (minute hour day-of-month month day-of-week). Day-of-week matches the
// backend (Cronos): Sunday=0 … Saturday=6, so weekdays are 1-5. Kept dependency-free and pure so the
// builder round-trips reliably and can be unit-tested without React.

export type CronFreq = 'minutes' | 'hourly' | 'daily' | 'weekdays' | 'weekly' | 'monthly';

export interface CronSpec {
  freq: CronFreq;
  /** Interval in minutes for freq='minutes'. */
  every: number;
  /** 0-59, used by hourly/daily/weekdays/weekly/monthly. */
  minute: number;
  /** 0-23, used by daily/weekdays/weekly/monthly. */
  hour: number;
  /** 0 (Sunday) – 6 (Saturday), used by weekly. */
  dow: number;
  /** 1-31, used by monthly. */
  dom: number;
}

export const DEFAULT_CRON_SPEC: CronSpec = {
  freq: 'daily',
  every: 5,
  minute: 0,
  hour: 8,
  dow: 1,
  dom: 1,
};

export const WEEKDAY_LABELS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export const FREQ_OPTIONS: { value: CronFreq; label: string }[] = [
  { value: 'minutes', label: 'Every N minutes' },
  { value: 'hourly', label: 'Every hour' },
  { value: 'daily', label: 'Every day' },
  { value: 'weekdays', label: 'Every weekday (Mon–Fri)' },
  { value: 'weekly', label: 'Every week' },
  { value: 'monthly', label: 'Every month' },
];

const clamp = (value: number, min: number, max: number): number =>
  Number.isFinite(value) ? Math.min(Math.max(Math.trunc(value), min), max) : min;

/** Build a 5-field cron expression from a friendly spec. */
export function buildCron(spec: CronSpec): string {
  const minute = clamp(spec.minute, 0, 59);
  const hour = clamp(spec.hour, 0, 23);
  switch (spec.freq) {
    case 'minutes':
      return `*/${clamp(spec.every, 1, 59)} * * * *`;
    case 'hourly':
      return `${minute} * * * *`;
    case 'daily':
      return `${minute} ${hour} * * *`;
    case 'weekdays':
      return `${minute} ${hour} * * 1-5`;
    case 'weekly':
      return `${minute} ${hour} * * ${clamp(spec.dow, 0, 6)}`;
    case 'monthly':
      return `${minute} ${hour} ${clamp(spec.dom, 1, 31)} * *`;
  }
}

/**
 * Parse a cron expression back into a friendly spec, or null when it doesn't match one of the simple
 * patterns the builder produces (i.e. it's a "custom" expression that must be edited as raw cron).
 */
export function parseCron(expression: string | undefined | null): CronSpec | null {
  if (!expression) return null;
  const parts = expression.trim().split(/\s+/);
  if (parts.length !== 5) return null;
  const [min, hr, dom, month, dow] = parts;
  if (month !== '*') return null;

  const base = { ...DEFAULT_CRON_SPEC };
  const isNum = (s: string) => /^\d+$/.test(s);

  // Every N minutes: */N * * * *
  const everyMatch = min.match(/^\*\/(\d+)$/);
  if (everyMatch && hr === '*' && dom === '*' && dow === '*') {
    return { ...base, freq: 'minutes', every: clamp(Number(everyMatch[1]), 1, 59) };
  }

  // Hourly: M * * * *
  if (isNum(min) && hr === '*' && dom === '*' && dow === '*') {
    return { ...base, freq: 'hourly', minute: clamp(Number(min), 0, 59) };
  }

  if (!isNum(min) || !isNum(hr)) return null;
  const minute = clamp(Number(min), 0, 59);
  const hour = clamp(Number(hr), 0, 23);

  // Weekdays: M H * * 1-5
  if (dom === '*' && dow === '1-5') {
    return { ...base, freq: 'weekdays', minute, hour };
  }
  // Daily: M H * * *
  if (dom === '*' && dow === '*') {
    return { ...base, freq: 'daily', minute, hour };
  }
  // Weekly: M H * * D
  if (dom === '*' && isNum(dow)) {
    return { ...base, freq: 'weekly', minute, hour, dow: clamp(Number(dow), 0, 6) };
  }
  // Monthly: M H D * *
  if (isNum(dom) && dow === '*') {
    return { ...base, freq: 'monthly', minute, hour, dom: clamp(Number(dom), 1, 31) };
  }

  return null;
}

const two = (n: number) => String(n).padStart(2, '0');

/** Short human summary of a cron expression for the preview line. Returns null for unrecognized cron. */
export function describeCron(expression: string | undefined | null): string | null {
  const spec = parseCron(expression);
  if (!spec) return null;
  const time = `${two(spec.hour)}:${two(spec.minute)}`;
  switch (spec.freq) {
    case 'minutes':
      return `Every ${spec.every} minute${spec.every === 1 ? '' : 's'}`;
    case 'hourly':
      return `Every hour at :${two(spec.minute)}`;
    case 'daily':
      return `Every day at ${time}`;
    case 'weekdays':
      return `Every weekday (Mon–Fri) at ${time}`;
    case 'weekly':
      return `Every ${WEEKDAY_LABELS[spec.dow]} at ${time}`;
    case 'monthly':
      return `Every month on day ${spec.dom} at ${time}`;
  }
}

/**
 * Common IANA time zones for the combobox suggestions. The field still accepts any typed value, and when
 * the runtime exposes the full zone list ({@link Intl.supportedValuesOf}) the caller merges that in.
 */
export const COMMON_TIMEZONES = [
  'UTC',
  'Europe/Berlin',
  'Europe/London',
  'Europe/Paris',
  'Europe/Madrid',
  'Europe/Rome',
  'Europe/Moscow',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'America/Sao_Paulo',
  'Asia/Dubai',
  'Asia/Kolkata',
  'Asia/Singapore',
  'Asia/Shanghai',
  'Asia/Tokyo',
  'Australia/Sydney',
  'Pacific/Auckland',
];

/** All IANA zones when the runtime supports it, otherwise the curated common list. */
export function listTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  if (typeof intl.supportedValuesOf === 'function') {
    try {
      const all = intl.supportedValuesOf('timeZone');
      if (Array.isArray(all) && all.length > 0) return all;
    } catch {
      // fall through to the curated list
    }
  }
  return COMMON_TIMEZONES;
}

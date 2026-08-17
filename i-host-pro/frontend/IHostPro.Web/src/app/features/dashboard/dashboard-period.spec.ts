import { describe, expect, it, afterEach } from 'vitest';

// This project's browser tsconfig has no Node type definitions (correctly —
// this is application code). Vitest itself runs on Node, so `process.env.TZ`
// is genuinely available at test-run time; this ambient declaration only
// tells the compiler that, without pulling in @types/node as a new
// dependency for a single test file.
declare const process: { env: Record<string, string | undefined> };

import {
  DASHBOARD_MAX_WINDOW_DAYS,
  addDays,
  customPeriod,
  isDashboardPeriodError,
  last30DaysPeriod,
  last7DaysPeriod,
  parseLocalDateInputValue,
  startOfLocalDay,
  todayPeriod,
} from './dashboard-period';

describe('dashboard-period', () => {
  describe('todayPeriod', () => {
    it('returns [start of today local, start of tomorrow local)', () => {
      const now = new Date(2026, 7, 17, 23, 45, 0); // 2026-08-17 23:45 local
      const period = todayPeriod(now);

      expect(period.from).toEqual(new Date(2026, 7, 17, 0, 0, 0, 0));
      expect(period.to).toEqual(new Date(2026, 7, 18, 0, 0, 0, 0));
    });

    it('never rolls over to UTC midnight for a local time that would differ from UTC', () => {
      // 2026-08-17 00:30 local — a UTC-midnight-based implementation, run in
      // a timezone ahead of UTC, would incorrectly report 2026-08-16 as
      // "today" once converted; a correct local-component implementation
      // never does, regardless of the machine's own timezone.
      const now = new Date(2026, 7, 17, 0, 30, 0);
      const period = todayPeriod(now);

      expect(period.from.getFullYear()).toBe(2026);
      expect(period.from.getMonth()).toBe(7);
      expect(period.from.getDate()).toBe(17);
    });
  });

  describe('last7DaysPeriod', () => {
    it('covers exactly 7 full local calendar days ending today, inclusive', () => {
      const now = new Date(2026, 7, 17, 10, 0, 0);
      const period = last7DaysPeriod(now);

      expect(period.from).toEqual(new Date(2026, 7, 11, 0, 0, 0, 0));
      expect(period.to).toEqual(new Date(2026, 7, 18, 0, 0, 0, 0));
      expect((period.to.getTime() - period.from.getTime()) / 86_400_000).toBe(7);
    });
  });

  describe('last30DaysPeriod', () => {
    it('covers exactly 30 full local calendar days ending today, inclusive', () => {
      const now = new Date(2026, 7, 17, 10, 0, 0);
      const period = last30DaysPeriod(now);

      expect(period.from).toEqual(new Date(2026, 6, 19, 0, 0, 0, 0));
      expect(period.to).toEqual(new Date(2026, 7, 18, 0, 0, 0, 0));
      expect((period.to.getTime() - period.from.getTime()) / 86_400_000).toBe(30);
    });
  });

  describe('customPeriod', () => {
    it('treats both bounds as inclusive local calendar days', () => {
      const result = customPeriod(new Date(2026, 7, 11), new Date(2026, 7, 17));
      expect(isDashboardPeriodError(result)).toBe(false);
      const period = result as { from: Date; to: Date };
      expect(period.from).toEqual(new Date(2026, 7, 11, 0, 0, 0, 0));
      expect(period.to).toEqual(new Date(2026, 7, 18, 0, 0, 0, 0));
    });

    it('accepts a single-day range (from === to)', () => {
      const result = customPeriod(new Date(2026, 7, 17), new Date(2026, 7, 17));
      expect(isDashboardPeriodError(result)).toBe(false);
    });

    it('rejects to before from', () => {
      const result = customPeriod(new Date(2026, 7, 17), new Date(2026, 7, 16));
      expect(result).toBe('invalidRange');
    });

    it(`accepts a window of exactly ${DASHBOARD_MAX_WINDOW_DAYS} days`, () => {
      const from = new Date(2026, 0, 1);
      const to = addDays(from, DASHBOARD_MAX_WINDOW_DAYS - 1); // inclusive-day math: 100 inclusive days
      const result = customPeriod(from, to);
      expect(isDashboardPeriodError(result)).toBe(false);
    });

    it(`rejects a window of ${DASHBOARD_MAX_WINDOW_DAYS + 1} days`, () => {
      const from = new Date(2026, 0, 1);
      const to = addDays(from, DASHBOARD_MAX_WINDOW_DAYS);
      const result = customPeriod(from, to);
      expect(result).toBe('windowTooLarge');
    });
  });

  describe('startOfLocalDay', () => {
    it('zeroes the time-of-day components without changing the calendar date', () => {
      expect(startOfLocalDay(new Date(2026, 7, 17, 23, 59, 59, 999))).toEqual(new Date(2026, 7, 17, 0, 0, 0, 0));
    });
  });

  describe('parseLocalDateInputValue', () => {
    it('parses a native date-input value as a local date, not UTC midnight', () => {
      const parsed = parseLocalDateInputValue('2026-08-17');
      expect(parsed).toEqual(new Date(2026, 7, 17, 0, 0, 0, 0));
    });

    it('returns undefined for an empty or malformed value', () => {
      expect(parseLocalDateInputValue('')).toBeUndefined();
      expect(parseLocalDateInputValue('not-a-date')).toBeUndefined();
    });
  });

  describe('timezone independence (mandate §34)', () => {
    const originalTz = process.env['TZ'];

    afterEach(() => {
      if (originalTz === undefined) delete process.env['TZ'];
      else process.env['TZ'] = originalTz;
    });

    it('produces a different UTC instant for the same local wall-clock date under a different process timezone', () => {
      process.env['TZ'] = 'America/Sao_Paulo';
      const saoPauloFrom = todayPeriod(new Date(2026, 7, 17, 10, 0, 0)).from;

      process.env['TZ'] = 'Asia/Tokyo';
      const tokyoFrom = todayPeriod(new Date(2026, 7, 17, 10, 0, 0)).from;

      // Same local calendar date (2026-08-17) in both cases, but a genuinely
      // local (never UTC-hardcoded) implementation produces two different
      // UTC instants for it, one per process timezone.
      expect(saoPauloFrom.getTime()).not.toBe(tokyoFrom.getTime());
      expect(saoPauloFrom.getFullYear()).toBe(2026);
      expect(saoPauloFrom.getMonth()).toBe(7);
      expect(saoPauloFrom.getDate()).toBe(17);
    });
  });
});

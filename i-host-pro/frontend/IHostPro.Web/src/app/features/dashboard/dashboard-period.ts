/**
 * Pure local-day boundary computation for the Dashboard Overview period
 * selector (Fase 7, Incremento 2, Checkpoint 3). Every function reads/writes
 * `Date` fields via the local-time accessors (`getFullYear`/`getMonth`/
 * `getDate`, the `Date(y, m, d)` constructor) — never `Date.UTC`/`toISOString`
 * parsing of a date-only string, which yields UTC midnight, not local
 * midnight (mandate §9/§34: "não usar UTC midnight como equivalente a hoje
 * local"). The backend's own [from,to) semantics (Fase 7, Incremento 2,
 * Checkpoint 2) are preserved: `to` is always the exclusive start of the day
 * AFTER the last included day.
 */

export const DASHBOARD_MAX_WINDOW_DAYS = 100;

export type DashboardPeriodPreset = 'today' | 'last7' | 'last30' | 'custom';

export interface DashboardPeriod {
  readonly from: Date;
  readonly to: Date;
}

export type DashboardPeriodError = 'invalidRange' | 'windowTooLarge';

export function startOfLocalDay(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

export function addDays(date: Date, days: number): Date {
  const result = new Date(date);
  result.setDate(result.getDate() + days);
  return result;
}

/** [start of today (local), start of tomorrow (local)). */
export function todayPeriod(now: Date): DashboardPeriod {
  const from = startOfLocalDay(now);
  return { from, to: addDays(from, 1) };
}

/** The 7 full local calendar days ending today, inclusive: [today-6, tomorrow). */
export function last7DaysPeriod(now: Date): DashboardPeriod {
  const todayStart = startOfLocalDay(now);
  return { from: addDays(todayStart, -6), to: addDays(todayStart, 1) };
}

/** The 30 full local calendar days ending today, inclusive: [today-29, tomorrow). */
export function last30DaysPeriod(now: Date): DashboardPeriod {
  const todayStart = startOfLocalDay(now);
  return { from: addDays(todayStart, -29), to: addDays(todayStart, 1) };
}

/**
 * A user-selected custom range, both bounds inclusive calendar days (the
 * `to` day itself is meant to be included, so the resulting boundary is the
 * start of the day AFTER it). Returns a `DashboardPeriodError` instead of
 * throwing — callers render it as inline validation feedback (mandate §10:
 * "não permitir selecionar janela >100 dias sem feedback").
 */
export function customPeriod(fromDate: Date, toDate: Date): DashboardPeriod | DashboardPeriodError {
  const from = startOfLocalDay(fromDate);
  const to = addDays(startOfLocalDay(toDate), 1);

  if (to <= from) return 'invalidRange';

  const windowDays = (to.getTime() - from.getTime()) / (24 * 60 * 60 * 1000);
  if (windowDays > DASHBOARD_MAX_WINDOW_DAYS) return 'windowTooLarge';

  return { from, to };
}

export function isDashboardPeriodError(value: DashboardPeriod | DashboardPeriodError): value is DashboardPeriodError {
  return typeof value === 'string';
}

/**
 * Parses a native `<input type="date">` value (`"YYYY-MM-DD"`) as a LOCAL
 * date — deliberately NOT `new Date(value)`, which the JS spec parses as UTC
 * midnight for a date-only ISO string (the exact trap this checkpoint's
 * mandate warns against for "hoje").
 */
export function parseLocalDateInputValue(value: string): Date | undefined {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return undefined;
  const [, year, month, day] = match;
  return new Date(Number(year), Number(month) - 1, Number(day));
}

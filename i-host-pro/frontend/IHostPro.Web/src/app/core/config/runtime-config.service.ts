import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import type { RuntimeConfig } from './runtime-config.model';

/**
 * Loads and validates `public/config.json` exactly once, before the rest of
 * the application bootstraps (wired via `provideAppInitializer` in
 * `app.config.ts`). Failing to load or an invalid/missing `apiBaseUrl` fails
 * bootstrap loudly — this app never falls back to a guessed or hardcoded
 * API URL.
 */
@Injectable({ providedIn: 'root' })
export class RuntimeConfigService {
  private readonly http = inject(HttpClient);
  private readonly configSignal = signal<RuntimeConfig | null>(null);

  async load(): Promise<void> {
    const raw = await firstValueFrom(this.http.get<unknown>('/config.json'));
    const config = this.validate(raw);
    this.configSignal.set(config);
  }

  /** Throws if called before `load()` has completed successfully — every consumer runs after bootstrap, so this should never happen in practice. */
  get config(): RuntimeConfig {
    const value = this.configSignal();
    if (!value) {
      throw new Error('RuntimeConfigService.config accessed before load() completed.');
    }
    return value;
  }

  private validate(raw: unknown): RuntimeConfig {
    if (typeof raw !== 'object' || raw === null) {
      throw new Error('config.json must contain a JSON object.');
    }

    const apiBaseUrl = (raw as Record<string, unknown>)['apiBaseUrl'];
    if (typeof apiBaseUrl !== 'string' || apiBaseUrl.trim().length === 0) {
      throw new Error('config.json: "apiBaseUrl" is required and must be a non-empty string.');
    }

    return { apiBaseUrl };
  }
}

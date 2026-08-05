/**
 * Shape of `public/config.json` — the ONLY source of environment-specific
 * values this application reads at runtime. Loaded and validated before
 * bootstrap (see `runtime-config.service.ts`), so it can be swapped per
 * deployment without recompiling the application.
 *
 * Must never contain secrets, tokens, credentials or tenant-specific data —
 * this file is a public, static asset served as-is by the web server.
 */
export interface RuntimeConfig {
  /** Base URL of the iHostPro API this app talks to (e.g. "http://localhost:5140"). Required. */
  apiBaseUrl: string;
}

# Playwright executor

A real browser is required because a static SPA can still depend on JavaScript, WebAssembly, browser routing, fetch, mutation observers, storage, module loading, and framework bootstrapping.

Snapshot Protocol uses one Chromium process and a bounded pool of long-lived pages. Each worker boots the SPA once, processes routes serially, and retains useful browser caching. There is no arbitrary delay between routes.

## Browser provisioning

Browser modes:

- `InstallIfMissing`
- `RequireExisting`
- `CustomExecutable`

The installer stores a platform/version-specific completion marker only after Playwright installation succeeds. Browser launch is validated before use. A stale marker or interrupted installation triggers a reinstall in `InstallIfMissing` mode.

Browser binaries are not committed to the repository because the Playwright package and browser payloads are very large and platform-specific.

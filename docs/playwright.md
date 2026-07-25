# Playwright executor

A real browser is required because a static SPA can still depend on JavaScript, WebAssembly, browser routing, fetch, mutation observers, storage, module loading, and framework bootstrapping.

Snapshot Protocol uses one Chromium process and a bounded pool of long-lived pages. Each worker boots the SPA once, processes routes serially, and retains useful browser caching. There is no arbitrary delay between routes.

## Driver provisioning

The CLI NuGet package deliberately excludes Playwright's large platform-specific Node drivers. Before a browser operation, Snapshot Protocol resolves the exact Microsoft.Playwright package version used by `Snapshot.Playwright` and checks these locations in order:

1. an explicitly configured `PLAYWRIGHT_DRIVER_SEARCH_PATH`;
2. the application output directory;
3. the current NuGet package cache;
4. Snapshot's per-user Playwright driver cache.

If the matching driver is still unavailable, the exact Microsoft.Playwright `.nupkg` is downloaded from NuGet's stable flat-container endpoint. Only the platform-independent Playwright package files and the current operating system's Node driver are extracted. Extraction is traversal-safe, written through a temporary directory, and promoted only after the required files exist. Linux and macOS executable permissions are repaired before use.

This keeps `Snapshot.Cli` small and cross-platform without pretending that a Linux-built tool package can contain the correct Windows and macOS driver.

## Browser provisioning

Browser modes:

- `InstallIfMissing`
- `RequireExisting`
- `CustomExecutable`

Chromium is provisioned separately from the Playwright driver. The installer stores a platform/version-specific completion marker only after browser installation succeeds and a real launch completes. A stale marker, missing executable, or interrupted installation triggers repair in `InstallIfMissing` mode.

Neither Playwright drivers nor browser binaries are committed to the repository. They are platform-specific and far too large to treat as source files.

# Protocol contract

Snapshot Protocol separates semantic readiness from browser execution.

The application client owns navigation, readiness waiting, DOM capture, metadata replacement, and structured success or failure. An executor owns browser lifecycle, route scheduling, retries, artifact generation, and validation.

## Identifiers

| Purpose | Value |
|---|---|
| Script ID | `snapshot-protocol` |
| Activation query | `snapshot-protocol=<session-token>` |
| Readiness element | `<snapshot-ready>` |
| Navigate message | `snapshot:navigate` |
| Result message | `snapshot:result` |
| Error message | `snapshot:error` |
| Site version metadata | `snapshot:site-version` |
| Captured route metadata | `snapshot:route` |

Every navigation contains a session token and request ID. Results are accepted only when the worker, session, request ID, and captured route match the expected request.

## Timeouts

The client owns the semantic readiness timeout and emits `snapshot:error` when readiness never appears. The executor has a slightly longer watchdog for browser crashes, frozen pages, or broken message transport.

## Root route

The root route is the protocol's one output-path exception. The developer's source `/index.html` is always preserved as the SPA or WebAssembly loader. When `/` is discovered, its hydrated snapshot is generated at `/index/index.html` instead of replacing the loader.

Root gateway generation is enabled by default and can be disabled. Disabling it omits the generated root snapshot; it never changes the root target to `/index.html`.

Generated snapshots embed their canonical application route. When a snapshot is opened through a physical folder path such as `/index/`, the browser client restores `/` in history before application hydration. Stale-version and force-origin recovery also use the embedded canonical route rather than the physical snapshot path.

See [Root gateway](root-gateway.md) for the rationale, discoverability tradeoffs, and configuration.

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

The source loader remains `/index.html`. The hydrated root snapshot maps to `/index/index.html`, allowing the loader and snapshot to coexist.

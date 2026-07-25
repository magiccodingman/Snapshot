# Browser client

The npm package is `@magiccodingman/snapshot-protocol`.

The readable source is maintained in `client/src/snapshot-protocol.js`. Release automation produces an unminified distribution file, a conservatively minified file, and a source map.

## Script attributes

- `data-site-version`: developer-controlled version used to invalidate stale snapshots.
- `data-force-origin`: when `true`, snapshot pages return through the root loader before hydration.
- `data-ready-timeout-ms`: optional default readiness timeout override.

The executor can override the timeout for an individual route.

## Readiness

Render `<snapshot-ready>` only after the page content and metadata are final. The client marks the current readiness signal consumed before soft navigation and waits for a fresh unprocessed signal.

The client uses both a mutation observer and lightweight polling so framework rendering behavior cannot silently suppress readiness detection.

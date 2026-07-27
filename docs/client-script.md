# Browser client

The npm package is `@magiccodingman/snapshot-protocol`.

The readable source is maintained in `client/src/snapshot-protocol.js`. Release automation produces an unminified distribution file, a conservatively minified file, and a source map.

## CDN usage

The minified npm build is the recommended browser client for most applications:

```html
<script
  id="snapshot-protocol"
  data-site-version="1"
  data-force-origin="false"
  src="https://cdn.jsdelivr.net/npm/@magiccodingman/snapshot-protocol@latest/dist/snapshot-protocol.min.js">
</script>
```

`@latest` follows the newest published npm release. Use an exact version when the deployed site must remain pinned until you deliberately update it:

```text
https://cdn.jsdelivr.net/npm/@magiccodingman/snapshot-protocol@0.1.0/dist/snapshot-protocol.min.js
```

The readable debugging build is available at the same URL with `snapshot-protocol.js` instead of `snapshot-protocol.min.js`. The source map is `snapshot-protocol.min.js.map`.

npm-backed jsDelivr URLs are preferred over a moving GitHub branch URL because each published npm version is an immutable release. The package also declares the minified build as its jsDelivr and UNPKG entry point, but the explicit `/dist/snapshot-protocol.min.js` path is retained in examples so the loaded artifact is obvious.

## Script attributes

- `data-site-version`: developer-controlled version used to invalidate stale snapshots.
- `data-force-origin`: when `true`, snapshot pages return through the root loader before hydration.
- `data-ready-timeout-ms`: optional default readiness timeout override.

The executor can override the timeout for an individual route.

## Readiness

Render `<snapshot-ready>` only after the page content and metadata are final. The client marks the current readiness signal consumed before soft navigation and waits for a fresh unprocessed signal.

The client uses both a mutation observer and lightweight polling so framework rendering behavior cannot silently suppress readiness detection.

# Metadata and hydration

During capture the client removes stale route metadata from `<head>`: titles, canonical links, JSON-LD, and replaceable named metadata. Charset, viewport, HTTP-equivalent metadata, base URL, stylesheets, icons, scripts, and framework resources remain.

Children of `<snapshot-ready>` are moved into `<head>`, then the readiness wrapper is removed. The client adds `snapshot:site-version` and `snapshot:route` metadata.

On ordinary snapshot startup, the same external client fetches `/index.html`, compares the live `data-site-version`, and returns through the root loader only when versions differ or `data-force-origin=true`. No duplicate inline validator script is injected.

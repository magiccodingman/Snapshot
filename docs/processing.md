# Safe processing

`Snapshot.Processing` provides default validation and conservative optimization for generated Snapshot Protocol pages. It is separate from the browser executor and operates through the processor contract defined by `Snapshot.Protocol`.

## Build flow

The normal CLI path remains one streaming build:

```text
rendered route
  -> protocol snapshot result
  -> validation and safe processing
  -> ZIP entry stream
  -> manifest hash and length
```

The source SPA or WebAssembly loader is copied directly into the archive. Processing is invoked only for successful generated route snapshots. It does not process:

- `/index.html`, the original loader
- case aliases
- missing-prefix gateways
- static assets
- Netlify or other hosting artifacts

The root route snapshot at `/index/index.html` is a generated snapshot and is therefore processed normally.

## Default validation

### Canonical URL

Every generated snapshot must contain exactly one canonical link in `<head>`:

```html
<link rel="canonical" href="https://example.com/docs/setup">
```

The canonical must:

- be an absolute HTTP or HTTPS URL
- not point to a loopback host
- not contain a query string or fragment
- have a normalized path equal to the captured Snapshot route

Snapshot intentionally trusts the origin supplied by the application. It validates route identity rather than forcing a deployment hostname.

Canonical failures are errors by default. Use `--canonical-policy warning` or `--canonical-policy off` only when the mismatch is deliberate.

### Inline JSON

Inline `application/json`, `application/ld+json`, and other `+json` script blocks are parsed with `System.Text.Json`. Invalid JSON is an error by default. Valid JSON is serialized compactly and parsed again before the result is accepted.

## Conservative minification

Processing removes representational waste. It is not an application bundler or compiler.

Default transformations:

- collapse safe HTML text-node whitespace through the parsed AngleSharp DOM
- remove ordinary HTML comments through the DOM
- preserve conditional, crawler-control, and Snapshot Protocol comments
- compact valid inline JSON and JSON-LD
- compact inline `<style>` content with conservative NUglify CSS settings

The HTML phase does not send the complete page through a second minifier parser. It mutates only ordinary text and comment nodes in the already-parsed DOM, and skips whitespace-sensitive subtrees entirely.

The processor never:

- minifies JavaScript
- changes content inside `script`, `style`, `pre`, `textarea`, `template`, `code`, SVG, MathML, or legacy preformatted elements during HTML whitespace processing
- renames variables, properties, selectors, keyframes, or custom properties
- combines or splits files
- rewrites filenames or asset references
- performs tree shaking
- modifies external JavaScript, CSS, JSON, framework files, SVGs, or images

## Verification and fallback

HTML and CSS transformations are transactional. The transformed output is parsed again before it is accepted.

HTML verification compares:

- element order and attributes
- complete inline script contents
- complete inline style contents after the explicit CSS phase
- whitespace-sensitive content, including `pre`, `textarea`, `template`, `code`, SVG, and MathML
- normalized visible body text

If the processor cannot prove that the transformed document preserves these invariants, it keeps the valid pre-minified HTML form and emits a warning identifying which protected category changed.

CSS blocks are parsed/minified and then parsed again. A block that reports an error is preserved unchanged while the rest of the snapshot continues processing. This fallback is informational rather than a build failure, and the diagnostic includes the parser detail returned by NUglify.

## CLI configuration

Processing is enabled automatically by `snapshot build`.

```bash
# Validation and default safe minification
snapshot build ./wwwroot

# Keep validation, disable every minifier
snapshot build ./wwwroot --no-minify

# Preserve ordinary HTML comments
snapshot build ./wwwroot --keep-html-comments

# Disable selected transformations
snapshot build ./wwwroot \
  --no-minify-html \
  --no-minify-inline-json \
  --no-minify-inline-css

# Relax canonical enforcement
snapshot build ./wwwroot --canonical-policy warning

# Deliberately disable the complete processing layer
snapshot build ./wwwroot --no-processing
```

`--no-minify` does not disable canonical or JSON validation. `--no-processing` is the full escape hatch.

## .NET composition

```csharp
using Snapshot.Playwright;
using Snapshot.Processing;
using Snapshot.Protocol.Build;

var engine = SnapshotEngine.CreateBuilder()
    .UsePlaywright()
    .UseStandardProcessing(options =>
    {
        options.CanonicalPolicy = SnapshotCanonicalPolicy.Error;
        options.MinifyHtml = true;
        options.RemoveHtmlComments = true;
        options.MinifyInlineJson = true;
        options.MinifyInlineCss = true;
    })
    .Build();
```

Advanced executors can implement or replace `ISnapshotProcessor`. The processor contract remains in `Snapshot.Protocol`; concrete DOM and minifier dependencies remain in `Snapshot.Processing`.

## Existing archives

The same implementation can process an existing Snapshot Protocol ZIP:

```bash
snapshot process ./site.snapshot.zip ./site.processed.snapshot.zip
```

```csharp
var processor = new SnapshotArchiveProcessor();
var result = await processor.ProcessAsync(
    "site.snapshot.zip",
    "site.processed.snapshot.zip");
```

Archive processing validates the input, streams entries into a new ZIP, rewrites only manifested snapshot entries, regenerates their hashes and lengths, writes a new manifest, validates the result, and only then publishes the destination artifact.

ZIP entries are not edited in place. This makes failures atomic and keeps the archive processor reusable for CLI, server, desktop, and future browser-backed storage implementations.

## Memory behavior

The live build does not hold the entire ZIP or all rendered HTML in memory. Source files use pooled streaming buffers, rendered strings are encoded directly into ZIP entry streams, and the manifest is serialized directly to its ZIP stream.

One rendered page still exists as a browser string and a managed string while that route is processed. Memory therefore scales primarily with browser concurrency and the largest pages being rendered, rather than total site size.

The processor contract and archive APIs do not depend on Playwright or SQLite. A future Blazor WebAssembly executor can reuse the same contracts with memory, IndexedDB, OPFS, or another browser-native backing store.

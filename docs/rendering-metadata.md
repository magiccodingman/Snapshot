# Rendering representation metadata

Snapshot Protocol artifacts identify completed prerendered representations through HTML and sitemap metadata without changing the developer's source files.

## HTML

The output copy of the root `index.html`, every generated canonical snapshot, and any developer-provided canonical HTML snapshot receive these declarations inside `<head>`:

```html
<meta name="rendering-mode" content="static-prerendered">
<meta name="snapshot-protocol" content="1">
```

Case aliases and prefix gateways do not receive these declarations. Existing matching declarations are preserved without duplication. Conflicting or structurally unsafe HTML is copied unchanged and reported with a warning.

## Sitemap XML

Rendering metadata uses the namespace:

```xml
xmlns:render="urn:snapshot-protocol:rendering:1"
```

Ordinary sitemap URLs remain eligible for prerendering by default. A developer can mark a route as client rendered:

```xml
<url>
  <loc>https://example.test/account/live</loc>
  <render:representation>client-rendered</render:representation>
</url>
```

Snapshot skips that route during sitemap discovery. Passing the same route explicitly creates an early build error because the two instructions conflict.

The output copy of each sitemap URL successfully represented by the completed artifact is annotated as:

```xml
<render:representation>static-prerendered</render:representation>
```

The legacy value `prerendered` is accepted as equivalent input and is preserved. Existing recognized declarations are never duplicated. When every URL in a local child sitemap is statically prerendered, its entry in a local sitemap index receives the same aggregate representation declaration.

Sitemap XML and HTML changes are made only in the generated ZIP. Source files remain untouched.

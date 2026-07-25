# Route discovery

The source tree is scanned recursively for `.xml` and `.xml.gz` files.

- `<urlset>` contributes each `<loc>` as a route.
- `<sitemapindex>` is recognized and ignored as a page list.
- Other XML roots are ignored.
- Malformed files whose names indicate a sitemap produce diagnostics.

Explicit routes can be combined with sitemap routes or used alone. Exact duplicates are removed. Routes differing only by case are errors because they are ambiguous across common filesystems and incompatible with case-alias generation.

Fragments are discarded. Query-string routes are rejected in the first format version.

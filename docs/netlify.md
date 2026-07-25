# Netlify

Enable Netlify artifacts with `--netlify` or `SnapshotHostingProvider.Netlify`.

Snapshot Protocol generates managed blocks inside `_headers` and `_redirects`. Existing developer content outside the markers is preserved. Rebuilding replaces only the managed block.

The generated headers suppress harmful shared ETags and disable stale caching on snapshot routes. Redirect rules use Netlify internal `200!` rewrites for canonical slash and case handling.

These files are Netlify-specific and are not presented as fixes for Azure, CloudFront, Cloudflare, or other hosts.

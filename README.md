# Snapshot Protocol

![Snapshot Protocol](128x128_compressed.png)

Snapshot Protocol turns a fully rendered static web application into a deployable ZIP whose routes contain complete HTML, route-specific metadata, and the original client application required for hydration.

It is designed for Blazor WebAssembly, React, Angular, Vue, other SPAs, static hosts, IPFS, and any environment where SSR is unavailable or undesirable but crawlers still need complete HTML.

The protocol does not guess when a page is ready. Your application declares readiness only after its final asynchronous state has rendered.

## What it produces

The archive root is the deployment root:

```text
index.html
index/
  index.html
snapshot-manifest.json
sitemap.xml
_framework/
css/
products/
  one/
    index.html
```

There is no enclosing folder inside the ZIP. You can upload it directly where supported, extract it into a web root, stream the ZIP as one object, or stream its entries one-by-one into systems such as IPFS.

The root route is the one special case: the original `/index.html` SPA/WASM loader is always preserved, while the rendered `/` snapshot is stored at `/index/index.html`. Root gateway generation is enabled by default and can be disabled with `--no-root-gateway`; disabling it never permits the loader to be overwritten.

## Quick start

Add the browser client to your application loader. Sites that deliberately want crawlers to discover the optional root gateway may also include the link shown below; Snapshot does not inject it automatically.

```html
<a href="/index/index.html" hidden aria-hidden="true" tabindex="-1">Snapshot root</a>

<script
  id="snapshot-protocol"
  data-site-version="1"
  data-force-origin="false"
  src="https://cdn.jsdelivr.net/npm/@magiccodingman/snapshot-protocol@latest/dist/snapshot-protocol.min.js">
</script>
```

The minified npm build above is the recommended browser script for most applications. `@latest` follows the newest published client release. Deployments that require a permanently pinned client can replace it with an exact version such as:

```text
https://cdn.jsdelivr.net/npm/@magiccodingman/snapshot-protocol@0.1.0/dist/snapshot-protocol.min.js
```

For debugging, use the readable distribution file by replacing `snapshot-protocol.min.js` with `snapshot-protocol.js`. See [Browser client](docs/client-script.md) for the complete client contract and CDN options.

When a route has reached its final state, render:

```html
<snapshot-ready>
  <title>Product One</title>
  <meta name="description" content="Product One description">
  <link rel="canonical" href="https://example.com/products/one">
  <script type="application/ld+json">{ "@context": "https://schema.org" }</script>
</snapshot-ready>
```

### .NET prerequisite and CLI

Snapshot currently targets .NET 10. Install the matching .NET SDK or runtime before installing and running the CLI. The repository's authoritative target framework is defined in [`Directory.Build.props`](Directory.Build.props), while project-specific requirements live in the corresponding `.csproj` files. Check those files if this README has not yet been updated for a newer target.

```bash
dotnet --version
dotnet tool install --global Snapshot.Cli
snapshot browser install
snapshot build ./publish/wwwroot --output ./site.snapshot.zip
```

The CLI package intentionally does not embed enormous platform-specific Playwright drivers or Chromium builds. Before the first browser operation it locates the exact Microsoft.Playwright driver in the NuGet cache, or downloads that matching package from NuGet's stable package endpoint into Snapshot's per-user cache. Chromium is provisioned separately, then a real browser launch is validated before rendering. Interrupted or incomplete driver and browser installations are detected and repaired.

## Safe processing

The CLI validates and safely compacts generated snapshot pages by default. Processing applies only to manifest entries marked as real snapshots. It never modifies the developer's source `/index.html`, case aliases, prefix gateways, assets, or hosting artifacts.

Default processing:

- Requires exactly one absolute HTTP or HTTPS canonical URL.
- Requires the canonical path to match the captured route. The supplied domain is trusted.
- Validates and compacts inline JSON and JSON-LD.
- Conservatively compacts inline `<style>` blocks and validates the output again.
- Conservatively collapses HTML whitespace and removes ordinary HTML comments.
- Preserves conditional, crawler-control, and Snapshot Protocol comments.
- Re-parses transformed HTML and compares structural, executable-content, sensitive-text, and visible-text invariants.
- Falls back to the unminified snapshot whenever the transformed result cannot be proven equivalent.

Snapshot processing does **not** minify JavaScript, rename identifiers or selectors, rewrite filenames, combine files, tree-shake code, optimize SVGs, or compress images. Those application-build responsibilities remain outside Snapshot.

Disable only minification while retaining validation:

```bash
snapshot build ./wwwroot --no-minify
```

Relax canonical enforcement deliberately:

```bash
snapshot build ./wwwroot --canonical-policy warning
```

Existing Snapshot archives can use the same processor:

```bash
snapshot process ./site.snapshot.zip ./site.processed.snapshot.zip
```

See [Safe processing](docs/processing.md) for the complete policy and configuration.

## .NET API

Install the executor and standard processing packages. `Snapshot.Protocol` is included transitively:

```bash
dotnet add package Snapshot.Playwright
dotnet add package Snapshot.Processing
```

```csharp
using Snapshot.Playwright;
using Snapshot.Processing;
using Snapshot.Protocol.Build;

var engine = SnapshotEngine.CreateBuilder()
    .UsePlaywright(options =>
    {
        options.Headless = true;
    })
    .UseStandardProcessing()
    .Build();

var result = await engine.BuildAsync(new SnapshotBuildRequest
{
    SourceDirectory = "./publish/wwwroot",
    OutputPath = "./site.snapshot.zip",
    Concurrency = 4
});
```

`Snapshot.Protocol` owns route discovery, casing rules, output planning, streaming ZIP creation, manifests, archive helpers, diagnostics, validation, and the processor contract. `Snapshot.Playwright` supplies the real browser executor. `Snapshot.Processing` supplies reusable validation and conservative transformation implementations.

## Archive helpers

```csharp
await using var archive = await SnapshotArchive.OpenAsync("site.snapshot.zip");

await foreach (var file in archive.EnumerateFilesAsync())
{
    Console.WriteLine(file.Path);
}

await archive.StreamFilesAsync(async file =>
{
    await destination.AddFileAsync(file.Path, file.Content, file.CancellationToken);
});

await using var completeZip = await archive.OpenArtifactReadStreamAsync();
```

You can also read one file, infer directories, inspect the manifest, safely extract the archive, find case-insensitive matches, or copy every entry through `ISnapshotFileSink`.

## Route discovery

By default Snapshot Protocol recursively scans `*.xml` and `*.xml.gz` files in the source directory:

- `<urlset>` files contribute page routes.
- `<sitemapindex>` files are recognized but are not mistaken for page lists.
- Unrelated XML is ignored.
- Explicit routes can be added through the API or repeated `--route` options.
- `robots.txt` is not used as implicit discovery logic.

Query-string routes are rejected because distinct query URLs cannot safely map to one folder-style `index.html` without an explicit future strategy.

## Casing and Windows targets

The default artifact uses case-sensitive route semantics even when built on Windows. ZIP entries can therefore preserve both canonical and compatibility paths.

For `/MyPath`, meaningful variants include:

```text
/MyPath
/myPath
/mypath
/Mypath
/MYPATH
```

For extraction or hosting on an ordinary Windows filesystem, use:

```bash
snapshot build ./wwwroot --target-filesystem windows
```

Windows mode disables physical case aliases and validates that canonical paths can coexist safely. The default system does not allow Windows filesystem limitations to reduce Linux, Netlify, IPFS, or object-storage output.

Existing developer files always win. If the source already contains an `index.html` at a generated target, Snapshot Protocol preserves it unchanged and records the decision.

## Netlify and ETags

Snapshot Protocol can generate and merge Netlify `_headers` and `_redirects` blocks:

```bash
snapshot build ./wwwroot --netlify
```

This support is explicitly Netlify-specific. Other hosts require their own provider implementations.

The hosted-site validator detects a dangerous deployment condition observed during real crawler testing: different HTML bodies being served with the same ETag.

```bash
snapshot validate-host ./site.snapshot.zip https://example.com
```

A correct ZIP does not guarantee that a host serves it correctly. Validate the deployed response behavior.

## URL paths and IPFS

For a root-hosted SPA, prefer `<base href="/">` and root-relative application paths such as `/css/site.css`. Document-relative paths such as `css/site.css` resolve beneath the current snapshot route.

IPFS subdomain gateways and DNSLink provide a proper application origin. Legacy path gateways such as `/ipfs/CID/...` do not preserve root-relative paths correctly and are not recommended for Snapshot Protocol applications.

## Branch and release model

- `main` is active upstream development.
- `release` is the protected stable source.
- npm publishing runs only for `client/**` changes and uses npm Trusted Publishing through GitHub OIDC. The client version in `client/package.json` determines whether a new package is published.
- `Snapshot.Protocol`, `Snapshot.Processing`, `Snapshot.Playwright`, and `Snapshot.Cli` have independent versions in `eng/Versions.props`. A release publishes only package versions that do not already exist on NuGet.org.
- README, license, and logo edits do not automatically create package releases. They are included the next time an intentionally versioned package is published; manual workflow runs are available for rerunning that release automation when needed.
- GitHub releases and immutable tags remain manually authored archival milestones.

See [Release process](docs/release-process.md) for the exact version properties, trusted-publishing setup, and workflow behavior.

## Documentation

- [Protocol and message contract](docs/protocol.md)
- [Root gateway](docs/root-gateway.md)
- [Browser client](docs/client-script.md)
- [Safe processing](docs/processing.md)
- [.NET API](docs/dotnet-api.md)
- [Playwright executor](docs/playwright.md)
- [CLI reference](docs/cli.md)
- [Route discovery](docs/route-discovery.md)
- [Casing and filesystems](docs/casing-and-filesystems.md)
- [ZIP archive format](docs/archive-format.md)
- [Metadata and hydration](docs/metadata-and-hydration.md)
- [Hosting and ETags](docs/hosting-and-etags.md)
- [Netlify](docs/netlify.md)
- [IPFS and URL paths](docs/ipfs-and-url-paths.md)
- [Testing](docs/testing.md)
- [Release process](docs/release-process.md)

## License

Snapshot Protocol is licensed under the GNU Affero General Public License v3.0 only. See [LICENSE](LICENSE).

# CLI reference

## .NET prerequisite

Snapshot currently targets .NET 10. Install the matching .NET SDK or runtime before installing and running the global tool:

```bash
dotnet --version
dotnet tool install --global Snapshot.Cli
```

The repository's authoritative shared target framework is defined in [`Directory.Build.props`](../Directory.Build.props). Project-specific requirements are defined in the corresponding `.csproj` files, and [`global.json`](../global.json) selects the SDK used to build the repository. Check those files if this documentation has not yet been updated for a newer target.

Main commands:

```text
snapshot build <source>
snapshot process <source.zip> <output.zip>
snapshot validate <artifact.zip>
snapshot validate-host <artifact.zip> <base-url>
snapshot inspect <artifact.zip>
snapshot extract <artifact.zip> <directory>
snapshot browser install
snapshot browser status
```

Run `snapshot help` for the complete option list. Important build overrides include output path, route inputs, discovery mode, case aliases, Windows target mode, concurrency, timeouts, retries, headed mode, custom browser executable, Netlify output, JSON reporting, and partial-artifact preservation.

The root gateway is enabled by default whenever `/` is discovered. Use `--no-root-gateway` to omit `/index/index.html` while preserving the original `/index.html` loader. See [Root gateway](root-gateway.md).

## Default processing

`snapshot build` automatically validates and safely compacts every generated route snapshot. The original source loader, redirects, gateways, and assets are not processed.

```text
--no-processing
    Disable the complete processing layer.

--no-minify
    Keep canonical and JSON validation, but disable every minifier.

--no-minify-html
    Disable safe HTML whitespace compaction.

--keep-html-comments
    Preserve ordinary HTML comments.

--no-minify-inline-json
    Preserve formatting in inline JSON and JSON-LD.

--no-minify-inline-css
    Preserve formatting in inline <style> blocks.

--canonical-policy error|warning|off
    Control canonical URL enforcement. Default: error.
```

There is intentionally no JavaScript minification option. Snapshot processing does not alter JavaScript.

See [Safe processing](processing.md) for validation, fallback, and safety guarantees.

## Processing existing archives

```bash
snapshot process ./site.snapshot.zip ./site.processed.snapshot.zip
```

The command validates the source archive, processes only manifested snapshot pages, regenerates hashes and the manifest, validates the destination, and publishes it atomically. The same processing switches used by `build` are accepted by `process`.

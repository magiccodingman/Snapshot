# .NET API

`Snapshot.Protocol` contains the reusable engine, archive model, and processor contract. `Snapshot.Playwright` configures the Chromium renderer. `Snapshot.Processing` supplies default validation, conservative minification, and archive-to-archive processing.

Important types:

- `SnapshotEngine`
- `SnapshotBuildRequest`
- `SnapshotBuildResult`
- `SnapshotDiagnostic`
- `SnapshotArchive`
- `ISnapshotRenderer`
- `ISnapshotProcessor`
- `ISnapshotFileSink`
- `StandardSnapshotProcessor`
- `SnapshotProcessingOptions`
- `SnapshotArchiveProcessor`

The API reports expected build failures as diagnostics rather than requiring applications to parse console text. Cancellation and unexpected internal failures still use normal .NET exception behavior.

The Playwright extension uses a console logger by default because library consumers commonly run it in build workflows. Supply any `ISnapshotLogger` implementation to redirect or suppress output.

## Standard build composition

```bash
dotnet add package Snapshot.Playwright
dotnet add package Snapshot.Processing
```

```csharp
using Snapshot.Playwright;
using Snapshot.Processing;
using Snapshot.Protocol.Build;

var engine = SnapshotEngine.CreateBuilder()
    .UsePlaywright()
    .UseStandardProcessing()
    .Build();
```

Concrete processing is deliberately composed outside `Snapshot.Protocol`, keeping the protocol usable by Playwright, custom server executors, tests, and a future Blazor WebAssembly executor.

## Configuration

```csharp
var engine = SnapshotEngine.CreateBuilder()
    .UsePlaywright()
    .UseStandardProcessing(options =>
    {
        options.CanonicalPolicy = SnapshotCanonicalPolicy.Error;
        options.MinifyHtml = true;
        options.RemoveHtmlComments = true;
        options.ValidateInlineJson = true;
        options.MinifyInlineJson = true;
        options.MinifyInlineCss = true;
    })
    .Build();
```

JavaScript is not processed. Setting every minification option to `false` preserves canonical and JSON validation. Setting `Enabled = false` bypasses the complete processor.

## Custom processors

`Snapshot.Protocol` provides the entry-level contract:

```csharp
public interface ISnapshotProcessor
{
    ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default);
}
```

Configure a custom implementation with:

```csharp
SnapshotEngine.CreateBuilder()
    .UsePlaywright()
    .UseProcessor(customProcessor)
    .Build();
```

The engine invokes the processor only for successful generated snapshot pages while results are flowing into the ZIP. Source files, aliases, gateways, and provider artifacts bypass it.

## Existing archives

```csharp
var processor = new SnapshotArchiveProcessor(new SnapshotProcessingOptions
{
    CanonicalPolicy = SnapshotCanonicalPolicy.Error
});

var result = await processor.ProcessAsync(
    "site.snapshot.zip",
    "site.processed.snapshot.zip");
```

The archive processor streams unchanged entries, rewrites only manifest entries whose kind is `Snapshot`, regenerates their lengths and hashes, writes a new manifest, validates the destination archive, and publishes it only after validation succeeds.

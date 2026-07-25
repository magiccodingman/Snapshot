# .NET API

`Snapshot.Protocol` contains the reusable engine and archive model. `Snapshot.Playwright` references it and configures the default Chromium renderer.

Important types:

- `SnapshotEngine`
- `SnapshotBuildRequest`
- `SnapshotBuildResult`
- `SnapshotDiagnostic`
- `SnapshotArchive`
- `ISnapshotRenderer`
- `ISnapshotFileSink`

The API reports expected build failures as diagnostics rather than requiring applications to parse console text. Cancellation and unexpected internal failures still use normal .NET exception behavior.

The Playwright extension uses a console logger by default because library consumers commonly run it in build workflows. Supply any `ISnapshotLogger` implementation to redirect or suppress output.

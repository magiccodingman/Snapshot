using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;

namespace Snapshot.Protocol.Build;

public sealed record SnapshotRouteResult(
    string Route,
    string OutputPath,
    bool Succeeded,
    int Attempts,
    TimeSpan Elapsed,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class SnapshotBuildResult
{
    public required bool Succeeded { get; init; }

    public string? OutputPath { get; init; }

    public required IReadOnlyList<SnapshotDiagnostic> Diagnostics { get; init; }

    public required IReadOnlyList<SnapshotRouteResult> Routes { get; init; }

    public SnapshotManifest? Manifest { get; init; }

    public TimeSpan Elapsed { get; init; }

    public ValueTask<SnapshotArchive> OpenArchiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OutputPath is null)
        {
            throw new InvalidOperationException("The build did not produce an archive.");
        }

        return SnapshotArchive.OpenAsync(OutputPath, cancellationToken);
    }
}

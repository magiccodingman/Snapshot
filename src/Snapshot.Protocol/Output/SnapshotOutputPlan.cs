using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Output;

public enum SnapshotGeneratedEntryKind
{
    Snapshot,
    CaseAlias,
    PrefixGateway,
    HostingArtifact
}

public sealed record SnapshotGeneratedEntry(
    string ArchivePath,
    SnapshotGeneratedEntryKind Kind,
    string? Route = null,
    string? CanonicalRoute = null,
    string? Content = null);

public sealed class SnapshotOutputPlan
{
    public required string SourceDirectory { get; init; }

    public required IReadOnlyList<SnapshotRoute> CanonicalRoutes { get; init; }

    public required IReadOnlyList<SnapshotRoute> RoutesToRender { get; init; }

    public required IReadOnlyList<SnapshotGeneratedEntry> GeneratedEntries { get; init; }

    public required IReadOnlySet<string> SourceEntries { get; init; }

    public required IReadOnlyList<SnapshotDiagnostic> Diagnostics { get; init; }

    public int AliasCount => GeneratedEntries.Count(static entry => entry.Kind == SnapshotGeneratedEntryKind.CaseAlias);

    public int GatewayCount => GeneratedEntries.Count(static entry => entry.Kind == SnapshotGeneratedEntryKind.PrefixGateway);
}

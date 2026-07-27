using Snapshot.Protocol.Protocol;

namespace Snapshot.Protocol.Manifest;

public sealed class SnapshotManifest
{
    public int FormatVersion { get; init; } = 1;

    public int ProtocolVersion { get; init; } = SnapshotProtocolConstants.ProtocolVersion;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? SiteVersion { get; init; }

    public string TargetFilesystem { get; init; } = "case-sensitive";

    public int SourceFileCount { get; init; }

    public int CanonicalRouteCount { get; init; }

    public int AliasCount { get; init; }

    public int GatewayCount { get; init; }

    public IReadOnlyList<SnapshotManifestEntry> Entries { get; init; } = [];
}

public enum SnapshotManifestEntryKind
{
    Source,
    Snapshot,
    CaseAlias,
    PrefixGateway,
    HostingArtifact
}

public sealed record SnapshotManifestEntry(
    string Path,
    SnapshotManifestEntryKind Kind,
    long Length,
    string Sha256,
    string? Route = null,
    string? CanonicalRoute = null);

namespace Snapshot.Protocol.Routes;

public enum SnapshotRouteDiscoveryMode
{
    SitemapsAndExplicit,
    SitemapsOnly,
    ExplicitOnly
}

public sealed class SnapshotRouteDiscoveryOptions
{
    public SnapshotRouteDiscoveryMode Mode { get; init; } = SnapshotRouteDiscoveryMode.SitemapsAndExplicit;

    public IReadOnlyList<string> AdditionalRoutes { get; init; } = [];
}

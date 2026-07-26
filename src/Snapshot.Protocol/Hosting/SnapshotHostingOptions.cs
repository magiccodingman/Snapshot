namespace Snapshot.Protocol.Hosting;

public enum SnapshotHostingProvider
{
    Generic,
    Netlify
}

public sealed class SnapshotHostingOptions
{
    public SnapshotHostingProvider Provider { get; init; } = SnapshotHostingProvider.Generic;
}

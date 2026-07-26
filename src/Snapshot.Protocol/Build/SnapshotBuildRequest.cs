using Snapshot.Protocol.Hosting;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Build;

public enum SnapshotTargetFilesystem
{
    CaseSensitive,
    Windows
}

public sealed class SnapshotBuildRequest
{
    public required string SourceDirectory { get; init; }

    public string OutputPath { get; init; } = "./snapshot-output.zip";

    public SnapshotTargetFilesystem TargetFilesystem { get; init; } = SnapshotTargetFilesystem.CaseSensitive;

    public SnapshotRouteDiscoveryOptions Discovery { get; init; } = new();

    public SnapshotRootGatewayOptions RootGateway { get; init; } = new();

    public SnapshotCaseAliasOptions CaseAliases { get; init; } = new();

    public SnapshotTimeoutOptions Timeouts { get; init; } = new();

    public SnapshotRetryOptions Retry { get; init; } = new();

    public SnapshotHostingOptions Hosting { get; init; } = new();

    public int? Concurrency { get; init; }

    public bool PreservePartialArtifact { get; init; }
}

public sealed class SnapshotRootGatewayOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class SnapshotCaseAliasOptions
{
    public bool Enabled { get; init; } = true;

    public bool GenerateMissingPrefixGateways { get; init; } = true;

    public int? MaximumAliasesPerRoute { get; init; }
}

public sealed class SnapshotTimeoutOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan WatchdogTimeout { get; init; } = TimeSpan.FromSeconds(40);
}

public sealed class SnapshotRetryOptions
{
    public int MaximumAttempts { get; init; } = 3;
}

using Snapshot.Protocol.Build;

namespace Snapshot.Protocol.Archive;

public sealed class SnapshotExtractionOptions
{
    public SnapshotTargetFilesystem TargetFilesystem { get; init; } = SnapshotTargetFilesystem.CaseSensitive;

    public bool OverwriteExistingFiles { get; init; }

    public bool Atomic { get; init; } = true;
}

public sealed class SnapshotStreamFilesOptions
{
    public int DegreeOfParallelism { get; init; } = 1;
}

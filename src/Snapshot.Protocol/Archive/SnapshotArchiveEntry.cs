namespace Snapshot.Protocol.Archive;

public abstract record SnapshotArchiveEntry(string Path);

public sealed record SnapshotArchiveFile(
    string Path,
    long UncompressedLength,
    long CompressedLength,
    DateTimeOffset LastModified,
    string? ContentType = null) : SnapshotArchiveEntry(Path);

public sealed record SnapshotArchiveDirectory(string Path) : SnapshotArchiveEntry(Path);

public sealed class SnapshotFileStreamContext
{
    public required SnapshotArchiveFile File { get; init; }

    public required Stream Content { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    public string Path => File.Path;
}

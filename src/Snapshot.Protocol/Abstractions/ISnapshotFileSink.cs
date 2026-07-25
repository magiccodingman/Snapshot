using Snapshot.Protocol.Archive;

namespace Snapshot.Protocol.Abstractions;

public interface ISnapshotFileSink
{
    ValueTask WriteFileAsync(
        SnapshotArchiveFile file,
        Stream content,
        CancellationToken cancellationToken);
}

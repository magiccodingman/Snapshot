using System.Text;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Processing;

public interface ISnapshotProcessor
{
    ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default);
}

public sealed record SnapshotProcessingContext(
    SnapshotRoute Route,
    string Html);

public sealed record SnapshotProcessingResult(
    string Html,
    IReadOnlyList<SnapshotDiagnostic> Diagnostics,
    long OriginalUtf8Length,
    long ProcessedUtf8Length)
{
    public static SnapshotProcessingResult Unchanged(SnapshotProcessingContext context) =>
        new(
            context.Html,
            [],
            Encoding.UTF8.GetByteCount(context.Html),
            Encoding.UTF8.GetByteCount(context.Html));
}

public sealed class PassthroughSnapshotProcessor : ISnapshotProcessor
{
    public static PassthroughSnapshotProcessor Instance { get; } = new();

    private PassthroughSnapshotProcessor()
    {
    }

    public ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(SnapshotProcessingResult.Unchanged(context));
    }
}

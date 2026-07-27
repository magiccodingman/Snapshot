using Snapshot.Protocol.Build;

namespace Snapshot.Processing;

public static class SnapshotProcessingExtensions
{
    public static SnapshotEngineBuilder UseStandardProcessing(
        this SnapshotEngineBuilder builder,
        Action<SnapshotProcessingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new SnapshotProcessingOptions();
        configure?.Invoke(options);
        return builder.UseProcessor(SnapshotProcessorFactory.Create(options));
    }

    public static SnapshotEngineBuilder UseStandardProcessing(
        this SnapshotEngineBuilder builder,
        SnapshotProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseProcessor(SnapshotProcessorFactory.Create(options));
    }
}
